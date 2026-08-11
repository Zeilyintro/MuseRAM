using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MuseRAM.Core;

namespace MuseRAM.App;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

public static class WindowBoundsPolicy
{
    public static WindowBounds CenterAndClamp(
        WindowBounds current,
        double targetWidth,
        double targetHeight,
        WindowBounds workingArea)
    {
        var centerX = current.Left + current.Width / 2;
        var centerY = current.Top + current.Height / 2;
        var maxLeft = workingArea.Left + Math.Max(0, workingArea.Width - targetWidth);
        var maxTop = workingArea.Top + Math.Max(0, workingArea.Height - targetHeight);
        var left = Math.Clamp(centerX - targetWidth / 2, workingArea.Left, maxLeft);
        var top = Math.Clamp(centerY - targetHeight / 2, workingArea.Top, maxTop);
        return new WindowBounds(left, top, targetWidth, targetHeight);
    }
}

public static class EnhancedSafetyBehavior
{
    public static TimeSpan PostTrimSamplingDelay(bool enabled) =>
        enabled ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;

    public static TimeSpan DeepReleaseGracePeriod(bool enabled) =>
        enabled ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(900);

    public static bool RequiresForceTerminationConfirmation(bool enabled) => enabled;
}

public enum ApplicationProtectionState
{
    None,
    Partial,
    EntireFamily
}

public sealed record RunningProtectionProcess(
    int ProcessId,
    long WorkingSetBytes);

public sealed record RunningProtectionExecutable(
    string Name,
    string ExecutablePath,
    int InstanceCount,
    long WorkingSetBytes,
    bool IsProtected,
    IReadOnlyList<RunningProtectionProcess> Processes);

public sealed record RunningProtectionCandidate(
    string FamilyKey,
    string DisplayName,
    string ApplicationExecutablePath,
    long WorkingSetBytes,
    int ProcessCount,
    ApplicationProtectionState ProtectionState,
    IReadOnlyList<RunningProtectionExecutable> Executables,
    IReadOnlyList<string> MatchedRuleApplicationPaths);

public sealed record RunningProtectionSelection(
    string ApplicationExecutablePath,
    ApplicationProtectionState ProtectionState,
    IReadOnlyList<string> ProtectedExecutablePaths,
    IReadOnlyList<string> ReplacedRuleApplicationPaths);

public static class RunningProtectionCandidateCatalog
{
    public static IReadOnlyList<RunningProtectionCandidate> Create(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyList<ApplicationProtectionRule> rules)
    {
        var displayFamilies = families;
        var allProcesses = displayFamilies.SelectMany(family => family.Processes).ToArray();
        var wholeFamilyContexts = rules
            .Where(rule => rule.ProtectEntireFamily)
            .Select(rule =>
            {
                var protection = new ProtectionRules(new[] { rule });
                return new WholeFamilyRuleContext(
                    rule,
                    protection,
                    protection.CreateContext(allProcesses));
            })
            .ToArray();
        return displayFamilies
            .Select(family => CreateCandidate(family, rules, wholeFamilyContexts))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.ProtectionState == ApplicationProtectionState.EntireFamily
                    ? "protected:" + candidate.ApplicationExecutablePath
                    : "family:" + candidate.FamilyKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(MergeRelatedCandidates)
            .OrderByDescending(candidate => candidate.ProtectionState)
            .ThenByDescending(candidate => candidate.WorkingSetBytes)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.ApplicationExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ApplicationProtectionRule> MergeSelections(
        IReadOnlyList<ApplicationProtectionRule> currentRules,
        IReadOnlyList<RunningProtectionSelection> selections)
    {
        var replacedPaths = selections
            .SelectMany(selection => selection.ReplacedRuleApplicationPaths)
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = currentRules
            .Where(rule => !replacedPaths.Contains(TryNormalizePath(rule.ApplicationExecutablePath) ?? string.Empty))
            .Select(CloneRule)
            .ToList();

        foreach (var selection in selections)
        {
            if (selection.ProtectionState == ApplicationProtectionState.None) continue;
            merged.Add(new ApplicationProtectionRule
            {
                ApplicationExecutablePath = selection.ApplicationExecutablePath,
                ProtectEntireFamily = selection.ProtectionState == ApplicationProtectionState.EntireFamily,
                ProtectedExecutablePaths = selection.ProtectionState == ApplicationProtectionState.Partial
                    ? selection.ProtectedExecutablePaths.ToList()
                    : new List<string>()
            });
        }
        return merged;
    }

    private static RunningProtectionCandidate? CreateCandidate(
        ProcessFamilySnapshot family,
        IReadOnlyList<ApplicationProtectionRule> rules,
        IReadOnlyList<WholeFamilyRuleContext> wholeFamilyContexts)
    {
        var executables = family.Processes
            .Select(process => new
            {
                Process = process,
                Path = TryNormalizePath(process.ExecutablePath)
            })
            .Where(item => item.Path is not null)
            .GroupBy(item => item.Path!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Path = group.Key,
                Name = group
                    .OrderByDescending(item => item.Process.WorkingSetBytes)
                    .Select(item => item.Process.Name)
                    .First(),
                InstanceCount = group.Count(),
                WorkingSetBytes = group.Sum(item => Math.Max(0, item.Process.WorkingSetBytes)),
                Processes = group
                    .Select(item => new RunningProtectionProcess(
                        item.Process.ProcessId,
                        Math.Max(0, item.Process.WorkingSetBytes)))
                    .OrderByDescending(process => process.WorkingSetBytes)
                    .ThenBy(process => process.ProcessId)
                    .ToArray()
            })
            .OrderByDescending(executable => executable.WorkingSetBytes)
            .ThenBy(executable => executable.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (executables.Length == 0) return null;

        var executablePaths = executables
            .Select(executable => executable.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingRules = rules
            .Where(rule => RuleMatchesFamily(rule, executablePaths) ||
                           wholeFamilyContexts.Any(context =>
                               ReferenceEquals(context.Rule, rule) &&
                               context.Protection.IsProtected(family, context.Context)))
            .GroupBy(rule => TryNormalizePath(rule.ApplicationExecutablePath) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(rule => rule.ProtectEntireFamily).First())
            .ToArray();
        var wholeFamilyRule = matchingRules.FirstOrDefault(rule => rule.ProtectEntireFamily);
        var protectedPaths = wholeFamilyRule is not null
            ? executablePaths
            : matchingRules
                .SelectMany(rule => rule.ProtectedExecutablePaths ?? new List<string>())
                .Select(TryNormalizePath)
                .Where(path => path is not null && executablePaths.Contains(path))
                .Select(path => path!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = wholeFamilyRule is not null
            ? ApplicationProtectionState.EntireFamily
            : protectedPaths.Count > 0
                ? ApplicationProtectionState.Partial
                : ApplicationProtectionState.None;
        var displayExecutable = executables.FirstOrDefault(executable => string.Equals(
            NormalizeProcessName(executable.Name),
            NormalizeProcessName(family.DisplayName),
            StringComparison.OrdinalIgnoreCase));
        var matchedApplicationPath = matchingRules
            .Select(rule => TryNormalizePath(rule.ApplicationExecutablePath))
            .FirstOrDefault(path => path is not null && executablePaths.Contains(path));
        var applicationPath = wholeFamilyRule is not null
            ? TryNormalizePath(wholeFamilyRule.ApplicationExecutablePath) ?? executables[0].Path
            : displayExecutable?.Path ?? matchedApplicationPath ?? executables[0].Path;
        var entries = executables.Select(executable => new RunningProtectionExecutable(
            executable.Name,
            executable.Path,
            executable.InstanceCount,
            executable.WorkingSetBytes,
            protectedPaths.Contains(executable.Path),
            executable.Processes)).ToArray();
        var matchedRulePaths = matchingRules
            .Select(rule => TryNormalizePath(rule.ApplicationExecutablePath))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RunningProtectionCandidate(
            family.Key,
            family.DisplayName,
            applicationPath,
            family.WorkingSetBytes,
            family.Processes.Count,
            state,
            entries,
            matchedRulePaths);
    }

    private static RunningProtectionCandidate MergeRelatedCandidates(
        IGrouping<string, RunningProtectionCandidate> group)
    {
        var candidates = group.ToArray();
        if (candidates.Length == 1) return candidates[0];
        var applicationPath = candidates[0].ApplicationExecutablePath;
        var primary = candidates.FirstOrDefault(candidate => candidate.Executables.Any(executable =>
                          string.Equals(
                              executable.ExecutablePath,
                              applicationPath,
                              StringComparison.OrdinalIgnoreCase))) ?? candidates[0];
        var executables = candidates
            .SelectMany(candidate => candidate.Executables)
            .GroupBy(executable => executable.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(executablesByPath =>
            {
                var processes = executablesByPath
                    .SelectMany(executable => executable.Processes)
                    .GroupBy(process => process.ProcessId)
                    .Select(processesById => processesById
                        .OrderByDescending(process => process.WorkingSetBytes)
                        .First())
                    .OrderByDescending(process => process.WorkingSetBytes)
                    .ThenBy(process => process.ProcessId)
                    .ToArray();
                return new RunningProtectionExecutable(
                    executablesByPath
                        .OrderByDescending(executable => executable.WorkingSetBytes)
                        .Select(executable => executable.Name)
                        .First(),
                    executablesByPath.Key,
                    processes.Length,
                    processes.Sum(process => process.WorkingSetBytes),
                    executablesByPath.Any(executable => executable.IsProtected),
                    processes);
            })
            .OrderByDescending(executable => executable.WorkingSetBytes)
            .ThenBy(executable => executable.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RunningProtectionCandidate(
            primary.FamilyKey,
            primary.DisplayName,
            applicationPath,
            candidates.Sum(candidate => candidate.WorkingSetBytes),
            candidates.Sum(candidate => candidate.ProcessCount),
            ApplicationProtectionState.EntireFamily,
            executables,
            candidates
                .SelectMany(candidate => candidate.MatchedRuleApplicationPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static bool RuleMatchesFamily(
        ApplicationProtectionRule rule,
        IReadOnlySet<string> executablePaths)
    {
        var applicationPath = TryNormalizePath(rule.ApplicationExecutablePath);
        if (applicationPath is not null && executablePaths.Contains(applicationPath)) return true;
        return !rule.ProtectEntireFamily &&
               (rule.ProtectedExecutablePaths ?? new List<string>())
               .Select(TryNormalizePath)
               .Any(path => path is not null && executablePaths.Contains(path));
    }

    private static ApplicationProtectionRule CloneRule(ApplicationProtectionRule rule) => new()
    {
        ApplicationExecutablePath = rule.ApplicationExecutablePath,
        ProtectEntireFamily = rule.ProtectEntireFamily,
        ProtectedExecutablePaths = rule.ProtectedExecutablePaths?.ToList() ?? new List<string>()
    };

    private static string? TryNormalizePath(string? path) =>
        ExecutablePathIdentity.TryNormalize(path, out var normalized) ? normalized : null;

    private static string NormalizeProcessName(string name)
    {
        var normalized = name.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private sealed record WholeFamilyRuleContext(
        ApplicationProtectionRule Rule,
        ProtectionRules Protection,
        ProtectionContext Context);
}

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public SingleInstanceGuard(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Mutex name is required.", nameof(name));
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public static bool HasOlderProcess(
        string processName,
        int currentProcessId,
        DateTime currentProcessStartedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (IsOlderProcessCandidate(
                            process.Id,
                            process.StartTime.ToUniversalTime(),
                            currentProcessId,
                            currentProcessStartedAtUtc))
                    {
                        return true;
                    }
                }
                catch
                {
                    // A process can exit while the duplicate-instance check is running.
                }
            }
        }
        return false;
    }

    public static bool IsOlderProcessCandidate(
        int candidateProcessId,
        DateTime candidateStartedAtUtc,
        int currentProcessId,
        DateTime currentProcessStartedAtUtc) =>
        candidateProcessId != currentProcessId &&
        candidateStartedAtUtc < currentProcessStartedAtUtc;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}

public sealed class SingleInstanceActivation : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;
    private bool _disposed;

    public SingleInstanceActivation(string name, Action callback)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Event name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(callback);
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _event,
            static (state, timedOut) =>
            {
                if (!timedOut) ((Action)state!).Invoke();
            },
            callback,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    public static bool TrySignal(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(name);
            return activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.Unregister(null);
        _event.Dispose();
    }
}

public sealed record ActivityHistoryEntry(
    DateTimeOffset OccurredAt,
    string? ResourceKey,
    IReadOnlyList<string> Arguments,
    string? NestedResourceKey = null,
    IReadOnlyList<string>? NestedArguments = null,
    int NestedArgumentIndex = 0,
    string? LegacyText = null)
{
    public static ActivityHistoryEntry Create(
        string resourceKey,
        IEnumerable<object?>? arguments = null,
        string? nestedResourceKey = null,
        IEnumerable<object?>? nestedArguments = null,
        int nestedArgumentIndex = 0,
        DateTimeOffset? occurredAt = null) =>
        new(
            occurredAt ?? DateTimeOffset.Now,
            resourceKey,
            ToText(arguments),
            nestedResourceKey,
            ToText(nestedArguments),
            nestedArgumentIndex);

    public static ActivityHistoryEntry FromLegacy(string text) =>
        new(DateTimeOffset.MinValue, null, Array.Empty<string>(), LegacyText: text);

    public string Format(UiLanguage language)
    {
        if (!string.IsNullOrWhiteSpace(LegacyText)) return LegacyText;
        if (string.IsNullOrWhiteSpace(ResourceKey)) return string.Empty;

        var texts = UiTextCatalog.For(language);
        var arguments = Arguments?.Cast<object?>().ToArray() ?? Array.Empty<object?>();
        if (!string.IsNullOrWhiteSpace(NestedResourceKey))
        {
            var nestedFormat = texts.GetValueOrDefault(NestedResourceKey, NestedResourceKey);
            var nested = string.Format(nestedFormat, (NestedArguments ?? Array.Empty<string>()).Cast<object?>().ToArray());
            if (NestedArgumentIndex >= 0 && NestedArgumentIndex < arguments.Length)
                arguments[NestedArgumentIndex] = nested;
        }

        var format = texts.GetValueOrDefault(ResourceKey, ResourceKey);
        return $"{OccurredAt.ToLocalTime():HH:mm:ss}  {string.Format(format, arguments)}";
    }

    private static IReadOnlyList<string> ToText(IEnumerable<object?>? values) =>
        values?.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToArray() ??
        Array.Empty<string>();
}

public sealed record ActivityHistoryDocument(int SchemaVersion, IReadOnlyList<ActivityHistoryEntry> Records);

public sealed class ActivityHistoryStore
{
    private const int MaximumEntries = 30;
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ActivityHistoryStore(string? path = null)
    {
        _path = path ?? AppDataPaths.HistoryFile;
    }

    public IReadOnlyList<ActivityHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<ActivityHistoryEntry>();
            var node = JsonNode.Parse(File.ReadAllText(_path));
            var records = node switch
            {
                JsonArray legacy => legacy
                    .Select(item => item?.GetValue<string>())
                    .Where(entry => !string.IsNullOrWhiteSpace(entry))
                    .Select(entry => ActivityHistoryEntry.FromLegacy(entry!)),
                JsonObject document when document["SchemaVersion"]?.GetValue<int>() <= CurrentSchemaVersion &&
                                         document["Records"] is JsonArray entries =>
                    entries.Deserialize<List<ActivityHistoryEntry>>(JsonOptions) ?? new List<ActivityHistoryEntry>(),
                _ => Array.Empty<ActivityHistoryEntry>()
            };
            return records
                .Where(entry => !string.IsNullOrWhiteSpace(entry.LegacyText) ||
                                !string.IsNullOrWhiteSpace(entry.ResourceKey))
                .Take(MaximumEntries)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ActivityHistoryEntry>();
        }
    }

    public void Save(IEnumerable<ActivityHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var values = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.LegacyText) ||
                            !string.IsNullOrWhiteSpace(entry.ResourceKey))
            .Take(MaximumEntries)
            .ToArray();
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(new ActivityHistoryDocument(CurrentSchemaVersion, values), JsonOptions));
        File.Move(temporaryPath, _path, true);
    }
}

public sealed class BenefitLearningStore
{
    private const int MaximumEntries = 250;
    private const int MaximumLearningSamples = 100;
    internal const int CurrentSchemaVersion = 6;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private bool _writesBlocked;

    public BenefitLearningStore(string? path = null)
    {
        _path = path ?? AppDataPaths.BenefitLearningFile;
    }

    public IReadOnlyList<ApplicationBenefitLearningRecord> Load() => LoadWithStatus().Records;

    public BenefitLearningLoadResult LoadWithStatus()
    {
        if (!File.Exists(_path))
            return new BenefitLearningLoadResult(Array.Empty<ApplicationBenefitLearningRecord>(), false, null);

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_path)) ??
                throw new InvalidDataException("Benefit-learning data is empty.");
            var sourceVersion = node is JsonArray
                ? 0
                : node["SchemaVersion"] is null
                    ? 0
                    : node["SchemaVersion"]!.GetValue<int>();
            if (sourceVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Benefit-learning version {sourceVersion} is newer than supported version {CurrentSchemaVersion}.");

            var recordsNode = node switch
            {
                JsonArray array => array,
                JsonObject root when root["Records"] is JsonArray array => array,
                _ => throw new InvalidDataException("Benefit-learning data must contain a records array.")
            };
            var sourceRecords = recordsNode.Deserialize<List<ApplicationBenefitLearningRecord>>(JsonOptions) ??
                new List<ApplicationBenefitLearningRecord>();
            var records = Normalize(sourceRecords);
            if (sourceVersion < 4)
            {
                records = records.Select(record => record with
                {
                    StableWorkingSetSamplesBytes = Array.Empty<long>(),
                    StableLastObservedAt = null,
                    LastStableLaunchSignature = null
                }).ToArray();
            }
            var familyStableRecords = node is JsonObject stableRoot &&
                                      stableRoot["FamilyStableRecords"] is JsonArray stableArray
                ? stableArray.Deserialize<List<ApplicationStableLearningRecord>>(JsonOptions) ?? new()
                : new List<ApplicationStableLearningRecord>();
            var dismissedSuggestionIds = node is JsonObject documentRoot &&
                                         documentRoot["DismissedSuggestionIds"] is JsonArray dismissedArray
                ? dismissedArray.Deserialize<List<string>>(JsonOptions) ?? new List<string>()
                : new List<string>();
            dismissedSuggestionIds = MigrateSuggestionIds(dismissedSuggestionIds, sourceRecords).ToList();
            var migrated = sourceVersion < CurrentSchemaVersion;
            if (migrated)
            {
                File.Copy(_path, _path + ".bak", true);
                Save(records, dismissedSuggestionIds, familyStableRecords);
            }
            DeleteMigrationBackup();
            return new BenefitLearningLoadResult(records, migrated, null)
            {
                DismissedSuggestionIds = NormalizeSuggestionIds(dismissedSuggestionIds),
                FamilyStableRecords = NormalizeFamilyStableRecords(familyStableRecords)
            };
        }
        catch (Exception exception)
        {
            _writesBlocked = true;
            return new BenefitLearningLoadResult(
                Array.Empty<ApplicationBenefitLearningRecord>(),
                false,
                exception.Message);
        }
    }

    public void Save(
        IEnumerable<ApplicationBenefitLearningRecord> records,
        IEnumerable<string>? dismissedSuggestionIds = null,
        IEnumerable<ApplicationStableLearningRecord>? familyStableRecords = null)
    {
        if (_writesBlocked)
            throw new InvalidOperationException("Benefit-learning data cannot be saved because the existing file could not be loaded safely.");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var document = new BenefitLearningDocument(CurrentSchemaVersion, Normalize(records))
        {
            DismissedSuggestionIds = NormalizeSuggestionIds(dismissedSuggestionIds),
            FamilyStableRecords = NormalizeFamilyStableRecords(familyStableRecords)
        };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    private void DeleteMigrationBackup()
    {
        try
        {
            var backupPath = _path + ".bak";
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch
        {
            // A stale migration backup must not prevent valid learning data from loading.
        }
    }

    private static ApplicationBenefitLearningRecord[] Normalize(
        IEnumerable<ApplicationBenefitLearningRecord>? records) => (records ?? Array.Empty<ApplicationBenefitLearningRecord>())
        .Where(record => !string.IsNullOrWhiteSpace(record.FamilyKey))
        .Select(NormalizeIdentity)
        .GroupBy(
            record => string.IsNullOrWhiteSpace(record.ComponentKey) ? record.FamilyKey : record.ComponentKey!,
            StringComparer.OrdinalIgnoreCase)
        .Select(MergeRecords)
        .OrderByDescending(record => record.LastObservedAt)
        .Take(MaximumEntries)
        .ToArray();

    private static ApplicationBenefitLearningRecord NormalizeIdentity(
        ApplicationBenefitLearningRecord record)
    {
        if (InstalledApplicationIdentity.TryResolveWindowsPackage(record.ExecutablePath, out var package))
        {
            return record with
            {
                FamilyKey = package.FamilyKey,
                ComponentKey = string.IsNullOrWhiteSpace(record.ComponentKey)
                    ? null
                    : ApplicationComponentIdentity.ForExecutable(package.FamilyKey, record.ExecutablePath)
            };
        }
        if (!InstalledApplicationIdentity.TryResolveVersionedDirectory(
                record.ExecutablePath, out var versioned)) return record;
        return record with
        {
            FamilyKey = versioned.FamilyKey,
            ComponentKey = string.IsNullOrWhiteSpace(record.ComponentKey)
                ? null
                : ApplicationComponentIdentity.ForExecutable(versioned.FamilyKey, record.ExecutablePath)
        };
    }

    private static ApplicationBenefitLearningRecord MergeRecords(
        IGrouping<string, ApplicationBenefitLearningRecord> group)
    {
        var records = group.OrderBy(record => record.LastObservedAt).ToArray();
        var validRecords = records.Where(record => record.ValidSampleCount > 0).ToArray();
        var contributingRecords = validRecords.Length > 0 ? validRecords : records;
        var newest = contributingRecords[^1];
        var weights = contributingRecords
            .Select(record => Math.Max(1, validRecords.Length > 0
                ? record.ValidSampleCount
                : record.SampleCount))
            .ToArray();
        var totalWeight = weights.Sum();
        var validSampleCount = Math.Min(
            MaximumLearningSamples,
            validRecords.Sum(record => Math.Max(0, record.ValidSampleCount)));
        var sampleCount = validRecords.Length > 0
            ? validSampleCount
            : Math.Min(MaximumLearningSamples, records.Sum(record => Math.Max(0, record.SampleCount)));
        var lateWorkingSetSamples = contributingRecords
            .SelectMany(record => record.LateWorkingSetSamplesBytes ?? Array.Empty<long>())
            .Where(value => value > 0)
            .TakeLast(MaximumLearningSamples)
            .ToArray();
        var stableRecords = contributingRecords
            .Where(record => (record.StableWorkingSetSamplesBytes ?? Array.Empty<long>()).Any(value => value > 0))
            .ToArray();
        var stableWorkingSetSamples = stableRecords
            .SelectMany(record => record.StableWorkingSetSamplesBytes ?? Array.Empty<long>())
            .Where(value => value > 0)
            .TakeLast(MaximumLearningSamples)
            .ToArray();
        var latestStableRecord = stableRecords.LastOrDefault();
        var recentBackoffRate = WeightedAverage(
            contributingRecords,
            weights,
            totalWeight,
            record => record.RecentBackoffRate);
        var recentQuickReturnRate = WeightedAverage(
            contributingRecords,
            weights,
            totalWeight,
            record => record.RecentQuickReturnRate);

        return newest with
        {
            AverageOutcomeMultiplier = WeightedAverage(contributingRecords, weights, totalWeight, record => record.AverageOutcomeMultiplier),
            SampleCount = sampleCount,
            QuickReturnCount = Math.Clamp(
                (int)Math.Round(recentQuickReturnRate * sampleCount),
                0,
                sampleCount),
            AverageReleasedBytes = WeightedLong(contributingRecords, weights, totalWeight, record => record.AverageReleasedBytes),
            AverageRetainedBytes = WeightedLong(contributingRecords, weights, totalWeight, record => record.AverageRetainedBytes),
            AverageLateWorkingSetBytes = WeightedLong(contributingRecords, weights, totalWeight, record => record.AverageLateWorkingSetBytes),
            AverageReboundPercent = WeightedAverage(contributingRecords, weights, totalWeight, record => record.AverageReboundPercent),
            BackoffTriggerCount = Math.Clamp(
                (int)Math.Round(recentBackoffRate * validSampleCount),
                0,
                validSampleCount),
            DistinctLaunchCount = Math.Min(MaximumLearningSamples, contributingRecords.Sum(record => Math.Max(0, record.DistinctLaunchCount))),
            LegacySampleCount = validRecords.Length > 0
                ? 0
                : Math.Min(MaximumLearningSamples, records.Sum(record => Math.Max(record.LegacySampleCount, record.SampleCount))),
            ValidSampleCount = validSampleCount,
            RecentBackoffRate = recentBackoffRate,
            RecentQuickReturnRate = recentQuickReturnRate,
            LateWorkingSetSamplesBytes = lateWorkingSetSamples,
            StableWorkingSetSamplesBytes = stableWorkingSetSamples,
            StableLastObservedAt = latestStableRecord?.StableLastObservedAt,
            LastStableLaunchSignature = latestStableRecord?.LastStableLaunchSignature
        };
    }

    private static double WeightedAverage(
        IReadOnlyList<ApplicationBenefitLearningRecord> records,
        IReadOnlyList<int> weights,
        int totalWeight,
        Func<ApplicationBenefitLearningRecord, double> selector) =>
        records.Select((record, index) => selector(record) * weights[index]).Sum() / Math.Max(1, totalWeight);

    private static long WeightedLong(
        IReadOnlyList<ApplicationBenefitLearningRecord> records,
        IReadOnlyList<int> weights,
        int totalWeight,
        Func<ApplicationBenefitLearningRecord, long> selector)
    {
        var value = records.Select((record, index) => (double)Math.Max(0, selector(record)) * weights[index]).Sum() /
                    Math.Max(1, totalWeight);
        return (long)Math.Clamp(Math.Round(value), 0d, long.MaxValue);
    }

    private static IEnumerable<string> MigrateSuggestionIds(
        IEnumerable<string> values,
        IEnumerable<ApplicationBenefitLearningRecord> records)
    {
        var replacements = records
            .Where(record => !string.IsNullOrWhiteSpace(record.ComponentKey))
            .Select(record => (Old: record.ComponentKey!, New: NormalizeIdentity(record).ComponentKey))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.New) &&
                           !string.Equals(pair.Old, pair.New, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var value in values)
        {
            var migrated = value;
            foreach (var replacement in replacements)
                migrated = migrated.Replace(replacement.Old, replacement.New!, StringComparison.OrdinalIgnoreCase);
            yield return migrated;
        }
    }

    private static string[] NormalizeSuggestionIds(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(500)
        .ToArray();

    private static ApplicationStableLearningRecord[] NormalizeFamilyStableRecords(
        IEnumerable<ApplicationStableLearningRecord>? records) =>
        (records ?? Array.Empty<ApplicationStableLearningRecord>())
        .Where(record => !string.IsNullOrWhiteSpace(record.FamilyKey))
        .Select(NormalizeFamilyStableRecord)
        .Where(record => record.StableWorkingSetSamplesBytes.Count > 0 &&
                         record.StableLastObservedAt.HasValue &&
                         record.ComponentKeys.Count > 0)
        .GroupBy(ApplicationStableScopeIdentity.For, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(record => record.StableLastObservedAt).First())
        .OrderByDescending(record => record.StableLastObservedAt)
        .Take(MaximumEntries)
        .ToArray();

    private static ApplicationStableLearningRecord NormalizeFamilyStableRecord(
        ApplicationStableLearningRecord record)
    {
        var familyKey = record.FamilyKey.Trim();
        var componentKeys = (record.ComponentKeys ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (TryNormalizeVersionedStableIdentity(componentKeys, out var versionedFamilyKey,
                out var versionedComponentKeys))
        {
            familyKey = versionedFamilyKey;
            componentKeys = versionedComponentKeys;
        }
        var hadMetadata = (record.StableSamples ?? Array.Empty<ApplicationStableSample>()).Count > 0;
        var samples = StableAnchorLearningPolicy.NormalizeSamples(record)
            .TakeLast(StableWorkingSetLearningPolicy.MaximumRecentSamples)
            .ToArray();
        var generation = hadMetadata ? Math.Max(0, record.AnchorGeneration) : 1;
        var baseline = hadMetadata
            ? Math.Max(0, record.AnchorGenerationBaselineBytes)
            : samples.Length == 0
                ? 0
                : StableWorkingSetLearningPolicy.Median(samples
                    .Select(sample => sample.WorkingSetBytes)
                    .OrderBy(value => value)
                    .ToArray());
        return record with
        {
            FamilyKey = familyKey,
            StableWorkingSetSamplesBytes = samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            StableSamples = samples,
            AnchorGeneration = generation,
            AnchorGenerationBaselineBytes = baseline,
            ComponentKeys = componentKeys,
            LastStableLaunchSignature = string.IsNullOrWhiteSpace(record.LastStableLaunchSignature)
                ? null
                : record.LastStableLaunchSignature.Trim()
        };
    }

    private static bool TryNormalizeVersionedStableIdentity(
        IReadOnlyList<string> componentKeys,
        out string familyKey,
        out string[] normalizedComponentKeys)
    {
        familyKey = string.Empty;
        normalizedComponentKeys = Array.Empty<string>();
        const string separator = "|component:";
        var resolved = new List<(string FamilyKey, string ComponentKey)>();
        foreach (var componentKey in componentKeys)
        {
            var separatorIndex = componentKey.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (separatorIndex < 0) return false;
            var executablePath = componentKey[(separatorIndex + separator.Length)..];
            if (!InstalledApplicationIdentity.TryResolveVersionedDirectory(executablePath, out var versioned))
                return false;
            resolved.Add((
                versioned.FamilyKey,
                ApplicationComponentIdentity.ForExecutable(versioned.FamilyKey, executablePath)));
        }
        if (resolved.Count == 0 || resolved.Any(item => !string.Equals(
                item.FamilyKey, resolved[0].FamilyKey, StringComparison.OrdinalIgnoreCase))) return false;
        familyKey = resolved[0].FamilyKey;
        normalizedComponentKeys = resolved.Select(item => item.ComponentKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }
}

public sealed record BenefitLearningDocument(
    int SchemaVersion,
    IReadOnlyList<ApplicationBenefitLearningRecord> Records)
{
    public IReadOnlyList<string> DismissedSuggestionIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ApplicationStableLearningRecord> FamilyStableRecords { get; init; } =
        Array.Empty<ApplicationStableLearningRecord>();
}

public sealed record BenefitLearningLoadResult(
    IReadOnlyList<ApplicationBenefitLearningRecord> Records,
    bool Migrated,
    string? ErrorMessage)
{
    public IReadOnlyList<string> DismissedSuggestionIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ApplicationStableLearningRecord> FamilyStableRecords { get; init; } =
        Array.Empty<ApplicationStableLearningRecord>();
}

public sealed record CandidatePlanCalibrationMetric(
    OptimizationRunContext RunContext,
    DateTimeOffset RecordedAt,
    OptimizationPlanOutcome Outcome,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint MemoryLoadPercent,
    ulong EffectiveTriggerAvailableBytes,
    int ObservedFamilyCount,
    int EvaluatedFamilyCount,
    int EligibleFamilyCount,
    int SelectedFamilyCount,
    double CandidateRatePercent,
    int MaxApplications,
    long MinimumFamilyWorkingSetBytes,
    long MinimumProcessWorkingSetBytes,
    double LegacyIdleThreshold,
    double ActiveCpuThresholdPercent,
    double ActiveIoThresholdBytesPerSecond,
    double VisibleWindowIdleDelaySeconds,
    double ProcessCooldownSeconds,
    double AutoCooldownSeconds,
    bool IgnoreMemoryPressureThreshold,
    bool QuickCandidateSelection,
    int LegacyOnlyEligibleCount,
    int ShadowOnlyEligibleCount,
    ProcessPopulationCalibrationMetric Population,
    IReadOnlyDictionary<string, int> ExclusionReasonCounts)
{
    public int LegacyOnlyExperimentalEligibleCount { get; init; }
    public int ExperimentalOnlyEligibleCount { get; init; }
    public IReadOnlyList<IdleScoreShadowMetric> IdleScoreShadows { get; init; } =
        Array.Empty<IdleScoreShadowMetric>();
    public IReadOnlyList<ActivityThresholdShadowMetric> ActivityThresholdShadows { get; init; } =
        Array.Empty<ActivityThresholdShadowMetric>();
    public IReadOnlyList<ProfileParameterShadowMetric> ProfileParameterShadows { get; init; } =
        Array.Empty<ProfileParameterShadowMetric>();
}

public sealed record ProfileParameterShadowPlanningOptions(
    Func<OptimizationSettings, OptimizationPlan>? CreateReadOnlyPlan = null,
    bool Enabled = true)
{
    public static ProfileParameterShadowPlanningOptions Disabled { get; } = new(null, false);
}

public sealed record ProfileParameterShadowMetric(
    string Key,
    string BaselineKey,
    string ParameterName,
    double BaselineValue,
    double ShadowValue,
    bool IsBaseline,
    int EvaluatedFamilyCount,
    int EligibleFamilyCount,
    int TargetProcessCount,
    long PotentialReleaseBytes,
    int AddedCandidateCount,
    int RemovedCandidateCount)
{
    public string ComparisonKind { get; init; } = string.Empty;
    public IReadOnlyList<string> AddedFamilyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedFamilyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProfileParameterCandidateDifferenceMetric> Differences { get; init; } =
        Array.Empty<ProfileParameterCandidateDifferenceMetric>();
}

public sealed record ProfileParameterCandidateDifferenceMetric(
    string FamilyId,
    bool FormalEligible,
    bool ShadowEligible,
    double FormalIdleScore,
    double ShadowIdleScore,
    long FormalTargetWorkingSetBytes,
    long ShadowTargetWorkingSetBytes,
    long FormalTotalWorkingSetBytes,
    long ShadowTotalWorkingSetBytes,
    int FormalReliableTargetProcessCount,
    int ShadowReliableTargetProcessCount,
    IReadOnlyList<string> FormalExclusionReasons,
    IReadOnlyList<string> ShadowExclusionReasons)
{
    public bool BaselineEligible => FormalEligible;
    public double BaselineIdleScore => FormalIdleScore;
    public long BaselineTargetWorkingSetBytes => FormalTargetWorkingSetBytes;
    public long BaselineTotalWorkingSetBytes => FormalTotalWorkingSetBytes;
    public int BaselineReliableTargetProcessCount => FormalReliableTargetProcessCount;
    public IReadOnlyList<string> BaselineExclusionReasons => FormalExclusionReasons;
}

public sealed record IdleScoreShadowMetric(
    string FamilyId,
    double LegacyIdleScore,
    double IdleConfidenceScore,
    double ExperimentalIdleScore,
    double IdleThreshold,
    bool LegacyMeetsThreshold,
    bool IdleConfidenceMeetsThreshold,
    bool ExperimentalMeetsThreshold,
    bool ActualPolicyEligible,
    bool SelectedForPlan,
    double IdleForSeconds,
    double MaximumReliableProcessCpuPercent,
    double MaximumReliableProcessIoBytesPerSecond,
    bool HasForegroundProcess,
    bool HasVisibleWindow,
    long WorkingSetBytes,
    int ProcessCount,
    int ReliableActivityProcessCount,
    IReadOnlyList<string> ExclusionReasons)
{
    public long TargetWorkingSetBytes { get; init; }
    public long TotalWorkingSetBytes { get; init; }
    public int TargetProcessCount { get; init; }
    public int ReliableTargetProcessCount { get; init; }
    public string SamplingReason { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, bool> FormalThresholdConclusions { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, bool> LocalThresholdConclusions { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    public IReadOnlyList<IdleScoreProcessInputMetric> ProcessInputs { get; init; } =
        Array.Empty<IdleScoreProcessInputMetric>();
}

public sealed record IdleScoreProcessInputMetric(
    long WorkingSetBytes,
    double CpuPercent,
    double IoBytesPerSecond,
    bool IsForeground,
    bool HasVisibleWindow,
    bool HasReliableActivitySample,
    double FormalIdleScore);

public sealed record ActivityThresholdShadowMetric(
    string Key,
    double CpuThresholdPercent,
    double IoThresholdBytesPerSecond,
    int EligibleFamilyCount,
    int SelectedFamilyCount,
    int TargetProcessCount,
    long PotentialReleaseBytes,
    int AddedCandidateCount,
    int RemovedCandidateCount,
    int CpuBlockedFamilyCount,
    int IoBlockedFamilyCount)
{
    public string ComparisonKind { get; init; } = string.Empty;
    public string ParameterName { get; init; } = "baseline";
    public double BaselineValue { get; init; }
    public double ShadowValue { get; init; }
    public bool IsBaseline { get; init; }
    public IReadOnlyList<string> AddedFamilyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedFamilyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ActivityThresholdCandidateDifferenceMetric> Differences { get; init; } =
        Array.Empty<ActivityThresholdCandidateDifferenceMetric>();
}

public sealed record ActivityThresholdCandidateDifferenceMetric(
    string FamilyId,
    bool ShadowEligible,
    double BaselineIdleScore,
    double ShadowIdleScore,
    long TargetWorkingSetBytes,
    long TotalWorkingSetBytes,
    int ReliableTargetProcessCount,
    IReadOnlyList<string> ExclusionReasons);

public sealed record ActivityThresholdExperiment(
    string Key,
    double CpuThresholdPercent,
    double IoThresholdBytesPerSecond)
{
    public string ParameterName { get; init; } = "baseline";
    public double BaselineValue { get; init; }
    public double ShadowValue { get; init; }
    public bool IsBaseline { get; init; }
}

public static class ActivityThresholdExperimentCatalog
{
    private const double Mebibyte = 1024d * 1024d;

    public static IReadOnlyList<ActivityThresholdExperiment> All { get; } =
        For(OptimizationProfile.Turbo);

    public static IReadOnlyList<ActivityThresholdExperiment> For(
        OptimizationProfile profile,
        OptimizationSettings? baselineSettings = null) =>
        For(profile, profile, baselineSettings);

    public static IReadOnlyList<ActivityThresholdExperiment> For(
        OptimizationProfile profile,
        OptimizationProfile baseProfile,
        OptimizationSettings? baselineSettings = null)
    {
        var prefix = baseProfile switch
        {
            OptimizationProfile.Lite => "lite",
            OptimizationProfile.Turbo => "turbo",
            OptimizationProfile.Ultimate => "ultimate",
            _ => "turbo"
        };
        var baselineCpu = baselineSettings?.ActiveCpuThresholdPercent ??
                          OptimizationSettings.For(baseProfile).ActiveCpuThresholdPercent;
        var baselineIoMib = (baselineSettings?.ActiveIoThresholdBytesPerSecond ??
                             OptimizationSettings.For(baseProfile).ActiveIoThresholdBytesPerSecond) / Mebibyte;
        baselineCpu = Math.Round(Math.Max(0.1, baselineCpu), 1, MidpointRounding.AwayFromZero);
        baselineIoMib = Math.Round(Math.Max(0.1, baselineIoMib), 1, MidpointRounding.AwayFromZero);

        var (cpuLow, cpuHigh, ioLow, ioHigh) = baseProfile switch
        {
            OptimizationProfile.Lite => (2d, 8d, 1d, 4d),
            OptimizationProfile.Turbo => (7.5d, 8.5d, 3.6d, 4.4d),
            OptimizationProfile.Ultimate => (6d, 20d, 4d, 16d),
            _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
        };
        cpuLow = ShiftForCustomBaseline(cpuLow, baselineCpu,
            OptimizationSettings.For(baseProfile).ActiveCpuThresholdPercent);
        cpuHigh = ShiftForCustomBaseline(cpuHigh, baselineCpu,
            OptimizationSettings.For(baseProfile).ActiveCpuThresholdPercent);
        var profileIoMib = OptimizationSettings.For(baseProfile).ActiveIoThresholdBytesPerSecond / Mebibyte;
        ioLow = ShiftForCustomBaseline(ioLow, baselineIoMib, profileIoMib);
        ioHigh = ShiftForCustomBaseline(ioHigh, baselineIoMib, profileIoMib);

        return new[]
        {
            CreateBaseline(prefix, baselineCpu, baselineIoMib),
            CreateVariant(prefix, "cpu", cpuLow, baselineCpu, baselineIoMib),
            CreateVariant(prefix, "cpu", cpuHigh, baselineCpu, baselineIoMib),
            CreateVariant(prefix, "io", ioLow, baselineCpu, baselineIoMib),
            CreateVariant(prefix, "io", ioHigh, baselineCpu, baselineIoMib)
        }.DistinctBy(experiment => (
            experiment.CpuThresholdPercent,
            experiment.IoThresholdBytesPerSecond)).ToArray();
    }

    public static long IoBytesPerSecond(double mebibytesPerSecond) =>
        Math.Max(1L, (long)Math.Round(
            mebibytesPerSecond * Mebibyte,
            MidpointRounding.AwayFromZero));

    private static double RoundThreshold(double value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static double ShiftForCustomBaseline(double profileValue, double baseline, double profileBaseline) =>
        RoundThreshold(Math.Max(0.1, baseline + profileValue - profileBaseline));

    private static ActivityThresholdExperiment CreateBaseline(
        string prefix,
        double cpu,
        double ioMib) => new(
        $"{prefix}-baseline",
        cpu,
        IoBytesPerSecond(ioMib))
    {
        ParameterName = "baseline",
        BaselineValue = 0,
        ShadowValue = 0,
        IsBaseline = true
    };

    private static ActivityThresholdExperiment CreateVariant(
        string prefix,
        string parameterName,
        double shadowValue,
        double baselineCpu,
        double baselineIoMib)
    {
        var value = parameterName == "cpu"
            ? shadowValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : shadowValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "mib";
        var key = $"{prefix}-{parameterName}-{value}";
        return new ActivityThresholdExperiment(
            key,
            parameterName == "cpu" ? shadowValue : baselineCpu,
            parameterName == "io" ? IoBytesPerSecond(shadowValue) : IoBytesPerSecond(baselineIoMib))
        {
            ParameterName = parameterName,
            BaselineValue = parameterName == "cpu" ? baselineCpu : baselineIoMib,
            ShadowValue = shadowValue,
            IsBaseline = false
        };
    }
}

public sealed record ProfileParameterExperiment(
    string Key,
    string ParameterName,
    double BaselineValue,
    double ShadowValue,
    OptimizationSettings Settings,
    bool IsBaseline);

public static class ProfileParameterExperimentCatalog
{
    private const double Mebibyte = 1024d * 1024d;

    public static IReadOnlyList<ProfileParameterExperiment> For(
        OptimizationProfile baseProfile,
        OptimizationSettings? baselineSettings = null)
    {
        var baseline = baselineSettings ?? OptimizationSettings.For(baseProfile);
        var profileSettings = OptimizationSettings.For(baseProfile);
        var prefix = PrefixFor(baseProfile);
        var experiments = new List<ProfileParameterExperiment>
        {
            CreateBaseline(prefix, baseline)
        };

        AddWorkingSetVariants(
            experiments,
            prefix,
            "family-ws",
            baseline,
            profileSettings.MinimumFamilyWorkingSetBytes,
            baseProfile,
            settings => settings.MinimumFamilyWorkingSetBytes,
            (settings, value) => settings with { MinimumFamilyWorkingSetBytes = value });
        AddWorkingSetVariants(
            experiments,
            prefix,
            "process-ws",
            baseline,
            profileSettings.MinimumProcessWorkingSetBytes,
            baseProfile,
            settings => settings.MinimumProcessWorkingSetBytes,
            (settings, value) => settings with { MinimumProcessWorkingSetBytes = value });
        AddDoubleVariants(
            experiments,
            prefix,
            "idle-score",
            baseline,
            profileSettings.MinimumIdleScore,
            baseProfile,
            settings => settings.MinimumIdleScore,
            (settings, value) => settings with { MinimumIdleScore = value },
            FormatScore);

        if (baseProfile != OptimizationProfile.Ultimate)
        {
            AddDoubleVariants(
                experiments,
                prefix,
                "visible-window",
                baseline,
                profileSettings.VisibleWindowIdleDelay.TotalMinutes,
                baseProfile,
                settings => settings.VisibleWindowIdleDelay.TotalMinutes,
                (settings, value) => settings with
                {
                    VisibleWindowIdleDelay = TimeSpan.FromMinutes(value)
                },
                FormatMinutes);
        }

        return experiments;
    }

    private static void AddWorkingSetVariants(
        ICollection<ProfileParameterExperiment> experiments,
        string prefix,
        string parameterName,
        OptimizationSettings baseline,
        long profileBaselineBytes,
        OptimizationProfile baseProfile,
        Func<OptimizationSettings, long> currentValue,
        Func<OptimizationSettings, long, OptimizationSettings> withValue)
    {
        var currentBaselineBytes = currentValue(baseline);
        var currentBaseline = currentBaselineBytes / Mebibyte;
        var profileBaselineMib = profileBaselineBytes / Mebibyte;
        var shadowValues = parameterName == "family-ws"
            ? baseProfile switch
            {
                OptimizationProfile.Lite => new[] { 192d, 384d },
                OptimizationProfile.Turbo => new[] { 96d },
                OptimizationProfile.Ultimate => new[] { 32d, 64d },
                _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
            }
            : baseProfile switch
            {
                OptimizationProfile.Lite => new[] { 12d, 40d },
                OptimizationProfile.Turbo => Array.Empty<double>(),
                OptimizationProfile.Ultimate => new[] { 2d, 8d },
                _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
            };
        AddVariants(
            experiments,
            prefix,
            parameterName,
            baseline,
            currentBaseline,
            profileBaselineMib,
            shadowValues,
            value => withValue(baseline, ToBytes(currentBaselineBytes, value - profileBaselineMib)),
            FormatMebibytes);
    }

    private static void AddDoubleVariants(
        ICollection<ProfileParameterExperiment> experiments,
        string prefix,
        string parameterName,
        OptimizationSettings baseline,
        double profileBaseline,
        OptimizationProfile baseProfile,
        Func<OptimizationSettings, double> currentValue,
        Func<OptimizationSettings, double, OptimizationSettings> withValue,
        Func<double, string> formatValue)
    {
        var currentBaseline = currentValue(baseline);
        var shadowValues = parameterName switch
        {
            "idle-score" => baseProfile switch
            {
                OptimizationProfile.Lite => new[] { 50d, 80d },
                OptimizationProfile.Turbo => new[] { 35d, 55d },
                OptimizationProfile.Ultimate => new[] { 15d, 45d },
                _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
            },
            "visible-window" => baseProfile switch
            {
                OptimizationProfile.Lite => new[] { 5d, 15d },
                OptimizationProfile.Turbo => new[] { 3d, 8d },
                _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(parameterName), parameterName, null)
        };
        AddVariants(
            experiments,
            prefix,
            parameterName,
            baseline,
            currentBaseline,
            profileBaseline,
            shadowValues,
            value => withValue(baseline, currentBaseline + value - profileBaseline),
            formatValue);
    }

    private static void AddVariants(
        ICollection<ProfileParameterExperiment> experiments,
        string prefix,
        string parameterName,
        OptimizationSettings baseline,
        double currentBaseline,
        double profileBaseline,
        IReadOnlyList<double> profileShadowValues,
        Func<double, OptimizationSettings> createSettings,
        Func<double, string> formatValue)
    {
        foreach (var profileShadowValue in profileShadowValues)
        {
            var shadowValue = currentBaseline + profileShadowValue - profileBaseline;
            if (shadowValue <= 0) continue;
            var settings = createSettings(profileShadowValue);
            if (settings == baseline) continue;
            experiments.Add(new ProfileParameterExperiment(
                $"{prefix}-{parameterName}-{formatValue(shadowValue)}",
                parameterName,
                currentBaseline,
                shadowValue,
                settings,
                false));
        }
    }

    private static ProfileParameterExperiment CreateBaseline(
        string prefix,
        OptimizationSettings settings) => new(
        $"{prefix}-baseline",
        "baseline",
        0,
        0,
        settings with { },
        true);

    private static long ToBytes(double currentBaselineBytes, double deltaMebibytes) =>
        Math.Max(1L, (long)Math.Round(
            currentBaselineBytes + deltaMebibytes * Mebibyte,
            MidpointRounding.AwayFromZero));

    private static string PrefixFor(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => "lite",
        OptimizationProfile.Turbo => "turbo",
        OptimizationProfile.Ultimate => "ultimate",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private static string FormatMebibytes(double value) =>
        $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}mib";

    private static string FormatScore(double value) =>
        value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatMinutes(double value) =>
        $"{value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}min";
}

public sealed record ProcessPopulationCalibrationMetric(
    int FamilyCount,
    int ProcessCount,
    int ReliableActivityProcessCount,
    int UnreliableActivityProcessCount,
    IReadOnlyDictionary<string, int> CpuPercentBucketCounts,
    IReadOnlyDictionary<string, int> IoRateBucketCounts,
    IReadOnlyDictionary<string, int> ProcessWorkingSetBucketCounts,
    IReadOnlyDictionary<string, int> FamilyWorkingSetBucketCounts,
    IReadOnlyDictionary<string, int> LegacyIdleScoreBucketCounts,
    IReadOnlyDictionary<string, int> IdleConfidenceBucketCounts,
    IReadOnlyDictionary<string, int> WindowStateCounts)
{
    public IReadOnlyDictionary<string, int> ExperimentalIdleScoreBucketCounts { get; init; } =
        new Dictionary<string, int>();
}

public sealed record CandidateTransitionCalibrationMetric(
    OptimizationRunContext RunContext,
    DateTimeOffset RecordedAt,
    string FamilyId,
    bool EnteredCandidate,
    int ProcessCount,
    int ReliableProcessCount,
    double FamilyCpuPercent,
    double MaximumProcessCpuPercent,
    double FamilyIoBytesPerSecond,
    double MaximumProcessIoBytesPerSecond,
    int? MaximumIoProcessId,
    double MaximumProcessIoReadBytesPerSecond,
    double MaximumProcessIoWriteBytesPerSecond,
    double MaximumProcessIoSampleIntervalSeconds,
    bool HasForegroundProcess,
    bool HasVisibleWindow,
    double ActiveCpuThresholdPercent,
    double ActiveIoThresholdBytesPerSecond,
    IReadOnlyList<string> ExclusionReasons);

public sealed record ProcessIoCalibrationMetric(
    OptimizationRunContext RunContext,
    DateTimeOffset RecordedAt,
    string FamilyId,
    string EventKind,
    int ProcessId,
    long? ProcessStartTimeFileTimeUtc,
    ulong ReadTransferCount,
    ulong WriteTransferCount,
    ulong ReadDeltaBytes,
    ulong WriteDeltaBytes,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double TotalBytesPerSecond,
    double SampleIntervalSeconds,
    double ProcessCpuPercent,
    bool ProcessIsForeground,
    double FamilyIoBytesPerSecond,
    bool FamilyHasForegroundProcess,
    double ActiveIoThresholdBytesPerSecond,
    bool FamilyIsCandidate,
    bool ProcessIsCandidateTarget,
    IReadOnlyList<string> ExclusionReasons)
{
    public double EpisodeDurationSeconds { get; init; }
    public int EpisodeSampleCount { get; init; }
    public double EpisodeAverageBytesPerSecond { get; init; }
    public double EpisodePeakBytesPerSecond { get; init; }
}

public sealed record ProcessIoCalibrationObservation(
    string FamilyKey,
    ProcessIoCalibrationMetric Metric);

public sealed record ProcessCpuCalibrationMetric(
    OptimizationRunContext RunContext,
    DateTimeOffset RecordedAt,
    string FamilyId,
    string EventKind,
    int ProcessId,
    long? ProcessStartTimeFileTimeUtc,
    double ProcessCpuPercent,
    bool ProcessIsForeground,
    double FamilyCpuPercent,
    bool FamilyHasForegroundProcess,
    double ActiveCpuThresholdPercent,
    bool FamilyIsCandidate,
    bool ProcessIsCandidateTarget,
    IReadOnlyList<string> ExclusionReasons,
    double EpisodeDurationSeconds,
    int EpisodeSampleCount,
    double EpisodeAverageCpuPercent,
    double EpisodePeakCpuPercent);

public sealed record ProcessCpuCalibrationObservation(
    string FamilyKey,
    ProcessCpuCalibrationMetric Metric);

public sealed class ProcessIoCalibrationTracker
{
    private const double SummaryIntervalSeconds = 120;
    private readonly Dictionary<ProcessIoIdentity, Episode> _episodes = new();

    public void Reset() => _episodes.Clear();

    public IReadOnlyList<ProcessIoCalibrationObservation> Observe(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        OptimizationPlan plan,
        OptimizationSettings settings,
        IReadOnlyList<ProcessFamilySnapshot> families)
    {
        var observations = new List<ProcessIoCalibrationObservation>();
        var currentIdentities = new HashSet<ProcessIoIdentity>();
        var candidates = plan.Candidates.ToDictionary(
            candidate => candidate.Family.Key,
            StringComparer.OrdinalIgnoreCase);
        var evaluations = plan.CandidateEvaluations.ToDictionary(
            evaluation => evaluation.FamilyKey,
            StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            candidates.TryGetValue(family.Key, out var candidate);
            var reasons = evaluations.TryGetValue(family.Key, out var evaluation)
                ? evaluation.ExclusionReasons.Select(reason => reason.ToString()).Distinct().ToArray()
                : Array.Empty<string>();
            foreach (var process in family.Processes)
            {
                var identity = new ProcessIoIdentity(
                    family.Key,
                    process.ProcessId,
                    process.StartTimeFileTimeUtc);
                currentIdentities.Add(identity);
                if (!process.HasReliableActivitySample) continue;

                var isAboveThreshold =
                    process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond;
                var wasAboveThreshold = _episodes.TryGetValue(identity, out var episode);
                if (!isAboveThreshold && !wasAboveThreshold) continue;

                var processIsCandidateTarget = candidate?.TargetProcesses.Any(target =>
                    target.ProcessId == process.ProcessId &&
                    target.StartTimeFileTimeUtc == process.StartTimeFileTimeUtc) == true;
                var currentMetric = CreateMetric(
                    runContext,
                    recordedAt,
                    family,
                    process,
                    settings,
                    candidate is not null,
                    processIsCandidateTarget,
                    reasons);
                if (!isAboveThreshold)
                {
                    _episodes.Remove(identity);
                    observations.Add(new ProcessIoCalibrationObservation(
                        family.Key,
                        WithEpisode(currentMetric with { EventKind = "threshold-exited" }, episode!)));
                    continue;
                }

                episode = wasAboveThreshold
                    ? episode!.Add(process.IoBytesPerSecond, process.IoSampleIntervalSeconds, currentMetric)
                    : Episode.Start(process.IoBytesPerSecond, process.IoSampleIntervalSeconds, currentMetric);
                _episodes[identity] = episode;
                var eventKind = wasAboveThreshold
                    ? episode.ObservedDurationSeconds - episode.LastSummaryDurationSeconds >= SummaryIntervalSeconds
                        ? "threshold-summary"
                        : null
                    : "threshold-entered";
                if (eventKind is null) continue;
                if (eventKind == "threshold-summary")
                {
                    episode = episode with { LastSummaryDurationSeconds = episode.ObservedDurationSeconds };
                    _episodes[identity] = episode;
                }
                observations.Add(new ProcessIoCalibrationObservation(
                    family.Key,
                    WithEpisode(currentMetric with { EventKind = eventKind }, episode)));
            }
        }

        foreach (var pair in _episodes.Where(pair => !currentIdentities.Contains(pair.Key)).ToArray())
        {
            _episodes.Remove(pair.Key);
            observations.Add(new ProcessIoCalibrationObservation(
                pair.Key.FamilyKey,
                WithEpisode(pair.Value.LastMetric with
                {
                    RecordedAt = recordedAt,
                    EventKind = "process-ended"
                }, pair.Value)));
        }
        return observations;
    }

    private static ProcessIoCalibrationMetric CreateMetric(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        ProcessFamilySnapshot family,
        ProcessSnapshot process,
        OptimizationSettings settings,
        bool familyIsCandidate,
        bool processIsCandidateTarget,
        IReadOnlyList<string> reasons) => new(
            runContext,
            recordedAt,
            string.Empty,
            string.Empty,
            process.ProcessId,
            process.StartTimeFileTimeUtc,
            process.IoReadTransferCount,
            process.IoWriteTransferCount,
            process.IoReadDeltaBytes,
            process.IoWriteDeltaBytes,
            Math.Max(0, process.IoReadBytesPerSecond),
            Math.Max(0, process.IoWriteBytesPerSecond),
            Math.Max(0, process.IoBytesPerSecond),
            Math.Max(0, process.IoSampleIntervalSeconds),
            Math.Max(0, process.CpuPercent),
            process.IsForeground,
            Math.Max(0, family.IoBytesPerSecond),
            family.HasForegroundProcess,
            settings.ActiveIoThresholdBytesPerSecond,
            familyIsCandidate,
            processIsCandidateTarget,
            reasons);

    private static ProcessIoCalibrationMetric WithEpisode(
        ProcessIoCalibrationMetric metric,
        Episode episode) => metric with
        {
            EpisodeDurationSeconds = episode.ObservedDurationSeconds,
            EpisodeSampleCount = episode.SampleCount,
            EpisodeAverageBytesPerSecond = episode.SampleCount == 0
                ? 0
                : episode.TotalValue / episode.SampleCount,
            EpisodePeakBytesPerSecond = episode.PeakValue
        };

    private sealed record Episode(
        double ObservedDurationSeconds,
        double LastSummaryDurationSeconds,
        int SampleCount,
        double TotalValue,
        double PeakValue,
        ProcessIoCalibrationMetric LastMetric)
    {
        public static Episode Start(
            double value,
            double intervalSeconds,
            ProcessIoCalibrationMetric metric) => new(
                Math.Max(0, intervalSeconds),
                0,
                1,
                Math.Max(0, value),
                Math.Max(0, value),
                metric);

        public Episode Add(
            double value,
            double intervalSeconds,
            ProcessIoCalibrationMetric metric) => this with
            {
                ObservedDurationSeconds = ObservedDurationSeconds + Math.Max(0, intervalSeconds),
                SampleCount = SampleCount + 1,
                TotalValue = TotalValue + Math.Max(0, value),
                PeakValue = Math.Max(PeakValue, Math.Max(0, value)),
                LastMetric = metric
            };
    }

    private sealed record ProcessIoIdentity(
        string FamilyKey,
        int ProcessId,
        long? ProcessStartTimeFileTimeUtc);
}

public sealed class ProcessCpuCalibrationTracker
{
    private const double SummaryIntervalSeconds = 120;
    private readonly Dictionary<ProcessCpuIdentity, Episode> _episodes = new();

    public void Reset() => _episodes.Clear();

    public IReadOnlyList<ProcessCpuCalibrationObservation> Observe(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        OptimizationPlan plan,
        OptimizationSettings settings,
        IReadOnlyList<ProcessFamilySnapshot> families)
    {
        var observations = new List<ProcessCpuCalibrationObservation>();
        var currentIdentities = new HashSet<ProcessCpuIdentity>();
        var candidates = plan.Candidates.ToDictionary(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase);
        var evaluations = plan.CandidateEvaluations.ToDictionary(evaluation => evaluation.FamilyKey, StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            candidates.TryGetValue(family.Key, out var candidate);
            var reasons = evaluations.TryGetValue(family.Key, out var evaluation)
                ? evaluation.ExclusionReasons.Select(reason => reason.ToString()).Distinct().ToArray()
                : Array.Empty<string>();
            foreach (var process in family.Processes)
            {
                var identity = new ProcessCpuIdentity(family.Key, process.ProcessId, process.StartTimeFileTimeUtc);
                currentIdentities.Add(identity);
                if (!process.HasReliableActivitySample) continue;

                var isAboveThreshold = process.CpuPercent >= settings.ActiveCpuThresholdPercent;
                var wasAboveThreshold = _episodes.TryGetValue(identity, out var episode);
                if (!isAboveThreshold && !wasAboveThreshold) continue;
                var processIsCandidateTarget = candidate?.TargetProcesses.Any(target =>
                    target.ProcessId == process.ProcessId &&
                    target.StartTimeFileTimeUtc == process.StartTimeFileTimeUtc) == true;
                var currentMetric = CreateMetric(
                    runContext, recordedAt, family, process, settings,
                    candidate is not null, processIsCandidateTarget, reasons);
                if (!isAboveThreshold)
                {
                    _episodes.Remove(identity);
                    observations.Add(new ProcessCpuCalibrationObservation(
                        family.Key,
                        WithEpisode(currentMetric with { EventKind = "threshold-exited" }, episode!)));
                    continue;
                }

                episode = wasAboveThreshold
                    ? episode!.Add(process.CpuPercent, process.IoSampleIntervalSeconds, currentMetric)
                    : Episode.Start(process.CpuPercent, process.IoSampleIntervalSeconds, currentMetric);
                _episodes[identity] = episode;
                var eventKind = wasAboveThreshold
                    ? episode.ObservedDurationSeconds - episode.LastSummaryDurationSeconds >= SummaryIntervalSeconds
                        ? "threshold-summary"
                        : null
                    : "threshold-entered";
                if (eventKind is null) continue;
                if (eventKind == "threshold-summary")
                {
                    episode = episode with { LastSummaryDurationSeconds = episode.ObservedDurationSeconds };
                    _episodes[identity] = episode;
                }
                observations.Add(new ProcessCpuCalibrationObservation(
                    family.Key,
                    WithEpisode(currentMetric with { EventKind = eventKind }, episode)));
            }
        }

        foreach (var pair in _episodes.Where(pair => !currentIdentities.Contains(pair.Key)).ToArray())
        {
            _episodes.Remove(pair.Key);
            observations.Add(new ProcessCpuCalibrationObservation(
                pair.Key.FamilyKey,
                WithEpisode(pair.Value.LastMetric with
                {
                    RecordedAt = recordedAt,
                    EventKind = "process-ended"
                }, pair.Value)));
        }
        return observations;
    }

    private static ProcessCpuCalibrationMetric CreateMetric(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        ProcessFamilySnapshot family,
        ProcessSnapshot process,
        OptimizationSettings settings,
        bool familyIsCandidate,
        bool processIsCandidateTarget,
        IReadOnlyList<string> reasons) => new(
            runContext,
            recordedAt,
            string.Empty,
            string.Empty,
            process.ProcessId,
            process.StartTimeFileTimeUtc,
            Math.Max(0, process.CpuPercent),
            process.IsForeground,
            Math.Max(0, family.CpuPercent),
            family.HasForegroundProcess,
            settings.ActiveCpuThresholdPercent,
            familyIsCandidate,
            processIsCandidateTarget,
            reasons,
            0,
            0,
            0,
            0);

    private static ProcessCpuCalibrationMetric WithEpisode(
        ProcessCpuCalibrationMetric metric,
        Episode episode) => metric with
        {
            EpisodeDurationSeconds = episode.ObservedDurationSeconds,
            EpisodeSampleCount = episode.SampleCount,
            EpisodeAverageCpuPercent = episode.SampleCount == 0 ? 0 : episode.TotalValue / episode.SampleCount,
            EpisodePeakCpuPercent = episode.PeakValue
        };

    private sealed record Episode(
        double ObservedDurationSeconds,
        double LastSummaryDurationSeconds,
        int SampleCount,
        double TotalValue,
        double PeakValue,
        ProcessCpuCalibrationMetric LastMetric)
    {
        public static Episode Start(double value, double intervalSeconds, ProcessCpuCalibrationMetric metric) => new(
            Math.Max(0, intervalSeconds), 0, 1, Math.Max(0, value), Math.Max(0, value), metric);

        public Episode Add(double value, double intervalSeconds, ProcessCpuCalibrationMetric metric) => this with
        {
            ObservedDurationSeconds = ObservedDurationSeconds + Math.Max(0, intervalSeconds),
            SampleCount = SampleCount + 1,
            TotalValue = TotalValue + Math.Max(0, value),
            PeakValue = Math.Max(PeakValue, Math.Max(0, value)),
            LastMetric = metric
        };
    }

    private sealed record ProcessCpuIdentity(string FamilyKey, int ProcessId, long? ProcessStartTimeFileTimeUtc);
}

public sealed record ActivityThresholdShadowState(
    ActivityThresholdExperiment Experiment,
    IReadOnlyDictionary<string, BackgroundActivity> Activity,
    IReadOnlyDictionary<int, CandidateIdleReadiness> CandidateIdleReadiness);

public sealed class ActivityThresholdShadowTracker
{
    private readonly Dictionary<string, ExperimentState> _states =
        new(StringComparer.Ordinal);
    private OptimizationProfile? _activeProfile;
    private OptimizationSettings? _activeSettings;

    public IReadOnlyList<ActivityThresholdShadowState> Observe(
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset now,
        OptimizationSettings settings,
        OptimizationProfile? profile = null)
    {
        var activeProfile = profile ?? OptimizationProfile.Turbo;
        EnsureExperiments(activeProfile, settings);
        var baselineState = _states.Values.Single(state => state.Experiment.IsBaseline);
        var activity = baselineState.ActivityTracker.Observe(
            families,
            now,
            settings.ActiveCpuThresholdPercent,
            settings.ActiveIoThresholdBytesPerSecond);
        var readiness = baselineState.CandidateIdleTracker.Observe(families, settings);
        return _states.Values
        .Select(state => new ActivityThresholdShadowState(state.Experiment, activity, readiness))
        .ToArray();
    }

    public void Reset()
    {
        _states.Clear();
        _activeProfile = null;
        _activeSettings = null;
    }

    private void EnsureExperiments(
        OptimizationProfile profile,
        OptimizationSettings settings)
    {
        var experiments = ActivityThresholdExperimentCatalog.For(profile, profile, settings);
        if (_activeProfile == profile &&
            Equals(_activeSettings, settings) &&
            _states.Count == experiments.Count &&
            experiments.All(experiment => _states.ContainsKey(experiment.Key)))
        {
            return;
        }

        _states.Clear();
        foreach (var experiment in experiments)
        {
            _states[experiment.Key] = new ExperimentState(
                experiment,
                new BackgroundActivityTracker(resetIdleOnBackgroundActivity: false),
                new CandidateIdleTracker());
        }
        _activeProfile = profile;
        _activeSettings = settings with { };
    }

    private sealed record ExperimentState(
        ActivityThresholdExperiment Experiment,
        BackgroundActivityTracker ActivityTracker,
        CandidateIdleTracker CandidateIdleTracker);
}

public static class CandidatePlanCalibrationPolicy
{
    private const double IdleScoreNearThresholdRange = 5;
    private const int ControlSampleDivisor = 20;

    public static CandidatePlanCalibrationMetric AttachActivityThresholdShadows(
        CandidatePlanCalibrationMetric metric,
        IReadOnlyList<ActivityThresholdShadowMetric> shadows)
    {
        var hasSharedBaseline = shadows.Any(shadow => shadow.IsBaseline);
        return metric with
        {
            ActivityThresholdShadows = shadows,
            ProfileParameterShadows = hasSharedBaseline
                ? metric.ProfileParameterShadows.Where(shadow => !shadow.IsBaseline).ToArray()
                : metric.ProfileParameterShadows
        };
    }

    public static CandidatePlanCalibrationMetric Create(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        OptimizationPlan plan,
        OptimizationSettings settings,
        MemorySnapshot memory,
        IReadOnlyList<ProcessFamilySnapshot> families,
        int observedFamilyCount,
        IReadOnlyDictionary<string, BackgroundActivity>? activity = null,
        ProfileParameterShadowPlanningOptions? profileParameterShadows = null)
    {
        var reasonCounts = plan.CandidateEvaluations
            .SelectMany(evaluation => evaluation.ExclusionReasons.Distinct())
            .GroupBy(reason => reason)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);
        var evaluated = plan.CandidateEvaluations.Count;
        var eligible = plan.CandidateEvaluations.Count(evaluation => evaluation.IsEligible);
        var legacyOnly = plan.CandidateEvaluations.Count(evaluation =>
            evaluation.IsEligible && evaluation.IdleConfidenceScore < settings.MinimumIdleScore);
        var shadowOnly = plan.CandidateEvaluations.Count(evaluation =>
            !evaluation.IsEligible &&
            evaluation.TargetProcessCount > 0 &&
            evaluation.IdleConfidenceScore >= settings.MinimumIdleScore &&
            evaluation.ExclusionReasons.Contains(CandidateExclusionReason.BelowIdleScore) &&
            evaluation.ExclusionReasons.All(reason =>
                reason is CandidateExclusionReason.Protected or CandidateExclusionReason.BelowIdleScore));
        var familyByKey = families.ToDictionary(family => family.Key, StringComparer.OrdinalIgnoreCase);
        var targetFamilyByKey = CreateTargetFamilies(plan, familyByKey);
        var experimentalScores = targetFamilyByKey.ToDictionary(
            pair => pair.Key,
            pair => LocalIdleScoreShadowPolicy.Calculate(
                pair.Value,
                activity?.GetValueOrDefault(pair.Key)?.IdleFor ?? TimeSpan.Zero),
            StringComparer.OrdinalIgnoreCase);
        var legacyOnlyExperimental = plan.CandidateEvaluations.Count(evaluation =>
            evaluation.IsEligible &&
            experimentalScores.GetValueOrDefault(evaluation.FamilyKey) < settings.MinimumIdleScore);
        var experimentalOnly = plan.CandidateEvaluations.Count(evaluation =>
            !evaluation.IsEligible &&
            evaluation.TargetProcessCount > 0 &&
            familyByKey.ContainsKey(evaluation.FamilyKey) &&
            experimentalScores.GetValueOrDefault(evaluation.FamilyKey) >= settings.MinimumIdleScore &&
            evaluation.ExclusionReasons.Count > 0 &&
            evaluation.ExclusionReasons.All(reason => reason == CandidateExclusionReason.BelowIdleScore));
        var idleScoreShadows = CreateIdleScoreShadows(
            plan,
            settings.MinimumIdleScore,
            familyByKey,
            targetFamilyByKey,
            experimentalScores,
            activity,
            recordedAt);
        return new CandidatePlanCalibrationMetric(
            runContext,
            recordedAt,
            plan.Outcome,
            memory.TotalPhysicalBytes,
            memory.AvailablePhysicalBytes,
            memory.LoadPercent,
            EffectiveTriggerAvailableBytes(memory, settings),
            Math.Max(0, observedFamilyCount),
            evaluated,
            eligible,
            plan.Candidates.Count,
            evaluated == 0 ? 0 : eligible / (double)evaluated * 100d,
            settings.MaxApplications,
            settings.MinimumFamilyWorkingSetBytes,
            settings.MinimumProcessWorkingSetBytes,
            settings.MinimumIdleScore,
            settings.ActiveCpuThresholdPercent,
            settings.ActiveIoThresholdBytesPerSecond,
            settings.VisibleWindowIdleDelay.TotalSeconds,
            settings.ProcessCooldown.TotalSeconds,
            settings.AutoCooldown.TotalSeconds,
            settings.IgnoreMemoryPressureThreshold,
            settings.QuickCandidateSelection,
            legacyOnly,
            shadowOnly,
            SummarizePopulation(families, experimentalScores),
            reasonCounts)
        {
            LegacyOnlyExperimentalEligibleCount = legacyOnlyExperimental,
            ExperimentalOnlyEligibleCount = experimentalOnly,
            IdleScoreShadows = idleScoreShadows,
            ProfileParameterShadows = profileParameterShadows?.Enabled == false
                ? Array.Empty<ProfileParameterShadowMetric>()
                : CreateProfileParameterShadows(
                    runContext,
                    recordedAt,
                    plan,
                    settings,
                    memory,
                    families,
                    activity,
                    profileParameterShadows)
        };
    }

    public static IReadOnlyList<ProfileParameterShadowMetric> CreateProfileParameterShadows(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        OptimizationPlan formalPlan,
        OptimizationSettings settings,
        MemorySnapshot memory,
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, BackgroundActivity>? activity = null,
        ProfileParameterShadowPlanningOptions? options = null)
    {
        if (options?.Enabled == false) return Array.Empty<ProfileParameterShadowMetric>();

        var experiments = ProfileParameterExperimentCatalog.For(runContext.BaseProfile, settings);
        var baseline = experiments.Single(experiment => experiment.IsBaseline);
        var createPlan = options?.CreateReadOnlyPlan ?? CreateFallbackReadOnlyPlanFactory(
            runContext,
            recordedAt,
            formalPlan,
            settings,
            memory,
            families,
            activity);
        var recomputedBaselinePlan = createPlan(baseline.Settings);
        var metrics = new List<ProfileParameterShadowMetric>(experiments.Count);

        foreach (var experiment in experiments)
        {
            var shadowPlan = experiment.IsBaseline
                ? recomputedBaselinePlan
                : createPlan(experiment.Settings);
            metrics.Add(CreateProfileParameterShadowMetric(
                experiment,
                baseline.Key,
                experiment.IsBaseline ? formalPlan : recomputedBaselinePlan,
                shadowPlan,
                families));
        }

        return metrics;
    }

    private static Func<OptimizationSettings, OptimizationPlan> CreateFallbackReadOnlyPlanFactory(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        OptimizationPlan formalPlan,
        OptimizationSettings settings,
        MemorySnapshot memory,
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, BackgroundActivity>? activity)
    {
        var familyByKey = families.ToDictionary(
            family => family.Key,
            StringComparer.OrdinalIgnoreCase);
        var evaluations = formalPlan.CandidateEvaluations.ToDictionary(
            evaluation => evaluation.FamilyKey,
            StringComparer.OrdinalIgnoreCase);
        var protectedPaths = evaluations.Values
            .Where(evaluation => evaluation.ExclusionReasons.Contains(CandidateExclusionReason.Protected))
            .SelectMany(evaluation => familyByKey.GetValueOrDefault(evaluation.FamilyKey)?.Processes ??
                                      Array.Empty<ProcessSnapshot>())
            .Select(process => process.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var protection = new ProtectionRules(protectedPaths, protectRelatedProcesses: false);
        var automaticBackoffFamilies = FamilyKeysWithReason(
            evaluations,
            CandidateExclusionReason.AutomaticBackoff);
        var pendingReboundObservationFamilies = FamilyKeysWithReason(
            evaluations,
            CandidateExclusionReason.ReboundObservationPending);
        var automaticBackoffComponents = ComponentKeysWithReason(
            evaluations,
            familyByKey,
            CandidateExclusionReason.AutomaticBackoff);
        var pendingReboundObservationComponents = ComponentKeysWithReason(
            evaluations,
            familyByKey,
            CandidateExclusionReason.ReboundObservationPending);
        var stableSuppressedComponents = ComponentKeysWithReason(
            evaluations,
            familyByKey,
            CandidateExclusionReason.StableStateSuppression);
        var candidateIdleReadiness = CreateReadinessSnapshot(families, evaluations);
        var lastTrimTimes = CreateCooldownSnapshot(
            families,
            evaluations,
            settings.ProcessCooldown,
            recordedAt);
        var planner = new OptimizationPlanner();
        var manual = runContext.Trigger == OptimizationTriggerKind.Manual;

        return shadowSettings => planner.CreatePlan(
            memory,
            families,
            shadowSettings,
            protection,
            lastTrimTimes,
            recordedAt,
            manual,
            activity,
            automaticBackoffFamilies: automaticBackoffFamilies,
            outcomeMultipliers: null,
            intelligentPreview: true,
            learningConfidences: null,
            candidateIdleReadiness: candidateIdleReadiness,
            enforceUnattendedSafety: false,
            pendingReboundObservationFamilies: pendingReboundObservationFamilies,
            lastTrimProcessStartTimes: null,
            automaticBackoffComponents: automaticBackoffComponents,
            pendingReboundObservationComponents: pendingReboundObservationComponents,
            stableSuppressedComponents: stableSuppressedComponents);
    }

    private static ProfileParameterShadowMetric CreateProfileParameterShadowMetric(
        ProfileParameterExperiment experiment,
        string baselineKey,
        OptimizationPlan formalPlan,
        OptimizationPlan shadowPlan,
        IReadOnlyList<ProcessFamilySnapshot> families)
    {
        var formalKeys = formalPlan.Candidates
            .Select(candidate => candidate.Family.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowKeys = shadowPlan.Candidates
            .Select(candidate => candidate.Family.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedKeys = shadowKeys
            .Except(formalKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removedKeys = formalKeys
            .Except(shadowKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var familyByKey = families.ToDictionary(
            family => family.Key,
            StringComparer.OrdinalIgnoreCase);
        var formalEvaluations = formalPlan.CandidateEvaluations.ToDictionary(
            evaluation => evaluation.FamilyKey,
            StringComparer.OrdinalIgnoreCase);
        var shadowEvaluations = shadowPlan.CandidateEvaluations.ToDictionary(
            evaluation => evaluation.FamilyKey,
            StringComparer.OrdinalIgnoreCase);
        var differences = addedKeys
            .Concat(removedKeys)
            .Select(key => CreateProfileParameterDifference(
                key,
                familyByKey,
                formalEvaluations,
                shadowEvaluations))
            .Where(difference => difference is not null)
            .Select(difference => difference!)
            .ToArray();

        return new ProfileParameterShadowMetric(
            experiment.Key,
            baselineKey,
            experiment.ParameterName,
            experiment.BaselineValue,
            experiment.ShadowValue,
            experiment.IsBaseline,
            shadowPlan.CandidateEvaluations.Count,
            shadowPlan.CandidateEvaluations.Count(evaluation => evaluation.IsEligible),
            shadowPlan.Candidates.Sum(candidate => candidate.TargetProcesses.Count),
            shadowPlan.Candidates.Sum(candidate => candidate.PotentialReleaseBytes),
            addedKeys.Length,
            removedKeys.Length)
        {
            ComparisonKind = experiment.IsBaseline ? "formal-plan-drift" : "recomputed-baseline",
            AddedFamilyIds = addedKeys
                .Select(CalibrationFamilyIdentity.Create)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            RemovedFamilyIds = removedKeys
                .Select(CalibrationFamilyIdentity.Create)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Differences = differences
        };
    }

    private static ProfileParameterCandidateDifferenceMetric? CreateProfileParameterDifference(
        string familyKey,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> familyByKey,
        IReadOnlyDictionary<string, CandidateEvaluation> formalEvaluations,
        IReadOnlyDictionary<string, CandidateEvaluation> shadowEvaluations)
    {
        if (!familyByKey.TryGetValue(familyKey, out var family)) return null;
        var formal = formalEvaluations.GetValueOrDefault(familyKey);
        var shadow = shadowEvaluations.GetValueOrDefault(familyKey);
        return new ProfileParameterCandidateDifferenceMetric(
            CalibrationFamilyIdentity.Create(familyKey),
            formal?.IsEligible == true,
            shadow?.IsEligible == true,
            formal?.LegacyIdleScore ?? family.IdleScore,
            shadow?.LegacyIdleScore ?? family.IdleScore,
            formal?.TargetWorkingSetBytes ?? 0,
            shadow?.TargetWorkingSetBytes ?? 0,
            formal?.TotalWorkingSetBytes ?? family.WorkingSetBytes,
            shadow?.TotalWorkingSetBytes ?? family.WorkingSetBytes,
            CountReliableTargetProcesses(family, formal),
            CountReliableTargetProcesses(family, shadow),
            ExclusionReasonNames(formal),
            ExclusionReasonNames(shadow));
    }

    private static int CountReliableTargetProcesses(
        ProcessFamilySnapshot family,
        CandidateEvaluation? evaluation)
    {
        if (evaluation is null) return 0;
        var targetIds = evaluation.TargetProcessIds.ToHashSet();
        return family.Processes.Count(process =>
            targetIds.Contains(process.ProcessId) && process.HasReliableActivitySample);
    }

    private static IReadOnlyList<string> ExclusionReasonNames(CandidateEvaluation? evaluation) =>
        (evaluation?.ExclusionReasons ?? Array.Empty<CandidateExclusionReason>())
        .Distinct()
        .OrderBy(reason => reason)
        .Select(reason => reason.ToString())
        .ToArray();

    private static IReadOnlySet<string> FamilyKeysWithReason(
        IReadOnlyDictionary<string, CandidateEvaluation> evaluations,
        CandidateExclusionReason reason) => evaluations.Values
        .Where(evaluation => evaluation.ExclusionReasons.Contains(reason))
        .Select(evaluation => evaluation.FamilyKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ComponentKeysWithReason(
        IReadOnlyDictionary<string, CandidateEvaluation> evaluations,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> familyByKey,
        CandidateExclusionReason reason)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in evaluations.Values.Where(
                     evaluation => evaluation.ExclusionReasons.Contains(reason)))
        {
            if (!familyByKey.TryGetValue(evaluation.FamilyKey, out var family)) continue;
            foreach (var process in family.Processes)
                result.Add(ApplicationComponentIdentity.ForProcess(family.Key, process));
        }
        return result;
    }

    private static IReadOnlyDictionary<int, CandidateIdleReadiness> CreateReadinessSnapshot(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, CandidateEvaluation> evaluations)
    {
        var result = new Dictionary<int, CandidateIdleReadiness>();
        foreach (var family in families)
        {
            var pending = evaluations.GetValueOrDefault(family.Key)?.ExclusionReasons.Contains(
                CandidateExclusionReason.IdleConfirmationPending) == true;
            var samples = pending ? 0 : CandidateIdleTracker.MinimumReliableLowActivitySamples;
            foreach (var process in family.Processes)
            {
                result[process.ProcessId] = new CandidateIdleReadiness(
                    process.ProcessId,
                    samples,
                    samples >= CandidateIdleTracker.MinimumReliableLowActivitySamples);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<int, DateTimeOffset> CreateCooldownSnapshot(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, CandidateEvaluation> evaluations,
        TimeSpan cooldown,
        DateTimeOffset now)
    {
        var result = new Dictionary<int, DateTimeOffset>();
        if (cooldown <= TimeSpan.Zero) return result;
        foreach (var family in families)
        {
            var evaluation = evaluations.GetValueOrDefault(family.Key);
            if (evaluation?.ExclusionReasons.Contains(CandidateExclusionReason.ProcessCooldown) != true)
                continue;
            foreach (var processId in evaluation.TargetProcessIds)
                result[processId] = now - TimeSpan.FromTicks(cooldown.Ticks / 2);
        }
        return result;
    }

    private static IReadOnlyList<IdleScoreShadowMetric> CreateIdleScoreShadows(
        OptimizationPlan plan,
        double idleThreshold,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> familyByKey,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> targetFamilyByKey,
        IReadOnlyDictionary<string, double> experimentalScores,
        IReadOnlyDictionary<string, BackgroundActivity>? activity,
        DateTimeOffset recordedAt)
    {
        var selectedFamilyKeys = plan.Candidates
            .Select(candidate => candidate.Family.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadows = new List<IdleScoreShadowMetric>();
        foreach (var evaluation in plan.CandidateEvaluations)
        {
            if (!familyByKey.TryGetValue(evaluation.FamilyKey, out var family) ||
                !targetFamilyByKey.TryGetValue(evaluation.FamilyKey, out var targetFamily)) continue;

            var experimentalScore = experimentalScores.GetValueOrDefault(evaluation.FamilyKey);
            var legacyMeetsThreshold = evaluation.LegacyIdleScore >= idleThreshold;
            var confidenceMeetsThreshold = evaluation.IdleConfidenceScore >= idleThreshold;
            var experimentalMeetsThreshold = experimentalScore >= idleThreshold;
            var hasTargetProcesses = targetFamily.Processes.Count > 0;
            var hasEligibilityDisagreement = hasTargetProcesses && (
                legacyMeetsThreshold != confidenceMeetsThreshold ||
                legacyMeetsThreshold != experimentalMeetsThreshold);
            var isNearThreshold = hasTargetProcesses && (
                Math.Abs(evaluation.LegacyIdleScore - idleThreshold) <= IdleScoreNearThresholdRange ||
                Math.Abs(evaluation.IdleConfidenceScore - idleThreshold) <= IdleScoreNearThresholdRange ||
                Math.Abs(experimentalScore - idleThreshold) <= IdleScoreNearThresholdRange);
            var samplingReason = hasEligibilityDisagreement
                ? "Disagreement"
                : isNearThreshold
                    ? "NearThreshold"
                    : ShouldIncludeControlSample(evaluation.FamilyKey, recordedAt)
                        ? "ControlSample"
                        : null;
            if (samplingReason is null) continue;

            var reliableProcesses = targetFamily.Processes
                .Where(process => process.HasReliableActivitySample)
                .ToArray();
            var familyReliableProcessCount = family.Processes.Count(process => process.HasReliableActivitySample);
            shadows.Add(new IdleScoreShadowMetric(
                CalibrationFamilyIdentity.Create(evaluation.FamilyKey),
                evaluation.LegacyIdleScore,
                evaluation.IdleConfidenceScore,
                experimentalScore,
                idleThreshold,
                legacyMeetsThreshold,
                confidenceMeetsThreshold,
                experimentalMeetsThreshold,
                evaluation.IsEligible,
                selectedFamilyKeys.Contains(evaluation.FamilyKey),
                Math.Max(0, activity?.GetValueOrDefault(evaluation.FamilyKey)?.IdleFor.TotalSeconds ?? 0),
                reliableProcesses.Length == 0
                    ? 0
                    : reliableProcesses.Max(process => Math.Max(0, process.CpuPercent)),
                reliableProcesses.Length == 0
                    ? 0
                    : reliableProcesses.Max(process => Math.Max(0, process.IoBytesPerSecond)),
                targetFamily.HasForegroundProcess,
                targetFamily.HasVisibleWindow,
                family.WorkingSetBytes,
                family.Processes.Count,
                familyReliableProcessCount,
                evaluation.ExclusionReasons
                    .Distinct()
                    .OrderBy(reason => reason)
                    .Select(reason => reason.ToString())
                    .ToArray())
            {
                TargetWorkingSetBytes = targetFamily.WorkingSetBytes,
                TotalWorkingSetBytes = family.WorkingSetBytes,
                TargetProcessCount = targetFamily.Processes.Count,
                ReliableTargetProcessCount = reliableProcesses.Length,
                SamplingReason = samplingReason,
                FormalThresholdConclusions = ThresholdConclusions(
                    evaluation.LegacyIdleScore,
                    idleThreshold),
                LocalThresholdConclusions = ThresholdConclusions(
                    experimentalScore,
                    idleThreshold),
                ProcessInputs = targetFamily.Processes
                    .Select(process => new IdleScoreProcessInputMetric(
                        Math.Max(0, process.WorkingSetBytes),
                        Math.Max(0, process.CpuPercent),
                        Math.Max(0, process.IoBytesPerSecond),
                        process.IsForeground,
                        process.HasVisibleWindow,
                        process.HasReliableActivitySample,
                        Math.Clamp(process.IdleScore, 0, 100)))
                    .ToArray()
            });
        }

        return shadows;
    }

    public static OptimizationPlan ApplyLongIdleFilter(
        OptimizationPlan plan,
        IReadOnlyDictionary<string, BackgroundActivity> activity,
        TimeSpan minimumIdle,
        int maxApplications)
    {
        var candidates = plan.Candidates
            .Where(candidate =>
                activity.TryGetValue(candidate.Family.Key, out var assessment) &&
                assessment.State == BackgroundActivityState.Idle &&
                assessment.IdleFor >= minimumIdle)
            .Take(maxApplications <= 0 ? int.MaxValue : maxApplications)
            .ToArray();
        return plan with
        {
            ShouldRun = candidates.Length > 0,
            Candidates = candidates,
            Outcome = candidates.Length > 0
                ? OptimizationPlanOutcome.CandidatesFound
                : OptimizationPlanOutcome.NoCandidates
        };
    }

    private static IReadOnlyDictionary<string, bool> ThresholdConclusions(
        double score,
        double baselineThreshold) => new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["-2.5"] = score >= baselineThreshold - 2.5,
        ["baseline"] = score >= baselineThreshold,
        ["+2.5"] = score >= baselineThreshold + 2.5
    };

    private static IReadOnlyDictionary<string, ProcessFamilySnapshot> CreateTargetFamilies(
        OptimizationPlan plan,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> familyByKey)
    {
        var result = new Dictionary<string, ProcessFamilySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in plan.CandidateEvaluations)
        {
            if (!familyByKey.TryGetValue(evaluation.FamilyKey, out var family)) continue;
            var targetProcessIds = evaluation.TargetProcessIds.ToHashSet();
            result[evaluation.FamilyKey] = new ProcessFamilySnapshot(
                family.Key,
                family.DisplayName,
                family.ExecutableDirectory,
                family.Processes
                    .Where(process => targetProcessIds.Contains(process.ProcessId))
                    .ToArray());
        }
        return result;
    }

    internal static bool ShouldIncludeControlSample(string familyKey, DateTimeOffset recordedAt)
    {
        var timeBucket = recordedAt.ToUnixTimeSeconds() / (15 * 60);
        var input = $"{familyKey.Trim().ToLowerInvariant()}|{timeBucket}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var value = (uint)(hash[0] | hash[1] << 8 | hash[2] << 16 | hash[3] << 24);
        return value % ControlSampleDivisor == 0;
    }

    private static ProcessPopulationCalibrationMetric SummarizePopulation(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, double> experimentalScores)
    {
        var processes = families.SelectMany(family => family.Processes).ToArray();
        var reliable = processes.Where(process => process.HasReliableActivitySample).ToArray();
        return new ProcessPopulationCalibrationMetric(
            families.Count,
            processes.Length,
            reliable.Length,
            processes.Length - reliable.Length,
            BucketCounts(reliable, process => CpuBucket(process.CpuPercent)),
            BucketCounts(reliable, process => IoBucket(process.IoBytesPerSecond)),
            BucketCounts(processes, process => ProcessWorkingSetBucket(process.WorkingSetBytes)),
            BucketCounts(families, family => FamilyWorkingSetBucket(family.WorkingSetBytes)),
            BucketCounts(families, family => ScoreBucket(family.IdleScore)),
            BucketCounts(families, family => ScoreBucket(family.IdleConfidenceScore)),
            BucketCounts(processes, WindowStateBucket))
        {
            ExperimentalIdleScoreBucketCounts = BucketCounts(
                families,
                family => ScoreBucket(experimentalScores.GetValueOrDefault(family.Key)))
        };
    }

    private static IReadOnlyDictionary<string, int> BucketCounts<T>(
        IEnumerable<T> values,
        Func<T, string> bucket) => values
        .GroupBy(bucket, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static string CpuBucket(double value) => value switch
    {
        < 0.5 => "lt-0.5",
        < 2 => "0.5-2",
        < 4 => "2-4",
        < 8 => "4-8",
        < 16 => "8-16",
        < 25 => "16-25",
        _ => "gte-25"
    };

    private static string IoBucket(double value) => value switch
    {
        < 32d * 1024 => "lt-32-kib",
        < 256d * 1024 => "32-256-kib",
        < 1d * 1024 * 1024 => "256-kib-1-mib",
        < 4d * 1024 * 1024 => "1-4-mib",
        < 16d * 1024 * 1024 => "4-16-mib",
        _ => "gte-16-mib"
    };

    private static string ProcessWorkingSetBucket(long value) => value switch
    {
        < 4L * 1024 * 1024 => "lt-4-mib",
        < 8L * 1024 * 1024 => "4-8-mib",
        < 24L * 1024 * 1024 => "8-24-mib",
        < 64L * 1024 * 1024 => "24-64-mib",
        < 256L * 1024 * 1024 => "64-256-mib",
        _ => "gte-256-mib"
    };

    private static string FamilyWorkingSetBucket(long value) => value switch
    {
        < 64L * 1024 * 1024 => "lt-64-mib",
        < 96L * 1024 * 1024 => "64-96-mib",
        < 280L * 1024 * 1024 => "96-280-mib",
        < 512L * 1024 * 1024 => "280-512-mib",
        _ => "gte-512-mib"
    };

    private static string ScoreBucket(double value) => value switch
    {
        < 20 => "lt-20",
        < 45 => "20-45",
        < 65 => "45-65",
        < 80 => "65-80",
        _ => "gte-80"
    };

    private static string WindowStateBucket(ProcessSnapshot process)
    {
        if (process.IsForeground) return "foreground";
        if (process.HasVisibleWindow) return "visible";
        if (process.HasMinimizedWindow) return "minimized";
        return "hidden";
    }

    private static ulong EffectiveTriggerAvailableBytes(
        MemorySnapshot memory,
        OptimizationSettings settings)
    {
        if (settings.IgnoreMemoryPressureThreshold) return 0;
        if (settings.TriggerAvailablePercent <= 0) return settings.TriggerAvailableBytes;

        var percent = (ulong)Math.Clamp(settings.TriggerAvailablePercent, 1, 95);
        var percentThreshold = memory.TotalPhysicalBytes * percent / 100;
        return settings.TriggerAvailableBytes == 0
            ? percentThreshold
            : Math.Min(settings.TriggerAvailableBytes, percentThreshold);
    }
}

public sealed record ApplicationOutcomeCalibrationMetric(
    OptimizationRunContext RunContext,
    string FamilyId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double ObservationWindowSeconds,
    long ReleasedBytes,
    long RegainedBytes,
    long RetainedBytes,
    double ReboundPercent,
    bool BackoffTriggered,
    double? TimeToForegroundSeconds)
{
    public long LateWorkingSetBytes { get; init; }
}

public sealed record StableStateObservationCalibrationMetric(
    OptimizationRunContext RunContext,
    string FamilyId,
    string ScopeId,
    string LaunchId,
    DateTimeOffset ObservedAt,
    int ComponentCount,
    long CurrentWorkingSetBytes,
    long PreviousWorkingSetBytes,
    long ConvergenceToleranceBytes,
    bool QualityEligible,
    ApplicationStableCandidateState? StateBefore,
    ApplicationStableCandidateState StateAfter,
    ApplicationStableObservationDecision Decision)
{
    public double ReboundPercent { get; init; }
}

public sealed record MonitoringCalibrationMetric(
    DateTimeOffset RecordedAt,
    double ProcessCaptureMilliseconds,
    double ConfiguredMonitoringIntervalSeconds,
    int ProcessCount,
    int FamilyCount,
    bool AutomaticOptimizationEnabled,
    long AppWorkingSetBytes,
    long AppPrivateMemoryBytes,
    double AppCpuPercent,
    double AppIoBytesPerSecond,
    int AppThreadCount,
    int AppHandleCount,
    bool HasReliableAppCpuSample,
    bool HasReliableAppIoSample)
{
    public double RelationshipSnapshotMilliseconds { get; init; }
    public double WindowEnumerationMilliseconds { get; init; }
    public double PathReadMilliseconds { get; init; }
    public double SlowestPathReadMilliseconds { get; init; }
    public int SlowestPathProcessId { get; init; }
    public int MainModuleFallbackCount { get; init; }
    public int PathFailureCount { get; init; }
    public double CpuReadMilliseconds { get; init; }
    public double IoReadMilliseconds { get; init; }
    public double ProcessLoopMilliseconds { get; init; }
    public double OtherMilliseconds { get; init; }
}

public sealed record OptimizationProcessCalibrationMetric(
    OptimizationRunContext RunContext,
    string RunId,
    string BuildId,
    DateTimeOffset RecordedAt,
    string FamilyId,
    int ProcessIndex,
    int ProcessCount,
    bool Success,
    bool Skipped,
    bool SetProcessWorkingSetSucceeded,
    int? SetProcessWorkingSetErrorCode,
    bool EmptyWorkingSetSucceeded,
    int? EmptyWorkingSetErrorCode,
    double IdleConfidenceScore,
    string IdleState,
    double IdleSeconds,
    bool WasForeground,
    bool HadVisibleWindow,
    int SafetyScopeProcessCount,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    uint? PageFaultCountDelta,
    double TotalMilliseconds,
    double OpenProcessMilliseconds,
    double IdentityCheckMilliseconds,
    double RelationshipCheckMilliseconds,
    double SetProcessWorkingSetMilliseconds,
    double EmptyWorkingSetMilliseconds,
    double MeasurementMilliseconds,
    double UiDispatchDelayMilliseconds);

public sealed record OptimizationRunCalibrationMetric(
    OptimizationRunContext RunContext,
    string RunId,
    string BuildId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool SnapshotAlreadyRefreshed,
    int CandidateCount,
    int TargetProcessCount,
    int SucceededProcessCount,
    int SkippedProcessCount,
    int FailedProcessCount,
    uint MemoryLoadPercentBefore,
    ulong AvailablePhysicalBytesBefore,
    uint? MemoryLoadPercentAfter,
    ulong? AvailablePhysicalBytesAfter,
    double SnapshotMilliseconds,
    double PlanningMilliseconds,
    double ExecutionMilliseconds,
    double CompletionMilliseconds,
    double MaximumUiDispatchDelayMilliseconds,
    double? AppAverageCpuPercent,
    double? AppPeakCpuPercent,
    ulong? SystemPageFaultCountDelta,
    ulong? SystemPageReadCountDelta,
    ulong? SystemPageReadIoCountDelta);

public sealed record OptimizationResourceSample(
    double AppAverageCpuPercent,
    double AppPeakCpuPercent,
    ulong? SystemPageFaultCountDelta,
    ulong? SystemPageReadCountDelta,
    ulong? SystemPageReadIoCountDelta);

public sealed class OptimizationResourceSampler
{
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(100);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly TimeSpan _startedProcessorTime;
    private readonly SystemPagingSnapshot? _pagingBefore;
    private readonly Task _samplingTask;
    private double _peakCpuPercent;

    private OptimizationResourceSampler()
    {
        _startedProcessorTime = _process.TotalProcessorTime;
        _pagingBefore = SystemPagingCounter.TryCapture();
        _samplingTask = SampleAsync(_cancellation.Token);
    }

    public static OptimizationResourceSampler Start() => new();

    public async Task<OptimizationResourceSample> StopAsync()
    {
        _cancellation.Cancel();
        try { await _samplingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var elapsed = Stopwatch.GetElapsedTime(_startedTimestamp);
        _process.Refresh();
        var processorTime = _process.TotalProcessorTime - _startedProcessorTime;
        var averageCpu = CpuPercent(processorTime, elapsed);
        var pagingAfter = SystemPagingCounter.TryCapture();
        var result = new OptimizationResourceSample(
            averageCpu,
            _peakCpuPercent,
            CounterDelta(_pagingBefore?.PageFaultCount, pagingAfter?.PageFaultCount),
            CounterDelta(_pagingBefore?.PageReadCount, pagingAfter?.PageReadCount),
            CounterDelta(_pagingBefore?.PageReadIoCount, pagingAfter?.PageReadIoCount));
        _process.Dispose();
        _cancellation.Dispose();
        return result;
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        var previousTimestamp = _startedTimestamp;
        var previousProcessorTime = _startedProcessorTime;
        while (true)
        {
            await Task.Delay(SamplingInterval, cancellationToken).ConfigureAwait(false);
            var timestamp = Stopwatch.GetTimestamp();
            _process.Refresh();
            var processorTime = _process.TotalProcessorTime;
            var cpu = CpuPercent(
                processorTime - previousProcessorTime,
                Stopwatch.GetElapsedTime(previousTimestamp, timestamp));
            _peakCpuPercent = Math.Max(_peakCpuPercent, cpu);
            previousTimestamp = timestamp;
            previousProcessorTime = processorTime;
        }
    }

    private static double CpuPercent(TimeSpan processorTime, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? 0
            : Math.Clamp(
                processorTime.TotalMilliseconds / elapsed.TotalMilliseconds /
                Math.Max(1, Environment.ProcessorCount) * 100,
                0,
                100);

    private static ulong? CounterDelta(uint? before, uint? after)
    {
        if (!before.HasValue || !after.HasValue) return null;
        return after.Value >= before.Value
            ? after.Value - before.Value
            : (ulong)uint.MaxValue + 1 + after.Value - before.Value;
    }
}

public readonly record struct SystemPagingSnapshot(
    uint PageFaultCount,
    uint PageReadCount,
    uint PageReadIoCount);

public static class SystemPagingCounter
{
    private const int SystemPerformanceInformation = 2;
    private const int BufferSize = 1024;
    private const int PageFaultCountOffset = 60;
    private const int PageReadCountOffset = 80;
    private const int PageReadIoCountOffset = 84;

    public static SystemPagingSnapshot? TryCapture()
    {
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            if (NtQuerySystemInformation(
                    SystemPerformanceInformation,
                    buffer,
                    BufferSize,
                    out _) != 0)
            {
                return null;
            }

            return new SystemPagingSnapshot(
                unchecked((uint)Marshal.ReadInt32(buffer, PageFaultCountOffset)),
                unchecked((uint)Marshal.ReadInt32(buffer, PageReadCountOffset)),
                unchecked((uint)Marshal.ReadInt32(buffer, PageReadIoCountOffset)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);
}

public sealed record ResponsivenessStallCalibrationMetric(
    DateTimeOffset RecordedAt,
    string BuildId,
    string Source,
    double DelayMilliseconds,
    bool OptimizationRunning,
    string? RunId);

public sealed record LargeMemoryOpportunityCalibrationMetric(
    OptimizationRunContext RunContext,
    DateTimeOffset RecordedAt,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint MemoryLoadPercent,
    int EvaluatedFamilyCount,
    int EligibleFamilyCount,
    long PotentialReleaseBytes,
    IReadOnlyDictionary<string, int> ExclusionReasonCounts);

public static class LargeMemoryOpportunityPolicy
{
    public const ulong MinimumTotalPhysicalBytes = 30UL * 1024 * 1024 * 1024;
    public const uint MaximumMemoryLoadPercentExclusive = 50;
    public static readonly TimeSpan MinimumObservationInterval = TimeSpan.FromMinutes(10);

    public static bool ShouldObserve(
        MemorySnapshot memory,
        OptimizationPlanOutcome outcome,
        DateTimeOffset? lastObservedAt,
        DateTimeOffset now) =>
        outcome == OptimizationPlanOutcome.LowMemoryPressure &&
        memory.TotalPhysicalBytes >= MinimumTotalPhysicalBytes &&
        memory.LoadPercent < MaximumMemoryLoadPercentExclusive &&
        (!lastObservedAt.HasValue || now - lastObservedAt.Value >= MinimumObservationInterval);

    public static LargeMemoryOpportunityCalibrationMetric CreateMetric(
        OptimizationRunContext runContext,
        DateTimeOffset recordedAt,
        MemorySnapshot memory,
        OptimizationPlan shadowPlan)
    {
        var evaluated = shadowPlan.CandidateEvaluations.Count;
        var eligible = shadowPlan.CandidateEvaluations.Count(candidate => candidate.IsEligible);
        var reasons = shadowPlan.CandidateEvaluations
            .SelectMany(candidate => candidate.ExclusionReasons.Distinct())
            .GroupBy(reason => reason)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);
        return new LargeMemoryOpportunityCalibrationMetric(
            runContext,
            recordedAt,
            memory.TotalPhysicalBytes,
            memory.AvailablePhysicalBytes,
            memory.LoadPercent,
            evaluated,
            eligible,
            shadowPlan.Candidates.Sum(candidate => candidate.PotentialReleaseBytes),
            reasons);
    }
}

public sealed class CalibrationMetricsStore
{
    private const int SchemaVersion = 10;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _sequence;

    public CalibrationMetricsStore(string? path = null)
    {
        _path = path ?? AppDataPaths.CalibrationMetricsFile;
    }

    public string MetricsFile => _path;

    public void AppendCandidatePlan(CandidatePlanCalibrationMetric metric) =>
        Append("candidate-plan", metric);

    public void AppendApplicationOutcome(ApplicationReboundOutcome outcome)
    {
        if (outcome.RunContext is null) return;
        Append("application-outcome", new ApplicationOutcomeCalibrationMetric(
            outcome.RunContext,
            CalibrationFamilyIdentity.Create(outcome.FamilyKey),
            outcome.StartedAt,
            outcome.CompletedAt,
            outcome.ObservationWindowSeconds,
            outcome.ReleasedBytes,
            outcome.RegainedBytes,
            outcome.RetainedBytes,
            outcome.ReboundPercent,
            outcome.BackoffTriggered,
            outcome.TimeToForeground?.TotalSeconds)
        {
            LateWorkingSetBytes = Math.Max(0, outcome.LateWorkingSetBytes)
        });
    }

    public void AppendStableStateObservation(ApplicationStableObservation observation)
    {
        if (observation.RunContext is null) return;
        Append("stable-state-observation", new StableStateObservationCalibrationMetric(
            observation.RunContext,
            CalibrationFamilyIdentity.Create(observation.FamilyKey),
            CalibrationFamilyIdentity.Create(observation.ScopeKey),
            string.IsNullOrWhiteSpace(observation.LaunchSignature)
                ? string.Empty
                : CalibrationFamilyIdentity.Create(observation.LaunchSignature),
            observation.ObservedAt,
            observation.ComponentCount,
            observation.CurrentWorkingSetBytes,
            observation.PreviousWorkingSetBytes,
            observation.ConvergenceToleranceBytes,
            observation.QualityEligible,
            observation.StateBefore,
            observation.StateAfter,
            observation.Decision)
        {
            ReboundPercent = observation.ReboundPercent
        });
    }

    public void AppendMonitoring(MonitoringCalibrationMetric metric) =>
        Append("monitoring", metric);

    public void AppendCandidateTransition(
        string familyKey,
        CandidateTransitionCalibrationMetric metric) =>
        Append("candidate-transition", metric with { FamilyId = CalibrationFamilyIdentity.Create(familyKey) });

    public void AppendProcessIoSample(
        string familyKey,
        ProcessIoCalibrationMetric metric) =>
        Append("process-io-sample", metric with { FamilyId = CalibrationFamilyIdentity.Create(familyKey) });

    public void AppendProcessCpuSample(
        string familyKey,
        ProcessCpuCalibrationMetric metric) =>
        Append("process-cpu-sample", metric with { FamilyId = CalibrationFamilyIdentity.Create(familyKey) });

    public void AppendOptimizationProcess(
        string familyKey,
        OptimizationProcessCalibrationMetric metric) =>
        Append("optimization-process", metric with { FamilyId = CalibrationFamilyIdentity.Create(familyKey) });

    public void AppendOptimizationRun(OptimizationRunCalibrationMetric metric) =>
        Append("optimization-run", metric);

    public void AppendResponsivenessStall(ResponsivenessStallCalibrationMetric metric) =>
        Append("responsiveness-stall", metric);

    public void AppendLargeMemoryOpportunity(LargeMemoryOpportunityCalibrationMetric metric) =>
        Append("large-memory-opportunity", metric);

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            var previousPath = _path + ".previous";
            if (File.Exists(previousPath)) File.Delete(previousPath);
        }
    }

    private void Append(string kind, object payload)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumBytes)
                File.Move(_path, _path + ".previous", true);
            var line = JsonSerializer.Serialize(
                new CalibrationMetricEnvelope(
                    SchemaVersion,
                    _sessionId,
                    ++_sequence,
                    DateTimeOffset.UtcNow,
                    kind,
                    payload),
                JsonOptions);
            File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private sealed record CalibrationMetricEnvelope(
        int SchemaVersion,
        string SessionId,
        long Sequence,
        DateTimeOffset WrittenAtUtc,
        string Kind,
        object Payload);
}

internal static class CalibrationFamilyIdentity
{
    public static string Create(string familyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(familyKey.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }
}

public static class SelectedApplicationOptimizationPolicy
{
    public static OptimizationSettings Apply(OptimizationSettings settings) => settings with
    {
        MaxApplications = 0,
        MinimumFamilyWorkingSetBytes = 0,
        MinimumProcessWorkingSetBytes = 0,
        MinimumIdleScore = 0,
        ProcessCooldown = TimeSpan.Zero,
        VisibleWindowIdleDelay = TimeSpan.Zero,
        QuickCandidateSelection = false
    };
}

public static class MonitoringIntervalPolicy
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(15);

    public static TimeSpan Resolve(bool automaticOptimizationEnabled, bool reboundTrackingActive) =>
        automaticOptimizationEnabled || reboundTrackingActive ? ActiveInterval : IdleInterval;
}

public static class OptimizationResultAttributionPolicy
{
    public static bool CanAttributeSystemMemoryChange(int successfulRequestCount) =>
        successfulRequestCount > 0;
}

public sealed class DiagnosticLog
{
    private const long MaximumBytes = 2L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<bool>? _isEnabled;

    public DiagnosticLog(string? path = null, Func<bool>? isEnabled = null)
    {
        _path = path ?? AppDataPaths.DiagnosticLogFile;
        _isEnabled = isEnabled;
    }

    public string LogFile => _path;

    public void Info(string message) => Write("INFO", message, null);
    public void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            var previousPath = _path + ".previous";
            if (File.Exists(previousPath)) File.Delete(previousPath);
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        if (_isEnabled is not null && !_isEnabled()) return;
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                RotateIfNeeded();
                var safeMessage = SingleLine(message);
                var exceptionText = exception is null
                    ? string.Empty
                    : $" | {exception.GetType().Name}: {SingleLine(exception.Message)}";
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:O} [{level}] {safeMessage}{exceptionText}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must not interrupt monitoring or optimization.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaximumBytes) return;
        File.Move(_path, _path + ".previous", true);
    }

    private static string SingleLine(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
}

public static class DeepReleaseSelectionPolicy
{
    public static bool IsCheckedByDefault(DeepReleaseCandidate candidate)
    {
        return candidate.IsSuggested;
    }
}

public static class DeepReleaseDialogFlow
{
    public static bool ShouldContinueToServices(bool applicationDialogWasShown, bool applicationDialogConfirmed) =>
        !applicationDialogWasShown || applicationDialogConfirmed;
}

public static class DeepReleasePresentation
{
    public static string FormatCandidate(DeepReleaseCandidate candidate, UiLanguage language)
    {
        var state = candidate.Activity.State switch
        {
            BackgroundActivityState.Idle => Text(language, "DeepReleaseIdle"),
            BackgroundActivityState.Working => Text(language, "DeepReleaseWorking"),
            BackgroundActivityState.Visible => Text(language, "DeepReleaseVisible"),
            _ => Text(language, "DeepReleaseObserving")
        };
        return $"{candidate.Family.DisplayName}  ·  {DisplayFormat.Bytes(candidate.Family.WorkingSetBytes)}  ·  {state}";
    }

    public static string FormatSelection(
        IReadOnlyCollection<DeepReleaseCandidate> candidates,
        UiLanguage language)
    {
        if (candidates.Count == 0) return Text(language, "DeepReleaseSelectionEmpty");
        var workingSetBytes = candidates.Sum(candidate => candidate.Family.WorkingSetBytes);
        return string.Format(
            Text(language, "DeepReleaseSelectionFormat"),
            candidates.Count,
            DisplayFormat.Bytes(workingSetBytes));
    }

    private static string Text(UiLanguage language, string key) => UiTextCatalog.For(language)[key];
}

public static class WindowThemeService
{
    private const int DwmwaCloak = 13;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void EnableNativeWindowAnimations(Window window)
    {
        ApplyNativeWindowStyle(window);
        _ = window.Dispatcher.BeginInvoke(
            () => ApplyNativeWindowStyle(window),
            DispatcherPriority.Loaded);
    }

    public static long NativeAnimationStyle(long style) => style | WsCaption;

    public static string DescribeNativeWindowState(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return "HWND=0x0";
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        return $"HWND=0x{handle.ToInt64():X}; Style=0x{style:X}; ExStyle=0x{extendedStyle:X}; " +
               $"Caption={(style & WsCaption) == WsCaption}; SysMenu={(style & WsSysMenu) != 0}; " +
               $"MinBox={(style & WsMinimizeBox) != 0}; MaxBox={(style & WsMaximizeBox) != 0}; " +
               $"AppWindow={(extendedStyle & WsExAppWindow) != 0}; ToolWindow={(extendedStyle & WsExToolWindow) != 0}; " +
               $"NativeVisible={IsWindowVisible(handle)}; Iconic={IsIconic(handle)}";
    }

    private static void ApplyNativeWindowStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        style = NativeAnimationStyle(style);
        _ = SetWindowLongPtr(handle, GwlStyle, new IntPtr(style));
        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _ = DwmFlush();
    }

    public static bool TrySetCloaked(Window window, bool cloaked)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var enabled = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(handle, DwmwaCloak, ref enabled, sizeof(int)) == 0;
    }

    public static void FlushComposition() => _ = DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}

internal static class ForegroundProcessProbe
{
    public static int? GetProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero ||
            GetWindowThreadProcessId(window, out var processId) == 0 ||
            processId > int.MaxValue)
        {
            return null;
        }

        return (int)processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
