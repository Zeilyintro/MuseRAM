namespace MuseRAM.Core;

public readonly record struct WindowsPackageApplicationIdentity(
    string FamilyKey,
    string PackageRootDirectory,
    string RelativeExecutablePath);

public readonly record struct VersionedDirectoryApplicationIdentity(
    string FamilyKey,
    string RootDirectory,
    string RelativeExecutablePath);

public static class InstalledApplicationIdentity
{
    private const string WindowsAppsMarker = "\\windowsapps\\";
    private static readonly System.Text.RegularExpressions.Regex PackageDirectoryPattern = new(
        "^(?<name>[^_]+)_(?<version>\\d+(?:\\.\\d+){3})_(?<architecture>[^_]+)_(?<resource>[^_]*)_(?<publisher>[^_]+)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex VersionedDirectoryPattern = new(
        "^app-\\d+(?:\\.\\d+){1,3}(?:[-+][0-9a-z.-]+)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool TryResolveWindowsPackage(
        string? executablePath,
        out WindowsPackageApplicationIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        try
        {
            var fullPath = Path.GetFullPath(executablePath.Trim()).Replace('/', '\\');
            var markerIndex = fullPath.IndexOf(WindowsAppsMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return false;

            var packageStart = markerIndex + WindowsAppsMarker.Length;
            var packageEnd = fullPath.IndexOf('\\', packageStart);
            if (packageEnd < 0 || packageEnd == fullPath.Length - 1) return false;

            var packageDirectory = fullPath[packageStart..packageEnd];
            var match = PackageDirectoryPattern.Match(packageDirectory);
            if (!match.Success) return false;

            var name = match.Groups["name"].Value.ToLowerInvariant();
            var publisher = match.Groups["publisher"].Value.ToLowerInvariant();
            identity = new WindowsPackageApplicationIdentity(
                $"package:{name}_{publisher}",
                fullPath[..packageEnd],
                fullPath[(packageEnd + 1)..].ToLowerInvariant());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryResolveVersionedDirectory(
        string? executablePath,
        out VersionedDirectoryApplicationIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        try
        {
            var fullPath = Path.GetFullPath(executablePath.Trim()).Replace('/', '\\');
            var versionDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(versionDirectory) ||
                !VersionedDirectoryPattern.IsMatch(Path.GetFileName(versionDirectory))) return false;
            var rootDirectory = Path.GetDirectoryName(versionDirectory);
            if (string.IsNullOrWhiteSpace(rootDirectory) || IsGenericInstallRoot(rootDirectory)) return false;
            identity = new VersionedDirectoryApplicationIdentity(
                "directory:" + rootDirectory.TrimEnd('\\').ToLowerInvariant(),
                rootDirectory,
                Path.GetRelativePath(versionDirectory, fullPath).Replace('/', '\\').ToLowerInvariant());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGenericInstallRoot(string directory)
    {
        var candidate = directory.TrimEnd('\\');
        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => string.Equals(path.TrimEnd('\\'), candidate, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ApplicationComponentIdentity
{
    private const string Separator = "|component:";

    public static string ForProcess(string familyKey, ProcessSnapshot process) =>
        ForExecutable(familyKey, process.ExecutablePath);

    public static string ForExecutable(string familyKey, string? executablePath) =>
        InstalledApplicationIdentity.TryResolveWindowsPackage(executablePath, out var package)
            ? familyKey + Separator + "package:" + package.RelativeExecutablePath
            : InstalledApplicationIdentity.TryResolveVersionedDirectory(executablePath, out var versioned)
            ? familyKey + Separator + "versioned:" + versioned.RelativeExecutablePath
            : ExecutablePathIdentity.TryNormalize(executablePath, out var path)
            ? familyKey + Separator + path
            : familyKey;

    public static IReadOnlyDictionary<string, ProcessSnapshot[]> GroupProcesses(
        ProcessFamilySnapshot family) => family.Processes
        .GroupBy(
            process => ForProcess(family.Key, process),
            StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.ToArray(),
            StringComparer.OrdinalIgnoreCase);
}

public static class StableStateSuppressionPolicy
{
    public const int NaturalStableStateModelVersion = 3;

    public static ApplicationStableLearningRecord? ActiveStableRecord(
        ProcessFamilySnapshot family,
        IReadOnlyList<ProcessFamilySnapshot> families,
        IEnumerable<ApplicationStableLearningRecord> records,
        OptimizationSettings optimizationSettings,
        ProtectionRules protection)
    {
        var protectionContext = protection.CreateContext(families.SelectMany(item => item.Processes));
        var familyRecords = records
            .Where(record => string.Equals(record.FamilyKey, family.Key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var componentKeys = ResolveCurrentScopeComponentKeys(
            family, optimizationSettings, protection, protectionContext, familyRecords);
        if (componentKeys.Length == 0) return null;
        var scopeKey = ApplicationStableScopeIdentity.For(family.Key, componentKeys);
        return familyRecords
            .Where(record => string.Equals(ApplicationStableScopeIdentity.For(record), scopeKey,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.StableLastObservedAt)
            .FirstOrDefault();
    }

    public static long? StableReferenceBytes(
        ApplicationStableLearningRecord record,
        int maximumSamples = StableWorkingSetLearningPolicy.DefaultRecentSamples) =>
        StableAnchorLearningPolicy.EffectiveAnchorBytes(record, maximumSamples);

    public static StableStateSuppressionMode ResolveMode(
        OptimizationProfile baseProfile,
        StableStateSuppressionMode configuredMode)
    {
        if (configuredMode != StableStateSuppressionMode.FollowBaseProfile)
            return configuredMode;

        return baseProfile switch
        {
            OptimizationProfile.Lite => StableStateSuppressionMode.ReduceRepeatedOptimization,
            OptimizationProfile.Turbo => StableStateSuppressionMode.Balanced,
            OptimizationProfile.Ultimate => StableStateSuppressionMode.FasterReevaluation,
            _ => StableStateSuppressionMode.Balanced
        };
    }

    public static long? SuppressionLimitBytes(
        ApplicationStableLearningRecord record,
        StableStateSuppressionSettings settings,
        DateTimeOffset now)
    {
        settings = settings.Normalize();
        var learnedFloor = StableAnchorLearningPolicy.EffectiveAnchorBytes(
            record,
            settings.MaximumStableSamplePool);
        var hasAnchorMetadata = (record.StableSamples ?? Array.Empty<ApplicationStableSample>()).Count > 0;
        if (!learnedFloor.HasValue ||
            hasAnchorMetadata && (record.AnchorGeneration <= 0 ||
                                  record.AnchorGenerationBaselineBytes <= 0) ||
            StableAnchorLearningPolicy.AcceptedSampleCount(
                record,
                settings.MaximumStableSamplePool) < settings.MinimumSamples ||
            learnedFloor.Value > settings.MaximumStableWorkingSetBytes ||
            record.StableLastObservedAt is not { } stableLastObservedAt ||
            now - stableLastObservedAt >= settings.MaximumRecordAge)
        {
            return null;
        }
        var growthMargin = Math.Max(
            settings.AbsoluteGrowthMarginBytes,
            (long)Math.Round(learnedFloor.Value * settings.RelativeGrowthMargin));
        var dynamicLimit = learnedFloor.Value > long.MaxValue - growthMargin
            ? long.MaxValue
            : learnedFloor.Value + growthMargin;
        return Math.Min(dynamicLimit, settings.MaximumStableWorkingSetBytes);
    }

    public static IReadOnlySet<string> SuppressedComponentKeys(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IEnumerable<ApplicationStableLearningRecord> familyStableRecords,
        OptimizationSettings optimizationSettings,
        ProtectionRules protection,
        StableStateSuppressionSettings? settings,
        DateTimeOffset now,
        IEnumerable<ApplicationStableCandidateStatus>? runtimeCandidates = null,
        IEnumerable<ApplicationStableAnchorSetting>? anchorSettings = null)
    {
        if (settings is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var stableRecords = familyStableRecords.ToArray();
        var recordsByScope = stableRecords
            .GroupBy(ApplicationStableScopeIdentity.For, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(record => record.StableLastObservedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var protectionContext = protection.CreateContext(families.SelectMany(family => family.Processes));
        var runtimeByScope = (runtimeCandidates ?? Array.Empty<ApplicationStableCandidateStatus>())
            .Where(candidate => candidate.State == ApplicationStableCandidateState.Converged)
            .GroupBy(candidate => candidate.ComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(candidate => candidate.LastObservedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var fixedAnchorsByScope = (anchorSettings ?? Array.Empty<ApplicationStableAnchorSetting>())
            .Where(anchor => anchor.Mode == StableAnchorMode.Fixed &&
                             anchor.FixedAnchorBytes > 0 &&
                             !string.IsNullOrWhiteSpace(anchor.ScopeKey))
            .GroupBy(anchor => anchor.ScopeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            var currentComponentKeys = ResolveCurrentScopeComponentKeys(
                family,
                optimizationSettings,
                protection,
                protectionContext,
                stableRecords.Where(record => string.Equals(
                    record.FamilyKey, family.Key, StringComparison.OrdinalIgnoreCase)));
            if (currentComponentKeys.Length == 0) continue;
            var components = ApplicationComponentIdentity.GroupProcesses(family);
            var scopeKey = ApplicationStableScopeIdentity.For(family.Key, currentComponentKeys);
            var currentWorkingSet = currentComponentKeys.Aggregate(0L, (total, key) =>
            {
                var componentBytes = components[key].Sum(process => Math.Max(0, process.WorkingSetBytes));
                return componentBytes > long.MaxValue - total ? long.MaxValue : total + componentBytes;
            });
            var fixedAnchor = fixedAnchorsByScope.GetValueOrDefault(scopeKey);
            var fixedAnchorSupported = fixedAnchor is not null &&
                fixedAnchor.FixedAnchorBytes <= settings.MaximumStableWorkingSetBytes &&
                recordsByScope.TryGetValue(scopeKey, out var anchorRecord) &&
                anchorRecord.StableLastObservedAt is { } anchorObservedAt &&
                now - anchorObservedAt < settings.MaximumRecordAge &&
                StableAnchorLearningPolicy.AcceptedSampleCount(
                    anchorRecord,
                    settings.MaximumStableSamplePool) >= settings.MinimumSamples;
            var persistedLimit = fixedAnchorSupported
                ? SuppressionLimitBytes(fixedAnchor!.FixedAnchorBytes, settings)
                : recordsByScope.TryGetValue(scopeKey, out var record)
                    ? SuppressionLimitBytes(record, settings, now)
                    : null;
            var launchSignature = NaturalStableLaunchSignature(currentComponentKeys, components);
            long? runtimeLimit = !fixedAnchorSupported &&
                                 runtimeByScope.TryGetValue(scopeKey, out var runtime) &&
                                 runtime.CandidateBytes <= settings.MaximumStableWorkingSetBytes &&
                                 string.Equals(runtime.LaunchSignature, launchSignature, StringComparison.Ordinal)
                ? SuppressionLimitBytes(runtime.CandidateBytes, settings)
                : null;
            // A persisted or fixed anchor is the user-visible suppression contract.
            // The current-launch runtime anchor is only a fallback while no eligible
            // persisted limit exists; it must not silently widen the displayed limit.
            var limit = persistedLimit ?? runtimeLimit;
            if (!limit.HasValue || currentWorkingSet > limit.Value) continue;
            foreach (var component in currentComponentKeys)
            {
                result.Add(component);
            }
        }
        return result;
    }

    public static IReadOnlyList<NaturalStableStateSnapshot> NaturalStableStateSnapshots(
        IReadOnlyList<ProcessFamilySnapshot> families,
        OptimizationSettings optimizationSettings,
        ProtectionRules protection,
        IReadOnlyDictionary<int, CandidateIdleReadiness> candidateIdleReadiness,
        IEnumerable<ApplicationStableLearningRecord>? stableRecords = null,
        IEnumerable<NaturalStableScopeRequest>? recoveryScopes = null)
    {
        var protectionContext = protection.CreateContext(families.SelectMany(family => family.Processes));
        var records = (stableRecords ?? Array.Empty<ApplicationStableLearningRecord>()).ToArray();
        var requestedScopes = (recoveryScopes ?? Array.Empty<NaturalStableScopeRequest>()).ToArray();
        var snapshots = new List<NaturalStableStateSnapshot>();
        foreach (var family in families)
        {
            var familyRequests = requestedScopes
                .Where(request => string.Equals(request.FamilyKey, family.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(request => request.StartedAt)
                .ToArray();
            var familyRecords = records
                .Where(record => string.Equals(record.FamilyKey, family.Key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var currentScopeComponentKeys = ResolveCurrentScopeComponentKeys(
                family, optimizationSettings, protection, protectionContext, familyRecords);
            var components = ApplicationComponentIdentity.GroupProcesses(family);
            var runningRequests = familyRequests
                .Where(request => ScopeComponentsAreRunning(
                    family, request.ComponentKeys, protection, protectionContext))
                .GroupBy(request => ApplicationStableScopeIdentity.For(
                    family.Key, request.ComponentKeys), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(request => request.StartedAt).First())
                .ToArray();
            var currentScopeKey = currentScopeComponentKeys.Length == 0
                ? string.Empty
                : ApplicationStableScopeIdentity.For(family.Key, currentScopeComponentKeys);
            var currentRecoveryRequest = runningRequests.FirstOrDefault(request => string.Equals(
                ApplicationStableScopeIdentity.For(family.Key, request.ComponentKeys),
                currentScopeKey,
                StringComparison.OrdinalIgnoreCase));
            var scopes = new List<(IReadOnlyList<string> ComponentKeys, NaturalStableScopeRequest? Request)>();
            if (currentScopeComponentKeys.Length > 0 &&
                (currentRecoveryRequest is not null || runningRequests.Length == 0))
            {
                scopes.Add((currentScopeComponentKeys, currentRecoveryRequest));
            }
            scopes.AddRange(runningRequests
                .Where(request => !string.Equals(
                    ApplicationStableScopeIdentity.For(family.Key, request.ComponentKeys),
                    currentScopeKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(request => (
                    (IReadOnlyList<string>)request.ComponentKeys
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    (NaturalStableScopeRequest?)request)));

            foreach (var (componentKeys, recoveryRequest) in scopes)
            {
                var processes = componentKeys.SelectMany(key => components[key]).ToArray();
                var launchSignature = NaturalStableLaunchSignature(componentKeys, components);
                var workingSet = SumWorkingSet(processes);
                var familyScopeComponentKeys = recoveryRequest is null
                    ? componentKeys
                    : componentKeys
                        .Concat(currentScopeComponentKeys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                var familyScopeProcesses = familyScopeComponentKeys
                    .SelectMany(key => components[key])
                    .ToArray();
                snapshots.Add(new NaturalStableStateSnapshot(
                    family.Key,
                    ApplicationStableScopeIdentity.For(family.Key, componentKeys),
                    componentKeys,
                    launchSignature,
                    workingSet,
                    processes.Any(process => process.IsForeground),
                    ScopeHasReliableLowActivity(processes, candidateIdleReadiness))
                {
                    RecoveryStartedAt = recoveryRequest?.StartedAt,
                    RecoveryDeadline = recoveryRequest?.Deadline,
                    RecoveryOrigin = recoveryRequest?.Origin ?? NaturalStableObservationOrigin.Unknown,
                    FamilyScopeKey = ApplicationStableScopeIdentity.For(family.Key, familyScopeComponentKeys),
                    FamilyScopeComponentKeys = familyScopeComponentKeys,
                    FamilyScopeLaunchSignature = NaturalStableLaunchSignature(familyScopeComponentKeys, components),
                    FamilyScopeWorkingSetBytes = SumWorkingSet(familyScopeProcesses),
                    FamilyScopeIsForeground = familyScopeProcesses.Any(process => process.IsForeground)
                });
            }
        }
        return snapshots;
    }

    private static long SumWorkingSet(IEnumerable<ProcessSnapshot> processes) =>
        processes.Aggregate(0L, (total, process) =>
        {
            var bytes = Math.Max(0, process.WorkingSetBytes);
            return bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
        });

    private static bool ScopeHasReliableLowActivity(
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyDictionary<int, CandidateIdleReadiness> readinessByProcess)
    {
        if (processes.Count == 0 || processes.Any(process => process.IsForeground)) return false;
        var totalWorkingSet = processes.Sum(process => Math.Max(0, process.WorkingSetBytes));
        var nonIdleWorkingSet = processes
            .Where(process => !readinessByProcess.TryGetValue(process.ProcessId, out var readiness) ||
                              !readiness.IsReady)
            .Sum(process => Math.Max(0, process.WorkingSetBytes));
        var tolerance = Math.Max(
            24L * 1024 * 1024,
            (long)Math.Round(totalWorkingSet * 0.10d));
        return processes.Any(process =>
                   readinessByProcess.TryGetValue(process.ProcessId, out var readiness) &&
                   readiness.IsReady) &&
               nonIdleWorkingSet <= tolerance;
    }

    public static long SuppressionLimitBytes(long referenceBytes, StableStateSuppressionSettings settings)
    {
        settings = settings.Normalize();
        var reference = Math.Max(0, referenceBytes);
        var growthMargin = Math.Max(
            settings.AbsoluteGrowthMarginBytes,
            (long)Math.Round(reference * settings.RelativeGrowthMargin));
        var dynamicLimit = reference > long.MaxValue - growthMargin ? long.MaxValue : reference + growthMargin;
        return Math.Min(dynamicLimit, settings.MaximumStableWorkingSetBytes);
    }

    private static string[] ResolveCurrentScopeComponentKeys(
        ProcessFamilySnapshot family,
        OptimizationSettings optimizationSettings,
        ProtectionRules protection,
        ProtectionContext protectionContext,
        IEnumerable<ApplicationStableLearningRecord>? records = null)
    {
        var unprotected = protection.FilterUnprotectedProcesses(family, protectionContext);
        if (unprotected is null) return Array.Empty<string>();
        var runningComponents = ApplicationComponentIdentity.GroupProcesses(family with
        {
            Processes = unprotected.Processes
                .Where(process => process.ProcessId != Environment.ProcessId &&
                                  !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath))
                .ToArray()
        });
        var structurallyEligible = unprotected.Processes
            .Where(process =>
                process.ProcessId != Environment.ProcessId &&
                !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath) &&
                process.WorkingSetBytes >= optimizationSettings.MinimumProcessWorkingSetBytes)
            .ToArray();
        var eligibleKeys = structurallyEligible.Sum(process => Math.Max(0, process.WorkingSetBytes)) <
                           optimizationSettings.MinimumFamilyWorkingSetBytes
            ? Array.Empty<string>()
            : ApplicationComponentIdentity.GroupProcesses(family with { Processes = structurallyEligible }).Keys
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var compatibleRecord = (records ?? Array.Empty<ApplicationStableLearningRecord>())
            .Where(record => record.ComponentKeys.Count > 0 &&
                             record.ComponentKeys.All(runningComponents.ContainsKey) &&
                             eligibleKeys.All(key => record.ComponentKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(record => record.StableLastObservedAt)
            .FirstOrDefault();
        return compatibleRecord is null
            ? eligibleKeys
            : compatibleRecord.ComponentKeys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ScopeComponentsAreRunning(
        ProcessFamilySnapshot family,
        IReadOnlyList<string> componentKeys,
        ProtectionRules protection,
        ProtectionContext protectionContext)
    {
        if (componentKeys.Count == 0) return false;
        var unprotected = protection.FilterUnprotectedProcesses(family, protectionContext);
        if (unprotected is null) return false;
        var running = ApplicationComponentIdentity.GroupProcesses(unprotected);
        return componentKeys.All(running.ContainsKey);
    }

    private static string NaturalStableLaunchSignature(
        IEnumerable<string> componentKeys,
        IReadOnlyDictionary<string, ProcessSnapshot[]> components)
    {
        var parts = componentKeys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(key => components[key]
                .Where(process => process.StartTimeFileTimeUtc is > 0)
                .Select(process => process.StartTimeFileTimeUtc!.Value)
                .DefaultIfEmpty()
                .Min() is var startedAt && startedAt > 0
                    ? $"{key}@{startedAt}"
                    : string.Empty)
            .ToArray();
        return parts.Length > 0 && parts.All(value => value.Length > 0)
            ? string.Join('|', parts)
            : string.Empty;
    }

    public static string CurrentNaturalStableLaunchSignature(
        ProcessFamilySnapshot family,
        IEnumerable<string> componentKeys)
    {
        var keys = componentKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var components = ApplicationComponentIdentity.GroupProcesses(family);
        return keys.Length > 0 && keys.All(components.ContainsKey)
            ? NaturalStableLaunchSignature(keys, components)
            : string.Empty;
    }
}

public sealed record NaturalStableStateSnapshot(
    string FamilyKey,
    string ScopeKey,
    IReadOnlyList<string> ComponentKeys,
    string LaunchSignature,
    long WorkingSetBytes,
    bool IsForeground,
    bool IsLowActivity)
{
    public DateTimeOffset? RecoveryStartedAt { get; init; }
    public DateTimeOffset? RecoveryDeadline { get; init; }
    public NaturalStableObservationOrigin RecoveryOrigin { get; init; }
    public string? FamilyScopeKey { get; init; }
    public IReadOnlyList<string> FamilyScopeComponentKeys { get; init; } = Array.Empty<string>();
    public string FamilyScopeLaunchSignature { get; init; } = string.Empty;
    public long FamilyScopeWorkingSetBytes { get; init; }
    public bool FamilyScopeIsForeground { get; init; }
}

public sealed record NaturalStableScopeRequest(
    string FamilyKey,
    IReadOnlyList<string> ComponentKeys,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset? Deadline { get; init; }
    public NaturalStableObservationOrigin Origin { get; init; }
}

public enum NaturalStableObservationOrigin
{
    Unknown,
    PostTrim,
    BackoffRecovery,
    HistoricalBoundedConfirmation
}

public enum StableObservationPhase
{
    Observing,
    ProvisionalValidation,
    GrowthReview
}

public sealed record NaturalStableObservationStatus(
    string ScopeKey,
    IReadOnlyList<string> ComponentKeys,
    NaturalStableObservationOrigin Origin,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    bool IsGrowthReview,
    long LatestWorkingSetBytes = 0,
    int ObservationCount = 0,
    DateTimeOffset? LastObservedAt = null,
    DateTimeOffset? ReevaluateAt = null,
    long? BaselineWorkingSetBytes = null,
    long? RequiredIncreaseBytes = null,
    bool LatestIsLowActivity = true)
{
    public StableObservationPhase Phase { get; init; } = IsGrowthReview
        ? StableObservationPhase.GrowthReview
        : StableObservationPhase.Observing;
    public DateTimeOffset? ValidationDeadline { get; init; }
    public DateTimeOffset? ContinuousStableSince { get; init; }
    public long? ValidationUpperLimitBytes { get; init; }
    public bool HasFiniteDeadline => Deadline != DateTimeOffset.MaxValue;
}

public sealed record NaturalStableReviewSchedule(
    DateTimeOffset NextReviewAt,
    int CompletedReviewCount,
    int InitialReviewTarget,
    int HighMigrationRecoveryCycleCount,
    int RequiredHighMigrationRecoveryCycles,
    bool AwaitingNewRecoveryCycle);

public sealed record NaturalStableTimedSampleProgress(
    DateTimeOffset ObservedAt,
    long WorkingSetBytes,
    bool IsLowActivity);

public sealed record NaturalStableValidationProgress(
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset? ContinuousStableSince,
    string FamilyScopeKey,
    string FamilyScopeLaunchSignature,
    long UpperLimitBytes,
    long BaselineFamilyWorkingSetBytes,
    long StableBytes,
    long StableMinimumBytes,
    long StableMaximumBytes,
    string RecoveryCycleId,
    bool BackoffObservation,
    bool CompletesBackoffObservation);

public sealed record NaturalStableGrowthReviewProgress(
    string FamilyScopeKey,
    IReadOnlyList<string> FamilyScopeComponentKeys,
    string FamilyScopeLaunchSignature,
    DateTimeOffset StartedAt,
    long BaselineFamilyWorkingSetBytes,
    long LatestFamilyWorkingSetBytes,
    long RequiredIncreaseBytes,
    DateTimeOffset LastObservedAt);

public sealed record NaturalStableObservationProgress(
    string FamilyKey,
    string ScopeKey,
    string LaunchSignature,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    IReadOnlyList<string> ComponentKeys,
    long MinimumBytes,
    long MaximumBytes,
    long LatestBytes,
    int ObservationCount,
    IReadOnlyList<NaturalStableTimedSampleProgress> WorkingSetSamples,
    TimeSpan StableDuration,
    TimeSpan TotalObservationDuration,
    DateTimeOffset LastObservedAt,
    bool AllowsNewBaseline,
    bool PreserveConvergedStatus,
    NaturalStableObservationOrigin Origin)
{
    public NaturalStableValidationProgress? Validation { get; init; }
    public NaturalStableGrowthReviewProgress? GrowthReview { get; init; }
}

public static class NaturalStableObservationPolicy
{
    private const double SustainedPeriodGrowthRatio = 0.05d;

    public static long? StableSampleBytes(IEnumerable<long>? samples)
    {
        var history = (samples ?? Array.Empty<long>())
            .Where(value => value > 0)
            .ToArray();
        if (history.Length < 2) return null;
        if (history.Length < 3)
            return HasMeaningfulGrowth(history[0], history[^1]) ? null : Median(history);
        var periodSize = history.Length / 3;

        var first = Median(history.Take(periodSize));
        var middle = Median(history.Skip(periodSize).Take(periodSize));
        var latest = Median(history.TakeLast(periodSize));
        return HasMeaningfulGrowth(first, middle) && HasMeaningfulGrowth(middle, latest)
            ? null
            : latest;
    }

    private static bool HasMeaningfulGrowth(long earlier, long later) =>
        earlier > 0 && later > earlier &&
        (later - earlier) / (double)earlier >= SustainedPeriodGrowthRatio;

    private static long Median(IEnumerable<long> samples) =>
        StableWorkingSetLearningPolicy.Median(samples.OrderBy(value => value).ToArray());
}

public static class StableAnchorLearningPolicy
{
    public static readonly TimeSpan PendingEvidenceLifetime = TimeSpan.FromDays(7);
    public const int RequiredMigrationRecoveryCycles = 3;
    public const int RequiredMigrationLaunches = 2;
    public static readonly TimeSpan RequiredSameLaunchMigrationSpan = TimeSpan.FromMinutes(30);
    public const long MinimumAnchorHighMarginBytes = 32L * 1024 * 1024;
    public const double RelativeAnchorHighMargin = 0.15d;

    public static ApplicationStableLearningRecord CommitSample(
        ApplicationStableLearningRecord record,
        ApplicationStableSample sample,
        int minimumSamples,
        int maximumSamples)
    {
        var limit = Math.Clamp(
            maximumSamples,
            1,
            StableWorkingSetLearningPolicy.MaximumRecentSamples);
        var normalizedSample = NormalizeSample(sample);
        var evidenceCutoff = normalizedSample.ObservedAt - PendingEvidenceLifetime;
        var samples = NormalizeSamples(record)
            .Where(item => !IsPendingEvidence(item, record.AnchorGeneration) || item.ObservedAt >= evidenceCutoff)
            .Append(normalizedSample)
            .ToArray();
        var generation = Math.Max(0, record.AnchorGeneration);
        var baseline = Math.Max(0, record.AnchorGenerationBaselineBytes);

        if (baseline <= 0 && InitialAnchorCluster(samples, Math.Max(1, minimumSamples)) is { } initial)
        {
            generation = Math.Max(1, generation);
            baseline = Median(initial.Select(item => item.WorkingSetBytes));
            samples = samples.Select(item => initial.Contains(item)
                ? item with { Generation = generation, PendingHigh = false }
                : item with { Generation = 0, PendingHigh = true }).ToArray();
        }
        else if (baseline > 0)
        {
            generation = Math.Max(1, generation);
            var latestIndex = samples.Length - 1;
            var latest = samples[latestIndex];
            var acceptedReferenceValues = samples
                .Take(latestIndex)
                .Where(item => item.Generation == generation && !item.PendingHigh)
                .Select(item => item.WorkingSetBytes)
                .ToArray();
            var classificationReference = acceptedReferenceValues.Length == 0
                ? baseline
                : Median(acceptedReferenceValues);
            samples[latestIndex] = latest with
            {
                Generation = generation,
                PendingHigh = IsHighSample(classificationReference, latest.WorkingSetBytes)
            };

            var classifiedLatest = samples[latestIndex];
            samples = samples.Where((item, index) =>
                    index == latestIndex ||
                    item.Generation != generation ||
                    !item.PendingHigh ||
                    !string.Equals(
                        item.RecoveryCycleId,
                        classifiedLatest.RecoveryCycleId,
                        StringComparison.Ordinal))
                .ToArray();

            if (TryMigrationCluster(samples, generation) is { } migration)
            {
                generation++;
                baseline = Median(migration.Select(item => item.WorkingSetBytes));
                samples = samples.Select(item => migration.Contains(item)
                    ? item with { Generation = generation, PendingHigh = false }
                    : item).ToArray();
            }
        }

        samples = TrimSamples(samples, generation, Math.Max(1, minimumSamples), limit);

        return record with
        {
            StableWorkingSetSamplesBytes = samples.Select(item => item.WorkingSetBytes).ToArray(),
            StableSamples = samples,
            AnchorGeneration = generation,
            AnchorGenerationBaselineBytes = baseline
        };
    }

    public static ApplicationStableLearningRecord ExpirePendingEvidence(
        ApplicationStableLearningRecord record,
        DateTimeOffset now)
    {
        var cutoff = now - PendingEvidenceLifetime;
        var samples = NormalizeSamples(record)
            .Where(item => !IsPendingEvidence(item, record.AnchorGeneration) || item.ObservedAt >= cutoff)
            .ToArray();
        if (samples.Length == NormalizeSamples(record).Count) return record;
        return record with
        {
            StableSamples = samples,
            StableWorkingSetSamplesBytes = samples.Select(item => item.WorkingSetBytes).ToArray()
        };
    }

    private static bool IsPendingEvidence(ApplicationStableSample sample, int anchorGeneration) =>
        sample.PendingHigh || anchorGeneration <= 0 || sample.Generation <= 0;

    public static long? EffectiveAnchorBytes(
        ApplicationStableLearningRecord record,
        int maximumSamples = StableWorkingSetLearningPolicy.DefaultRecentSamples)
    {
        var samples = NormalizeSamples(record)
            .TakeLast(Math.Clamp(
                maximumSamples,
                1,
                StableWorkingSetLearningPolicy.MaximumRecentSamples))
            .ToArray();
        if (samples.Length == 0) return null;
        var hasAnchorMetadata = (record.StableSamples ?? Array.Empty<ApplicationStableSample>()).Count > 0;
        if (hasAnchorMetadata &&
            (record.AnchorGenerationBaselineBytes <= 0 || record.AnchorGeneration <= 0))
        {
            return null;
        }
        if (record.AnchorGenerationBaselineBytes <= 0 || record.AnchorGeneration <= 0)
            return Median(samples.Select(item => item.WorkingSetBytes));

        var accepted = samples
            .Where(item => item.Generation == record.AnchorGeneration && !item.PendingHigh)
            .Select(item => item.WorkingSetBytes)
            .ToArray();
        return accepted.Length == 0
            ? record.AnchorGenerationBaselineBytes
            : Median(accepted);
    }

    public static StableSampleRange? ReferenceRange(
        ApplicationStableLearningRecord record,
        int maximumSamples = StableWorkingSetLearningPolicy.DefaultRecentSamples)
    {
        var samples = NormalizeSamples(record)
            .TakeLast(Math.Clamp(
                maximumSamples,
                1,
                StableWorkingSetLearningPolicy.MaximumRecentSamples))
            .ToArray();
        if (samples.Length == 0) return null;
        var centers = samples.Select(item => item.WorkingSetBytes).OrderBy(value => value).ToArray();
        return samples.Length == 0
            ? null
            : new StableSampleRange(
                samples.Min(item => item.MinimumWorkingSetBytes),
                samples.Max(item => item.MaximumWorkingSetBytes),
                Median(centers),
                samples.Length);
    }

    public static int AcceptedSampleCount(
        ApplicationStableLearningRecord record,
        int maximumSamples = StableWorkingSetLearningPolicy.DefaultRecentSamples)
    {
        var samples = NormalizeSamples(record)
            .TakeLast(Math.Clamp(
                maximumSamples,
                1,
                StableWorkingSetLearningPolicy.MaximumRecentSamples))
            .ToArray();
        var hasAnchorMetadata = (record.StableSamples ?? Array.Empty<ApplicationStableSample>()).Count > 0;
        if (hasAnchorMetadata &&
            (record.AnchorGeneration <= 0 || record.AnchorGenerationBaselineBytes <= 0))
        {
            return 0;
        }
        if (record.AnchorGeneration <= 0 || record.AnchorGenerationBaselineBytes <= 0)
            return samples.Length;
        return samples.Count(item =>
            item.Generation == record.AnchorGeneration && !item.PendingHigh);
    }

    public static int AcceptedSampleCountForLaunch(
        ApplicationStableLearningRecord record,
        string? launchSignature,
        int maximumSamples = StableWorkingSetLearningPolicy.DefaultRecentSamples)
    {
        if (string.IsNullOrWhiteSpace(launchSignature) ||
            record.AnchorGeneration <= 0 ||
            record.AnchorGenerationBaselineBytes <= 0)
            return 0;

        return NormalizeSamples(record)
            .TakeLast(Math.Clamp(
                maximumSamples,
                1,
                StableWorkingSetLearningPolicy.MaximumRecentSamples))
            .Count(item =>
                string.Equals(item.LaunchSignature, launchSignature, StringComparison.Ordinal) &&
                item.Generation == record.AnchorGeneration &&
                !item.PendingHigh);
    }

    public static int PendingHighRecoveryCycleCount(ApplicationStableLearningRecord record)
    {
        if (record.AnchorGeneration <= 0) return 0;
        var pending = NormalizeSamples(record)
            .Where(item => item.Generation == record.AnchorGeneration && item.PendingHigh)
            .OrderBy(item => item.WorkingSetBytes)
            .ToArray();
        var bestCycles = 0;
        var bestSamples = 0;
        var bestObservedAt = DateTimeOffset.MinValue;
        for (var start = 0; start < pending.Length; start++)
        {
            for (var end = start; end < pending.Length; end++)
            {
                if (!IsWithinAnchorBand(
                        pending[start].WorkingSetBytes,
                        pending[end].WorkingSetBytes)) break;
                var cluster = pending[start..(end + 1)];
                var cycles = cluster.Select(item => item.RecoveryCycleId)
                    .Distinct(StringComparer.Ordinal).Count();
                var latest = cluster.Max(item => item.ObservedAt);
                if (cycles > bestCycles ||
                    cycles == bestCycles && cluster.Length > bestSamples ||
                    cycles == bestCycles && cluster.Length == bestSamples && latest > bestObservedAt)
                {
                    bestCycles = cycles;
                    bestSamples = cluster.Length;
                    bestObservedAt = latest;
                }
            }
        }
        return bestCycles;
    }

    public static bool HasPendingHighSampleForRecoveryCycle(
        ApplicationStableLearningRecord record,
        string recoveryCycleId) =>
        !string.IsNullOrWhiteSpace(recoveryCycleId) &&
        NormalizeSamples(record).Any(item =>
            item.Generation == record.AnchorGeneration &&
            item.PendingHigh &&
            string.Equals(item.RecoveryCycleId, recoveryCycleId, StringComparison.Ordinal));

    public static ApplicationStableLearningRecord ReclassifyPendingHighSamples(
        ApplicationStableLearningRecord record)
    {
        if (record.AnchorGeneration <= 0 || record.AnchorGenerationBaselineBytes <= 0)
            return record;
        var samples = NormalizeSamples(record).ToArray();
        var accepted = samples
            .Where(item => item.Generation == record.AnchorGeneration && !item.PendingHigh)
            .Select(item => item.WorkingSetBytes)
            .ToArray();
        if (accepted.Length < 2) return record;
        var reference = Median(accepted);
        var changed = false;
        samples = samples.Select(item =>
        {
            if (item.Generation != record.AnchorGeneration ||
                !item.PendingHigh ||
                IsHighSample(reference, item.WorkingSetBytes)) return item;
            changed = true;
            return item with { PendingHigh = false };
        }).ToArray();
        return !changed
            ? record
            : record with
            {
                StableSamples = samples,
                StableWorkingSetSamplesBytes = samples.Select(item => item.WorkingSetBytes).ToArray()
            };
    }

    public static long ClampFixedAnchorBytes(
        long requestedBytes,
        long minimumBytes,
        long maximumBytes)
    {
        var minimum = Math.Max(1, minimumBytes);
        var maximum = Math.Max(minimum, maximumBytes);
        return Math.Clamp(requestedBytes, minimum, maximum);
    }

    public static IReadOnlyList<ApplicationStableSample> NormalizeSamples(
        ApplicationStableLearningRecord record)
    {
        var samples = (record.StableSamples ?? Array.Empty<ApplicationStableSample>())
            .Where(item => item.WorkingSetBytes > 0 && item.ObservedAt != default)
            .Select(NormalizeSample)
            .ToArray();
        if (samples.Length > 0) return samples;

        var legacyValues = (record.StableWorkingSetSamplesBytes ?? Array.Empty<long>())
            .Where(value => value > 0)
            .ToArray();
        if (legacyValues.Length == 0) return Array.Empty<ApplicationStableSample>();
        var observedAt = record.StableLastObservedAt ?? DateTimeOffset.MinValue.AddTicks(1);
        var launch = string.IsNullOrWhiteSpace(record.LastStableLaunchSignature)
            ? "legacy"
            : record.LastStableLaunchSignature.Trim();
        var generation = Math.Max(1, record.AnchorGeneration);
        return legacyValues.Select(value => new ApplicationStableSample(
            value,
            observedAt,
            launch,
            "legacy",
            generation,
            PendingHigh: false)).ToArray();
    }

    private static ApplicationStableSample[]? TryMigrationCluster(
        IReadOnlyList<ApplicationStableSample> samples,
        int generation)
    {
        var pending = samples
            .Where(item => item.Generation == generation && item.PendingHigh)
            .OrderBy(item => item.WorkingSetBytes)
            .ToArray();
        ApplicationStableSample[]? best = null;
        for (var start = 0; start < pending.Length; start++)
        {
            for (var end = start; end < pending.Length; end++)
            {
                if (!IsWithinAnchorBand(
                        pending[start].WorkingSetBytes,
                        pending[end].WorkingSetBytes)) break;
                var cluster = pending[start..(end + 1)];
                var recoveryCycles = cluster.Select(item => item.RecoveryCycleId)
                    .Distinct(StringComparer.Ordinal).Count();
                var launches = cluster.Select(item => item.LaunchSignature)
                    .Distinct(StringComparer.Ordinal).Count();
                var observationSpan = cluster.Max(item => item.ObservedAt) - cluster.Min(item => item.ObservedAt);
                if (recoveryCycles < RequiredMigrationRecoveryCycles ||
                    (launches < RequiredMigrationLaunches &&
                     observationSpan < RequiredSameLaunchMigrationSpan))
                {
                    continue;
                }
                if (best is null || cluster.Length > best.Length ||
                    cluster.Length == best.Length &&
                    cluster.Max(item => item.ObservedAt) > best.Max(item => item.ObservedAt))
                {
                    best = cluster;
                }
            }
        }
        return best;
    }

    private static ApplicationStableSample[]? InitialAnchorCluster(
        IReadOnlyList<ApplicationStableSample> samples,
        int minimumSamples)
    {
        var ordered = samples.OrderBy(item => item.WorkingSetBytes).ToArray();
        ApplicationStableSample[]? best = null;
        for (var start = 0; start < ordered.Length; start++)
        {
            for (var end = start; end < ordered.Length; end++)
            {
                if (!IsWithinAnchorBand(
                        ordered[start].WorkingSetBytes,
                        ordered[end].WorkingSetBytes)) break;
                var cluster = ordered[start..(end + 1)];
                if (cluster.Length < minimumSamples) continue;
                if (best is null || cluster.Length > best.Length ||
                    cluster.Length == best.Length &&
                    Median(cluster.Select(item => item.WorkingSetBytes)) <
                    Median(best.Select(item => item.WorkingSetBytes)))
                {
                    best = cluster;
                }
            }
        }
        return best;
    }

    private static ApplicationStableSample[] TrimSamples(
        IReadOnlyList<ApplicationStableSample> samples,
        int generation,
        int minimumSamples,
        int limit)
    {
        if (samples.Count <= limit) return samples.ToArray();
        if (generation <= 0) return samples.TakeLast(limit).ToArray();
        var indexed = samples.Select((item, index) => (Item: item, Index: index)).ToArray();
        var reserved = indexed
            .Where(entry => entry.Item.Generation == generation && !entry.Item.PendingHigh)
            .TakeLast(Math.Min(minimumSamples, limit))
            .ToArray();
        var reservedIndices = reserved.Select(entry => entry.Index).ToHashSet();
        return reserved
            .Concat(indexed.Where(entry => !reservedIndices.Contains(entry.Index))
                .TakeLast(limit - reserved.Length))
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();
    }

    private static ApplicationStableSample NormalizeSample(ApplicationStableSample sample) => sample with
    {
        WorkingSetBytes = Math.Max(0, sample.WorkingSetBytes),
        MinimumWorkingSetBytes = sample.MinimumWorkingSetBytes > 0
            ? Math.Min(sample.MinimumWorkingSetBytes, Math.Max(0, sample.WorkingSetBytes))
            : Math.Max(0, sample.WorkingSetBytes),
        MaximumWorkingSetBytes = Math.Max(
            Math.Max(0, sample.WorkingSetBytes),
            sample.MaximumWorkingSetBytes),
        LaunchSignature = string.IsNullOrWhiteSpace(sample.LaunchSignature)
            ? "unknown-launch"
            : sample.LaunchSignature.Trim(),
        RecoveryCycleId = string.IsNullOrWhiteSpace(sample.RecoveryCycleId)
            ? "unknown-recovery"
            : sample.RecoveryCycleId.Trim(),
        Generation = Math.Max(0, sample.Generation)
    };

    public static long AnchorHighMarginBytes(long baselineBytes) => Math.Max(
        MinimumAnchorHighMarginBytes,
        (long)Math.Round(Math.Max(0, baselineBytes) * RelativeAnchorHighMargin));

    public static bool IsHighSample(long baselineBytes, long sampleBytes)
    {
        var margin = AnchorHighMarginBytes(baselineBytes);
        var boundary = baselineBytes > long.MaxValue - margin
            ? long.MaxValue
            : baselineBytes + margin;
        return sampleBytes > boundary;
    }

    private static bool IsWithinAnchorBand(long leftBytes, long rightBytes)
    {
        var scale = Math.Max(Math.Max(0, leftBytes), Math.Max(0, rightBytes));
        return Math.Abs((double)leftBytes - rightBytes) <= AnchorHighMarginBytes(scale);
    }

    private static long Median(IEnumerable<long> values) =>
        StableWorkingSetLearningPolicy.Median(values.OrderBy(value => value).ToArray());
}

public static class StableWorkingSetLearningPolicy
{
    public const int DefaultRecentSamples = 9;
    public const int MaximumRecentSamples = 100;
    public const long MinimumConvergenceToleranceBytes = 8L * 1024 * 1024;
    public const long MaximumConvergenceToleranceBytes = 32L * 1024 * 1024;
    public const double RelativeConvergenceTolerance = 0.15d;

    public static bool IsConverged(long previousBytes, long currentBytes)
    {
        if (previousBytes <= 0 || currentBytes <= 0) return false;
        var tolerance = ConvergenceToleranceBytes(previousBytes, currentBytes);
        return Math.Abs((double)previousBytes - currentBytes) <= tolerance;
    }

    public static long ConvergenceToleranceBytes(long previousBytes, long currentBytes)
    {
        var scale = Math.Max(Math.Max(0, previousBytes), Math.Max(0, currentBytes));
        var relativeTolerance = (long)Math.Round(scale * RelativeConvergenceTolerance);
        return Math.Max(
            MinimumConvergenceToleranceBytes,
            Math.Min(MaximumConvergenceToleranceBytes, relativeTolerance));
    }

    public static long Midpoint(long left, long right) =>
        Math.Max(0, (long)Math.Round(left / 2d + right / 2d));

    public static IReadOnlyList<long> NormalizeSamples(
        IEnumerable<long>? samples,
        int maximumSamples = DefaultRecentSamples) =>
        NormalizeSampleHistory(samples, maximumSamples)
        .OrderBy(value => value)
        .ToArray();

    public static IReadOnlyList<long> NormalizeSampleHistory(
        IEnumerable<long>? samples,
        int maximumSamples = DefaultRecentSamples) =>
        (samples ?? Array.Empty<long>())
        .Where(value => value > 0)
        .TakeLast(Math.Clamp(maximumSamples, 1, MaximumRecentSamples))
        .ToArray();

    public static long Median(IReadOnlyList<long> sortedSamples)
    {
        if (sortedSamples.Count == 0) return 0;
        var middle = sortedSamples.Count / 2;
        return sortedSamples.Count % 2 != 0
            ? sortedSamples[middle]
            : Midpoint(sortedSamples[middle - 1], sortedSamples[middle]);
    }

    public static StableSampleRange? TrustedRange(
        IEnumerable<long>? samples,
        int minimumSamples,
        int maximumSamples = DefaultRecentSamples)
    {
        var history = NormalizeSampleHistory(samples, maximumSamples)
            .Select((value, index) => (Value: value, HistoryIndex: index))
            .OrderBy(item => item.Value)
            .ToArray();
        var required = Math.Max(1, minimumSamples);
        (int Start, int Count, int LatestHistoryIndex)? best = null;
        for (var start = 0; start < history.Length; start++)
        {
            for (var end = start; end < history.Length; end++)
            {
                if (!IsConverged(history[start].Value, history[end].Value)) break;
                var count = end - start + 1;
                if (count < required) continue;
                var latest = history[start..(end + 1)].Max(item => item.HistoryIndex);
                if (best is null || count > best.Value.Count ||
                    count == best.Value.Count && latest > best.Value.LatestHistoryIndex)
                {
                    best = (start, count, latest);
                }
            }
        }

        if (best is null) return null;
        var cluster = history
            .Skip(best.Value.Start)
            .Take(best.Value.Count)
            .Select(item => item.Value)
            .ToArray();
        return new StableSampleRange(cluster[0], cluster[^1], Median(cluster), cluster.Length);
    }

    public static StableSampleRange? ReferenceRange(
        IEnumerable<long>? samples,
        int minimumSamples,
        int maximumSamples = DefaultRecentSamples)
    {
        var normalized = NormalizeSamples(samples, maximumSamples);
        if (normalized.Count < Math.Max(1, minimumSamples)) return null;
        return new StableSampleRange(
            normalized[0],
            normalized[^1],
            Median(normalized),
            normalized.Count);
    }
}

public sealed record StableSampleRange(long MinimumBytes, long MaximumBytes, long MedianBytes, int SampleCount);

public sealed record ProtectionSuggestion(
    string SuggestionId,
    string FamilyKey,
    string ComponentKey,
    string? ExecutablePath,
    int SampleCount,
    double AverageReboundPercent,
    int BackoffTriggerCount,
    long AverageRetainedBytes);

public static class ProtectionSuggestionPolicy
{
    public static IReadOnlyList<ProtectionSuggestion> Create(
        IEnumerable<ApplicationBenefitLearningRecord> records,
        IReadOnlySet<string> dismissedSuggestionIds)
    {
        return records
            .Where(record =>
                !string.IsNullOrWhiteSpace(record.ComponentKey) &&
                record.LastLaunchContributionWeight > 0d &&
                record.ValidSampleCount >= 8 &&
                record.DistinctLaunchCount >= 3 &&
                record.RecentBackoffRate * record.ValidSampleCount >= 3 &&
                record.AverageReboundPercent >= 70)
            .Select(record => new ProtectionSuggestion(
                SuggestionId(record),
                record.FamilyKey,
                record.ComponentKey!,
                record.ExecutablePath,
                record.ValidSampleCount,
                record.AverageReboundPercent,
                (int)Math.Round(record.RecentBackoffRate * record.ValidSampleCount),
                record.AverageRetainedBytes))
            .Where(suggestion => !dismissedSuggestionIds.Contains(suggestion.SuggestionId))
            .OrderByDescending(suggestion => suggestion.BackoffTriggerCount)
            .ThenByDescending(suggestion => suggestion.AverageReboundPercent)
            .ToArray();
    }

    public static string SuggestionId(ApplicationBenefitLearningRecord record) =>
        $"protect|{record.ComponentKey}|high-rebound-v1".ToLowerInvariant();
}

public sealed record ApplicationOptimizationThresholdSuggestion(
    string SuggestionId,
    string FamilyKey,
    string ComponentKey,
    string? ExecutablePath,
    int ValidSampleCount,
    int DistinctLaunchCount,
    long LateWorkingSetP75Bytes,
    long TriggerThresholdBytes,
    double RecentBackoffRate,
    double AverageRetainedBytes);

public static class ApplicationOptimizationThresholdSuggestionPolicy
{
    private const int MinimumValidSamples = 8;
    private const int MinimumDistinctLaunches = 3;
    private const double MaximumBackoffRate = 0.5;
    private const double MaximumQuickReturnRate = 0.25;
    private const double SafetyMarginRatio = 0.05;
    private const long MinimumSafetyMarginBytes = 16L * 1024 * 1024;

    public static IReadOnlyList<ApplicationOptimizationThresholdSuggestion> Create(
        IEnumerable<ApplicationBenefitLearningRecord> records)
    {
        return records
            .Where(IsEligible)
            .Select(record =>
            {
                var p75 = Percentile(record.LateWorkingSetSamplesBytes, 0.75);
                var safetyMargin = Math.Max(
                    MinimumSafetyMarginBytes,
                    (long)Math.Round(p75 * SafetyMarginRatio));
                return new ApplicationOptimizationThresholdSuggestion(
                    SuggestionId(record),
                    record.FamilyKey,
                    record.ComponentKey!,
                    record.ExecutablePath,
                    record.ValidSampleCount,
                    record.DistinctLaunchCount,
                    p75,
                    checked(p75 + safetyMargin),
                    record.RecentBackoffRate,
                    record.AverageRetainedBytes);
            })
            .OrderByDescending(suggestion => suggestion.AverageRetainedBytes)
            .ToArray();
    }

    public static string SuggestionId(ApplicationBenefitLearningRecord record) =>
        $"rule-threshold|{record.ComponentKey}|p75-v1".ToLowerInvariant();

    private static bool IsEligible(ApplicationBenefitLearningRecord record)
    {
        return !string.IsNullOrWhiteSpace(record.ComponentKey) &&
               record.LastLaunchContributionWeight > 0d &&
               record.ValidSampleCount >= MinimumValidSamples &&
               record.DistinctLaunchCount >= MinimumDistinctLaunches &&
               record.LateWorkingSetSamplesBytes.Count >= MinimumValidSamples &&
               record.AverageRetainedBytes > 0 &&
               record.AverageReboundPercent < 70 &&
               record.RecentBackoffRate < MaximumBackoffRate &&
               record.RecentQuickReturnRate < MaximumQuickReturnRate;
    }

    private static long Percentile(IReadOnlyList<long> values, double percentile)
    {
        var ordered = values
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0) return 0;
        // Keep the reference conservative when the upper quartile contains tied samples.
        // This is advisory only; the user still confirms the resulting rule threshold.
        var index = Math.Clamp(
            Math.Clamp(percentile, 0, 1) * (ordered.Length + 1) - 0.5,
            0,
            ordered.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return ordered[lower];
        var fraction = index - lower;
        return Math.Max(0, (long)Math.Round(
            ordered[lower] + (ordered[upper] - ordered[lower]) * fraction));
    }
}
