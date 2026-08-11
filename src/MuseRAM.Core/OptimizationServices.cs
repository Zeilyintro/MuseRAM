namespace MuseRAM.Core;

public sealed class OptimizationReboundTracker
{
    public static readonly TimeSpan TrackingDuration = TimeSpan.FromSeconds(120);

    private ulong _baselineAvailableBytes;
    private long _netGainBytes;
    private DateTimeOffset? _trackingUntil;

    public bool HasResult { get; private set; }
    public double RatePercent { get; private set; }

    public void Begin(MemorySnapshot before, MemorySnapshot after, DateTimeOffset startedAt)
    {
        HasResult = true;
        _baselineAvailableBytes = before.AvailablePhysicalBytes;
        _netGainBytes = checked((long)after.AvailablePhysicalBytes - (long)before.AvailablePhysicalBytes);
        RatePercent = 0;
        _trackingUntil = _netGainBytes > 0 ? startedAt + TrackingDuration : null;
    }

    public double Observe(MemorySnapshot current, DateTimeOffset now)
    {
        if (!_trackingUntil.HasValue || now > _trackingUntil.Value) return RatePercent;

        var currentGain = checked((long)current.AvailablePhysicalBytes - (long)_baselineAvailableBytes);
        var reboundBytes = Math.Max(0L, _netGainBytes - Math.Max(0L, currentGain));
        RatePercent = Math.Clamp(reboundBytes / (double)_netGainBytes * 100d, 0d, 100d);
        return RatePercent;
    }

    public bool IsTracking(DateTimeOffset now) =>
        _trackingUntil.HasValue && now <= _trackingUntil.Value;
}

public sealed record ApplicationReboundDetail(
    string FamilyKey,
    string DisplayName,
    long ReleasedBytes,
    long RegainedBytes,
    double ReboundPercent,
    bool IsComplete);

public sealed class ApplicationReboundDetailTracker
{
    public static readonly TimeSpan TrackingDuration = TimeSpan.FromSeconds(120);
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _trackingUntil;
    private DateTimeOffset? _completedAt;

    public IReadOnlyList<ApplicationReboundDetail> Details => _observations.Values
        .Select(ToDetail)
        .OrderByDescending(detail => detail.RegainedBytes)
        .ThenByDescending(detail => detail.ReleasedBytes)
        .ThenBy(detail => detail.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DateTimeOffset? StartedAt => _startedAt;
    public DateTimeOffset? ExpectedCompletionAt => _trackingUntil;
    public DateTimeOffset? CompletedAt => _completedAt;
    public bool HasResults => _observations.Count > 0;

    public void BeginRun(DateTimeOffset now)
    {
        _observations.Clear();
        _startedAt = now;
        _trackingUntil = now + TrackingDuration;
        _completedAt = null;
    }

    public void Track(
        string familyKey,
        string displayName,
        long workingSetAfter,
        long releasedBytes,
        IReadOnlyCollection<int>? targetProcessIds = null,
        IReadOnlyCollection<int>? baselineFamilyProcessIds = null,
        IReadOnlyCollection<string>? targetExecutablePaths = null)
    {
        if (!_startedAt.HasValue || string.IsNullOrWhiteSpace(familyKey) || releasedBytes <= 0) return;
        _observations[familyKey] = new Observation(
            familyKey,
            string.IsNullOrWhiteSpace(displayName) ? familyKey : displayName,
            Math.Max(0, workingSetAfter),
            Math.Max(0, releasedBytes),
            targetProcessIds?.ToHashSet(),
            baselineFamilyProcessIds?.ToHashSet(),
            targetExecutablePaths?
                .Where(path => ExecutablePathIdentity.TryNormalize(path, out _))
                .Select(path =>
                {
                    ExecutablePathIdentity.TryNormalize(path, out var normalizedPath);
                    return normalizedPath;
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            RegainedBytes: 0,
            IsComplete: false);
    }

    public void Observe(IReadOnlyList<ProcessFamilySnapshot> families, DateTimeOffset now)
    {
        if (!_trackingUntil.HasValue || _observations.Count == 0 ||
            _observations.Values.All(observation => observation.IsComplete)) return;

        var complete = now >= _trackingUntil.Value;
        if (complete && !_completedAt.HasValue) _completedAt = now;
        foreach (var pair in _observations.ToArray())
        {
            var observation = pair.Value;
            var family = families.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, pair.Key, StringComparison.OrdinalIgnoreCase));
            var current = family is null
                ? 0
                : observation.TargetProcessIds is null
                    ? Math.Max(0, family.WorkingSetBytes)
                    : family.Processes
                        .Where(process =>
                            observation.TargetProcessIds.Contains(process.ProcessId) ||
                            (observation.BaselineFamilyProcessIds is not null &&
                             !observation.BaselineFamilyProcessIds.Contains(process.ProcessId) &&
                             ComponentMatches(observation, process)))
                        .Sum(process => Math.Max(0, process.WorkingSetBytes));
            var regained = Math.Clamp(
                current - observation.WorkingSetAfter,
                0,
                observation.ReleasedBytes);
            _observations[pair.Key] = observation with
            {
                RegainedBytes = regained,
                IsComplete = complete
            };
        }
    }

    public bool IsTracking(DateTimeOffset now) =>
        _trackingUntil.HasValue && now < _trackingUntil.Value &&
        _observations.Values.Any(observation => !observation.IsComplete);

    public TimeSpan Elapsed(DateTimeOffset now) => !_startedAt.HasValue
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Math.Clamp((now - _startedAt.Value).TotalSeconds, 0, TrackingDuration.TotalSeconds));

    private static ApplicationReboundDetail ToDetail(Observation observation) => new(
        observation.FamilyKey,
        observation.DisplayName,
        observation.ReleasedBytes,
        observation.RegainedBytes,
        Math.Clamp(observation.RegainedBytes / (double)Math.Max(1, observation.ReleasedBytes) * 100d, 0d, 100d),
        observation.IsComplete);

    private sealed record Observation(
        string FamilyKey,
        string DisplayName,
        long WorkingSetAfter,
        long ReleasedBytes,
        IReadOnlySet<int>? TargetProcessIds,
        IReadOnlySet<int>? BaselineFamilyProcessIds,
        IReadOnlySet<string>? TargetExecutablePaths,
        long RegainedBytes,
        bool IsComplete);

    private static bool ComponentMatches(Observation observation, ProcessSnapshot process) =>
        observation.TargetExecutablePaths is null ||
        ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var currentPath) &&
        observation.TargetExecutablePaths.Contains(currentPath);
}

public static class ApplicationFamilyGrouper
{
    private static readonly HashSet<string> GenericChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "cefsharp.browsersubprocess", "cefrendererprocess", "crashpad_handler",
        "helper", "host", "qtwebengineprocess", "renderer", "updater", "webviewhost", "worker"
    };

    public static IReadOnlyList<ProcessFamilySnapshot> Group(IReadOnlyList<ProcessSnapshot> processes)
    {
        if (processes.Count == 0) return Array.Empty<ProcessFamilySnapshot>();

        var byId = processes.ToDictionary(process => process.ProcessId);
        var buckets = new Dictionary<string, FamilyBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            var identity = ResolveIdentity(process, byId);
            if (!buckets.TryGetValue(identity.Key, out var bucket))
            {
                bucket = new FamilyBucket(identity.Directory, new List<ProcessSnapshot>());
                buckets.Add(identity.Key, bucket);
            }
            bucket.Processes.Add(process);
        }

        return buckets.Select(pair => CreateFamily(pair.Key, pair.Value)).ToArray();
    }

    private static ProcessFamilySnapshot CreateFamily(string key, FamilyBucket bucket)
    {
        var familyProcessIds = bucket.Processes
            .Select(process => process.ProcessId)
            .ToHashSet();
        var display = bucket.Processes
            .OrderByDescending(process => process.IsForeground)
            .ThenByDescending(process => process.HasVisibleWindow)
            .ThenByDescending(process => process.HasMinimizedWindow)
            .ThenBy(process =>
                process.ParentProcessId is int parentProcessId && familyProcessIds.Contains(parentProcessId)
                    ? 1
                    : 0)
            .ThenBy(process => NormalizeName(process.Name).Length)
            .ThenByDescending(process => process.WorkingSetBytes)
            .First();
        return new ProcessFamilySnapshot(key, display.Name, bucket.Directory, bucket.Processes);
    }

    private static FamilyIdentity ResolveIdentity(
        ProcessSnapshot process,
        IReadOnlyDictionary<int, ProcessSnapshot> byId)
        => ResolveIdentity(process, byId, new HashSet<int>());

    private static FamilyIdentity ResolveIdentity(
        ProcessSnapshot process,
        IReadOnlyDictionary<int, ProcessSnapshot> byId,
        HashSet<int> visited)
    {
        if (!visited.Add(process.ProcessId))
            return IdentityFromPath(process.ExecutablePath) ??
                new FamilyIdentity("process:" + process.ProcessId, null);

        var pathIdentity = IdentityFromPath(process.ExecutablePath);
        if (pathIdentity.HasValue &&
            process.ParentProcessId is int parentProcessId &&
            byId.TryGetValue(parentProcessId, out var pathParent) &&
            IsNestedApplicationChild(pathIdentity.Value, pathParent))
        {
            return ResolveIdentity(pathParent, byId, visited);
        }
        if (pathIdentity.HasValue) return pathIdentity.Value;

        visited.Remove(process.ProcessId);
        var current = process;
        while (visited.Add(current.ProcessId))
        {
            var currentPathIdentity = IdentityFromPath(current.ExecutablePath);
            if (currentPathIdentity.HasValue) return currentPathIdentity.Value;

            if (!current.ParentProcessId.HasValue ||
                !byId.TryGetValue(current.ParentProcessId.Value, out var parent) ||
                !CanInheritParent(current.Name, parent.Name))
            {
                break;
            }
            current = parent;
        }

        var name = NormalizeName(current.Name);
        return GenericChildren.Contains(name)
            ? new FamilyIdentity($"process:{current.ProcessId}", null)
            : new FamilyIdentity("name:" + name, null);
    }

    private static bool IsNestedApplicationChild(FamilyIdentity child, ProcessSnapshot parent)
    {
        if (string.IsNullOrWhiteSpace(child.Directory)) return false;
        var parentIdentity = IdentityFromPath(parent.ExecutablePath);
        if (!parentIdentity.HasValue || string.IsNullOrWhiteSpace(parentIdentity.Value.Directory)) return false;

        var childDirectory = NormalizePath(child.Directory).TrimEnd('\\');
        var parentDirectory = NormalizePath(parentIdentity.Value.Directory).TrimEnd('\\');
        return !IsSharedDirectory(parentDirectory) &&
            childDirectory.StartsWith(parentDirectory + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static FamilyIdentity? IdentityFromPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        if (InstalledApplicationIdentity.TryResolveWindowsPackage(executablePath, out var package))
            return new FamilyIdentity(package.FamilyKey, package.PackageRootDirectory);
        if (InstalledApplicationIdentity.TryResolveVersionedDirectory(executablePath, out var versioned))
            return new FamilyIdentity(versioned.FamilyKey, versioned.RootDirectory);

        try
        {
            var fullPath = Path.GetFullPath(executablePath.Trim());
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || IsSharedDirectory(directory))
            {
                return new FamilyIdentity("path:" + NormalizePath(fullPath), null);
            }

            return new FamilyIdentity("directory:" + NormalizePath(directory), directory);
        }
        catch
        {
            return new FamilyIdentity("path:" + NormalizePath(executablePath), null);
        }
    }

    private static bool CanInheritParent(string childName, string parentName)
    {
        var child = NormalizeName(childName);
        var parent = NormalizeName(parentName);
        if (child.Length == 0 || parent.Length == 0) return false;
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase) || GenericChildren.Contains(child)) return true;

        return Math.Min(child.Length, parent.Length) >= 4 &&
            (child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
             parent.StartsWith(child, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSharedDirectory(string directory)
    {
        var candidate = NormalizePath(directory).TrimEnd('\\');
        var windows = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
        if (windows.Length > 0 &&
            (candidate.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
             candidate.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path))
         .Select(path => NormalizePath(path).TrimEnd('\\'))
         .Any(path => candidate.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) => path.Trim().Replace('/', '\\').ToLowerInvariant();
    private static string NormalizeName(string name) =>
        name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name.Trim()[..^4].ToLowerInvariant()
            : name.Trim().ToLowerInvariant();

    private readonly record struct FamilyIdentity(string Key, string? Directory);
    private sealed record FamilyBucket(string? Directory, List<ProcessSnapshot> Processes);
}

public sealed class CandidateIdleTracker
{
    public const int MinimumReliableLowActivitySamples = 2;
    private readonly Dictionary<int, History> _history = new();

    public IReadOnlyDictionary<int, CandidateIdleReadiness> Observe(
        IReadOnlyList<ProcessFamilySnapshot> families,
        OptimizationSettings settings)
    {
        var result = new Dictionary<int, CandidateIdleReadiness>();
        var seenProcessIds = new HashSet<int>();
        foreach (var process in families.SelectMany(family => family.Processes))
        {
            seenProcessIds.Add(process.ProcessId);
            var hasSameIdentity = _history.TryGetValue(process.ProcessId, out var history) &&
                HasSameIdentity(history, process);
            var previousLowActivitySamples = hasSameIdentity
                ? history!.ConsecutiveReliableLowActivitySamples
                : 0;
            var isReliablyLowActivity = process.HasReliableActivitySample &&
                (ForegroundTrimPolicy.IsAllowed(settings) || !process.IsForeground) &&
                process.CpuPercent < settings.ActiveCpuThresholdPercent &&
                process.IoBytesPerSecond < settings.ActiveIoThresholdBytesPerSecond;
            var lowActivitySamples = isReliablyLowActivity
                ? previousLowActivitySamples + 1
                : 0;
            _history[process.ProcessId] = new History(
                process.StartTimeFileTimeUtc,
                process.Name,
                process.ExecutablePath,
                lowActivitySamples);
            result[process.ProcessId] = new CandidateIdleReadiness(
                process.ProcessId,
                lowActivitySamples,
                lowActivitySamples >= MinimumReliableLowActivitySamples);
        }

        foreach (var processId in _history.Keys.Where(processId => !seenProcessIds.Contains(processId)).ToArray())
            _history.Remove(processId);
        return result;
    }

    private static bool HasSameIdentity(History history, ProcessSnapshot process)
    {
        if (history.StartTimeFileTimeUtc.HasValue || process.StartTimeFileTimeUtc.HasValue)
            return history.StartTimeFileTimeUtc == process.StartTimeFileTimeUtc;
        return string.Equals(history.Name, process.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(history.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record History(
        long? StartTimeFileTimeUtc,
        string Name,
        string? ExecutablePath,
        int ConsecutiveReliableLowActivitySamples);
}

public sealed class OptimizationPlanner
{
    private const int MaximumAdaptiveApplications = 12;
    private static readonly string[] AuxiliaryProcessTokens =
    {
        "agent", "browsersubprocess", "crashpad", "helper", "renderer", "updater",
        "webview", "worker"
    };

    public OptimizationPlan CreatePlan(
        MemorySnapshot memory,
        IReadOnlyList<ProcessFamilySnapshot> families,
        OptimizationSettings settings,
        ProtectionRules protection,
        IReadOnlyDictionary<int, DateTimeOffset> lastTrimTimes,
        DateTimeOffset now,
        bool manual,
        IReadOnlyDictionary<string, BackgroundActivity>? activity = null,
        IReadOnlySet<string>? automaticBackoffFamilies = null,
        IReadOnlyDictionary<string, double>? outcomeMultipliers = null,
        bool intelligentPreview = false,
        IReadOnlyDictionary<string, double>? learningConfidences = null,
        IReadOnlyDictionary<int, CandidateIdleReadiness>? candidateIdleReadiness = null,
        bool enforceUnattendedSafety = false,
        IReadOnlySet<string>? pendingReboundObservationFamilies = null,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes = null,
        IReadOnlySet<string>? automaticBackoffComponents = null,
        IReadOnlySet<string>? pendingReboundObservationComponents = null,
        IReadOnlySet<string>? stableSuppressedComponents = null,
        IReadOnlyList<AutomaticOptimizationThresholdOverride>? automaticThresholdOverrides = null)
    {
        var unattendedSafety = !manual || enforceUnattendedSafety;
        if (!manual && !settings.IgnoreMemoryPressureThreshold && !HasMemoryPressure(memory, settings))
        {
            return new OptimizationPlan(
                false,
                "内存压力较低，暂不需要优化。",
                Array.Empty<OptimizationCandidate>(),
                OptimizationPlanOutcome.LowMemoryPressure);
        }

        var intelligentSelection = settings.IntelligentCandidateSelection &&
            (unattendedSafety || intelligentPreview);
        var allProcesses = families.SelectMany(family => family.Processes).ToArray();
        var protectionContext = protection.CreateContext(allProcesses);
        var parentById = allProcesses
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First().ParentProcessId);
        var activeProcessIds = allProcesses
            .Where(process => IsForegroundOrActive(process, settings))
            .Select(process => process.ProcessId)
            .ToHashSet();
        var evaluations = new List<CandidateEvaluation>(families.Count);
        var candidatesByPolicy = new List<OptimizationCandidate>(families.Count);
        foreach (var originalFamily in families)
        {
            var bypassProcessIds = automaticThresholdOverrides?
                .Where(item => item.BypassProtection &&
                               string.Equals(item.FamilyKey, originalFamily.Key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.ProcessIds)
                .ToHashSet() ?? new HashSet<int>();
            var unprotected = protection.FilterUnprotectedProcesses(originalFamily, protectionContext);
            var unprotectedProcessIds = unprotected?.Processes
                .Select(process => process.ProcessId)
                .ToHashSet() ?? new HashSet<int>();
            var allowedProcesses = originalFamily.Processes
                .Where(process => unprotectedProcessIds.Contains(process.ProcessId) ||
                                  bypassProcessIds.Contains(process.ProcessId))
                .ToArray();
            var family = allowedProcesses.Length == 0
                ? null
                : originalFamily with { Processes = allowedProcesses };
            if (family is null)
            {
                evaluations.Add(new CandidateEvaluation(
                    originalFamily.Key,
                    originalFamily.DisplayName,
                    false,
                    originalFamily.Processes.Count,
                    0,
                    new[] { CandidateExclusionReason.Protected })
                {
                    LegacyIdleScore = 0,
                    IdleConfidenceScore = 0,
                    TotalWorkingSetBytes = 0
                });
                continue;
            }

            var initialReasons = family.Processes.Count < originalFamily.Processes.Count
                ? new[] { CandidateExclusionReason.Protected }
                : Array.Empty<CandidateExclusionReason>();
            var familyComponentKeys = ApplicationComponentIdentity.GroupProcesses(family).Keys;
            var hasBlockedComponent = automaticBackoffComponents is not null &&
                                      familyComponentKeys.Any(automaticBackoffComponents.Contains);
            if (unattendedSafety &&
                (automaticBackoffFamilies?.Contains(family.Key) == true || hasBlockedComponent))
            {
                evaluations.Add(new CandidateEvaluation(
                    family.Key,
                    family.DisplayName,
                    false,
                    originalFamily.Processes.Count,
                    0,
                    initialReasons.Append(CandidateExclusionReason.AutomaticBackoff).ToArray())
                {
                    LegacyIdleScore = 0,
                    IdleConfidenceScore = 0,
                    TotalWorkingSetBytes = family.WorkingSetBytes
                });
                continue;
            }
            var hasPendingComponent = pendingReboundObservationComponents is not null &&
                                      familyComponentKeys.Any(pendingReboundObservationComponents.Contains);
            if (unattendedSafety &&
                (pendingReboundObservationFamilies?.Contains(family.Key) == true || hasPendingComponent))
            {
                evaluations.Add(new CandidateEvaluation(
                    family.Key,
                    family.DisplayName,
                    false,
                    originalFamily.Processes.Count,
                    0,
                    initialReasons.Append(CandidateExclusionReason.ReboundObservationPending).ToArray())
                {
                    LegacyIdleScore = 0,
                    IdleConfidenceScore = 0,
                    TotalWorkingSetBytes = family.WorkingSetBytes
                });
                continue;
            }

            var assessment = CreateCandidate(
                family,
                originalFamily.Processes.Count,
                settings,
                lastTrimTimes,
                now,
                activeProcessIds,
                parentById,
                activity?.GetValueOrDefault(family.Key),
                initialReasons,
                unattendedSafety,
                candidateIdleReadiness,
                lastTrimProcessStartTimes,
                automaticBackoffComponents,
                pendingReboundObservationComponents,
                stableSuppressedComponents,
                automaticThresholdOverrides?.Where(item =>
                    string.Equals(item.FamilyKey, family.Key, StringComparison.OrdinalIgnoreCase)).ToArray());
            evaluations.Add(assessment.Evaluation);
            if (assessment.Candidate is not null) candidatesByPolicy.Add(assessment.Candidate);
        }
        var orderedCandidates = intelligentSelection
            ? BenefitAwareRanking.OrderCandidates(candidatesByPolicy, outcomeMultipliers)
            : candidatesByPolicy
                .OrderByDescending(candidate => candidate.PotentialReleaseBytes)
                .ThenBy(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var limit = CalculateCandidateLimit(memory, settings, orderedCandidates.Length);
        var candidates = limit <= 0
            ? orderedCandidates
            : orderedCandidates.Take(limit).ToArray();

        var plan = candidates.Length == 0
            ? new OptimizationPlan(
                false,
                "没有找到符合当前策略的可优化应用。",
                candidates,
                OptimizationPlanOutcome.NoCandidates)
            : new OptimizationPlan(
                true,
                $"已找到 {candidates.Length} 个可优化候选应用。",
                candidates,
                OptimizationPlanOutcome.CandidatesFound);
        return plan with { CandidateEvaluations = evaluations };
    }

    private static CandidateAssessment CreateCandidate(
        ProcessFamilySnapshot family,
        int originalProcessCount,
        OptimizationSettings settings,
        IReadOnlyDictionary<int, DateTimeOffset> lastTrimTimes,
        DateTimeOffset now,
        IReadOnlySet<int> activeProcessIds,
        IReadOnlyDictionary<int, int?> parentById,
        BackgroundActivity? activity,
        IEnumerable<CandidateExclusionReason> initialReasons,
        bool requireIdleConfirmation,
        IReadOnlyDictionary<int, CandidateIdleReadiness>? candidateIdleReadiness,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes,
        IReadOnlySet<string>? automaticBackoffComponents,
        IReadOnlySet<string>? pendingReboundObservationComponents,
        IReadOnlySet<string>? stableSuppressedComponents,
        IReadOnlyList<AutomaticOptimizationThresholdOverride>? thresholdOverrides)
    {
        var reasons = initialReasons.ToHashSet();
        var familyHasActiveProcess = family.Processes.Any(process =>
            activeProcessIds.Contains(process.ProcessId));
        var familyBackgroundWaitSatisfied = activity is not null &&
            activity.IdleFor >= settings.VisibleWindowIdleDelay;
        var isVisibleWindowWaiting = !ForegroundTrimPolicy.IsAllowed(settings) &&
            family.HasVisibleWindow &&
            !family.HasForegroundProcess &&
            !settings.QuickCandidateSelection &&
            settings.VisibleWindowIdleDelay > TimeSpan.Zero &&
            !familyBackgroundWaitSatisfied &&
            (!family.LastForegroundAt.HasValue ||
             now - family.LastForegroundAt.Value < settings.VisibleWindowIdleDelay);
        if (isVisibleWindowWaiting)
        {
            reasons.Add(CandidateExclusionReason.VisibleWindowWait);
        }

        var overrides = thresholdOverrides ?? Array.Empty<AutomaticOptimizationThresholdOverride>();
        var overriddenProcessIds = overrides.SelectMany(item => item.ProcessIds).ToHashSet();
        var satisfiedOverrideProcessIds = overrides
            .Where(item => family.Processes
                .Where(process => item.ProcessIds.Contains(process.ProcessId) && process.HasReliableActivitySample)
                .Sum(process => Math.Max(0, process.WorkingSetBytes)) >= item.ThresholdBytes)
            .SelectMany(item => item.ProcessIds)
            .ToHashSet();
        if (overriddenProcessIds.Except(satisfiedOverrideProcessIds).Any())
            reasons.Add(CandidateExclusionReason.BelowFamilyWorkingSet);

        var normalTargets = new List<ProcessSnapshot>(family.Processes.Count);
        var overrideTargets = new List<ProcessSnapshot>(family.Processes.Count);
        foreach (var process in family.Processes)
        {
            var overridden = overriddenProcessIds.Contains(process.ProcessId);
            var overrideSatisfied = satisfiedOverrideProcessIds.Contains(process.ProcessId);
            if (overridden && !overrideSatisfied) continue;
            var processReasons = ProcessExclusionReasons(
                process,
                settings,
                lastTrimTimes,
                now,
                activeProcessIds,
                parentById,
                familyHasActiveProcess,
                requireIdleConfirmation,
                candidateIdleReadiness,
                lastTrimProcessStartTimes,
                family.Key,
                automaticBackoffComponents,
                pendingReboundObservationComponents,
                stableSuppressedComponents,
                ignoreWorkingSetMinimum: overrideSatisfied);
            if (processReasons.Count == 0)
            {
                if (overrideSatisfied) overrideTargets.Add(process);
                else normalTargets.Add(process);
            }
            else reasons.UnionWith(processReasons);
        }
        if (normalTargets.Sum(process => Math.Max(0, process.WorkingSetBytes)) <
            settings.MinimumFamilyWorkingSetBytes)
        {
            if (normalTargets.Count > 0) reasons.Add(CandidateExclusionReason.BelowFamilyWorkingSet);
            normalTargets.Clear();
        }
        var targets = normalTargets.Concat(overrideTargets).ToArray();
        if (targets.Length == 0)
            return Assessment(family, originalProcessCount, targets, null, reasons);

        var targetFamily = new ProcessFamilySnapshot(
            family.Key,
            family.DisplayName,
            family.ExecutableDirectory,
            targets.ToArray());
        if (isVisibleWindowWaiting)
            return Assessment(family, originalProcessCount, targets, null, reasons);
        var candidate = new OptimizationCandidate(
            family,
            targets.ToArray(),
            targetFamily.IdleConfidenceScore,
            targets.Sum(process => Math.Max(0, process.WorkingSetBytes)),
            BuildReason(targetFamily));
        return Assessment(family, originalProcessCount, targets, candidate, reasons);
    }

    private static IReadOnlyList<CandidateExclusionReason> ProcessExclusionReasons(
        ProcessSnapshot process,
        OptimizationSettings settings,
        IReadOnlyDictionary<int, DateTimeOffset> lastTrimTimes,
        DateTimeOffset now,
        IReadOnlySet<int> activeProcessIds,
        IReadOnlyDictionary<int, int?> parentById,
        bool familyHasActiveProcess,
        bool requireIdleConfirmation,
        IReadOnlyDictionary<int, CandidateIdleReadiness>? candidateIdleReadiness,
        IReadOnlyDictionary<int, long>? lastTrimProcessStartTimes,
        string familyKey,
        IReadOnlySet<string>? automaticBackoffComponents,
        IReadOnlySet<string>? pendingReboundObservationComponents,
        IReadOnlySet<string>? stableSuppressedComponents,
        bool ignoreWorkingSetMinimum)
    {
        var reasons = new List<CandidateExclusionReason>();
        var componentKey = ApplicationComponentIdentity.ForProcess(familyKey, process);
        if (requireIdleConfirmation && automaticBackoffComponents?.Contains(componentKey) == true)
            reasons.Add(CandidateExclusionReason.AutomaticBackoff);
        if (requireIdleConfirmation && pendingReboundObservationComponents?.Contains(componentKey) == true)
            reasons.Add(CandidateExclusionReason.ReboundObservationPending);
        if (requireIdleConfirmation && stableSuppressedComponents?.Contains(componentKey) == true)
            reasons.Add(CandidateExclusionReason.StableStateSuppression);
        if (process.ProcessId == Environment.ProcessId) reasons.Add(CandidateExclusionReason.MuseRamProcess);
        if (!ignoreWorkingSetMinimum && process.WorkingSetBytes < settings.MinimumProcessWorkingSetBytes)
            reasons.Add(CandidateExclusionReason.BelowProcessWorkingSet);
        if (!process.HasReliableActivitySample) reasons.Add(CandidateExclusionReason.UnreliableActivitySample);
        if (!ForegroundTrimPolicy.IsAllowed(settings) && process.IsForeground)
            reasons.Add(CandidateExclusionReason.Foreground);
        if (process.CpuPercent >= settings.ActiveCpuThresholdPercent)
            reasons.Add(CandidateExclusionReason.CurrentCpuActivity);
        if (process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond)
            reasons.Add(CandidateExclusionReason.CurrentIoActivity);
        var requiredLowActivitySamples = settings.QuickCandidateSelection
            ? 1
            : CandidateIdleTracker.MinimumReliableLowActivitySamples;
        if (requireIdleConfirmation && candidateIdleReadiness is not null &&
            (!candidateIdleReadiness.TryGetValue(process.ProcessId, out var readiness) ||
             readiness.ConsecutiveReliableLowActivitySamples < requiredLowActivitySamples))
            reasons.Add(CandidateExclusionReason.IdleConfirmationPending);
        if (!CanTrimInFamilyContext(
                process,
                activeProcessIds,
                parentById,
                familyHasActiveProcess,
                settings))
            reasons.Add(CandidateExclusionReason.ActiveProcessRelationship);
        if (ProcessTrimHistoryPolicy.IsCoolingDown(
                process,
                lastTrimTimes,
                lastTrimProcessStartTimes,
                now,
                settings.ProcessCooldown))
            reasons.Add(CandidateExclusionReason.ProcessCooldown);
        return reasons;
    }

    private static CandidateAssessment Assessment(
        ProcessFamilySnapshot family,
        int processCount,
        IReadOnlyCollection<ProcessSnapshot> targets,
        OptimizationCandidate? candidate,
        IEnumerable<CandidateExclusionReason> reasons)
    {
        var scoredFamily = new ProcessFamilySnapshot(
            family.Key,
            family.DisplayName,
            family.ExecutableDirectory,
            targets.ToArray());
        return new CandidateAssessment(
            candidate,
            new CandidateEvaluation(
                family.Key,
                family.DisplayName,
                candidate is not null,
                processCount,
                targets.Count,
                reasons.OrderBy(reason => reason).ToArray())
            {
                LegacyIdleScore = scoredFamily.IdleScore,
                IdleConfidenceScore = scoredFamily.IdleConfidenceScore,
                TargetWorkingSetBytes = scoredFamily.WorkingSetBytes,
                TotalWorkingSetBytes = family.WorkingSetBytes,
                TargetProcessIds = targets.Select(process => process.ProcessId).ToArray()
            });
    }

    private static bool CanTrimInFamilyContext(
        ProcessSnapshot process,
        IReadOnlySet<int> activeProcessIds,
        IReadOnlyDictionary<int, int?> parentById,
        bool familyHasActiveProcess,
        OptimizationSettings settings)
    {
        if (!settings.AllowIndependentBackgroundProcessTrim && familyHasActiveProcess) return false;
        var activeAncestors = activeProcessIds.Any(activeId =>
            IsAncestor(activeId, process.ProcessId, parentById));
        var activeDescendants = activeProcessIds.Any(activeId =>
            IsAncestor(process.ProcessId, activeId, parentById));
        if (!activeAncestors && !activeDescendants) return true;
        if (settings.EnhancedSafety || activeDescendants) return false;

        return IsLikelyAuxiliary(process.Name) &&
            process.CpuPercent <= 0.5 &&
            process.IoBytesPerSecond <= 256d * 1024;
    }

    private static bool IsForegroundOrActive(ProcessSnapshot process, OptimizationSettings settings) =>
        process.IsForeground ||
        process.CpuPercent >= settings.ActiveCpuThresholdPercent ||
        process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond;

    private static bool IsLikelyAuxiliary(string processName)
    {
        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return AuxiliaryProcessTokens.Any(token =>
            normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAncestor(
        int possibleAncestorId,
        int processId,
        IReadOnlyDictionary<int, int?> parentById)
    {
        var current = parentById.GetValueOrDefault(processId);
        var visited = new HashSet<int>();
        for (var depth = 0; current.HasValue && depth < 16 && visited.Add(current.Value); depth++)
        {
            if (current.Value == possibleAncestorId) return true;
            current = parentById.GetValueOrDefault(current.Value);
        }
        return false;
    }

    private static bool HasExcessiveActivity(ProcessSnapshot process, OptimizationSettings settings)
        => process.CpuPercent >= settings.ActiveCpuThresholdPercent ||
           process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond;

    public static bool HasMemoryPressure(MemorySnapshot memory, OptimizationSettings settings)
    {
        return memory.AvailablePhysicalBytes < CalculateEffectiveThreshold(memory, settings);
    }

    private static int CalculateCandidateLimit(MemorySnapshot memory, OptimizationSettings settings, int candidateCount)
    {
        if (settings.MaxApplications <= 0) return 0;

        var configuredLimit = settings.MaxApplications;
        if (!IsSevereMemoryPressure(memory, settings))
            return Math.Min(configuredLimit, candidateCount);

        return Math.Min(candidateCount, Math.Min(
            MaximumAdaptiveApplications,
            Math.Max(configuredLimit + 2, configuredLimit * 2)));
    }

    public static bool IsSevereMemoryPressure(
        MemorySnapshot memory,
        OptimizationSettings settings)
    {
        if (settings.IgnoreMemoryPressureThreshold) return false;
        return IsSevereMemoryPressureCore(memory, settings);
    }

    public static bool IsSevereMemoryPressureRegardlessOfOptimizationOverride(
        MemorySnapshot memory,
        OptimizationSettings settings) =>
        IsSevereMemoryPressureCore(memory, settings with { IgnoreMemoryPressureThreshold = false });

    private static bool IsSevereMemoryPressureCore(
        MemorySnapshot memory,
        OptimizationSettings settings)
    {
        var threshold = CalculateEffectiveThreshold(memory, settings);
        if (threshold == 0 && memory.TotalPhysicalBytes > 0)
            threshold = memory.TotalPhysicalBytes / 5;
        return memory.LoadPercent >= 90 &&
            threshold > 0 &&
            memory.AvailablePhysicalBytes <= threshold / 2;
    }

    private static ulong CalculateEffectiveThreshold(MemorySnapshot memory, OptimizationSettings settings)
    {
        if (settings.IgnoreMemoryPressureThreshold) return 0;
        if (settings.TriggerAvailablePercent <= 0) return settings.TriggerAvailableBytes;

        var percent = (ulong)Math.Clamp(settings.TriggerAvailablePercent, 1, 95);
        var percentThreshold = memory.TotalPhysicalBytes * percent / 100;
        return settings.TriggerAvailableBytes == 0
            ? percentThreshold
            : Math.Min(settings.TriggerAvailableBytes, percentThreshold);
    }

    private static string BuildReason(ProcessFamilySnapshot family) =>
        $"闲置可信度 {family.IdleConfidenceScore:0}，收益基础 {family.WorkingSetBytes / (1024d * 1024d):0} MB";

    private sealed record CandidateAssessment(
        OptimizationCandidate? Candidate,
        CandidateEvaluation Evaluation);
}

public static class BenefitAwareRanking
{
    public static OptimizationCandidate[] OrderCandidates(
        IReadOnlyList<OptimizationCandidate> candidates,
        IReadOnlyDictionary<string, double>? outcomeMultipliers)
    {
        return candidates
            .OrderByDescending(candidate => ExpectedRetainedBytes(candidate, outcomeMultipliers))
            .ThenByDescending(candidate => candidate.PotentialReleaseBytes)
            .ThenBy(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static double ExpectedRetainedBytes(
        OptimizationCandidate candidate,
        IReadOnlyDictionary<string, double>? outcomeMultipliers) =>
        candidate.PotentialReleaseBytes * Math.Clamp(
            outcomeMultipliers?.GetValueOrDefault(candidate.Family.Key) ?? 1d,
            0d,
            1d);

}

public sealed class BackgroundActivityTracker
{
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromSeconds(60);
    public const int MinimumSamples = 5;
    public const double DeepReleaseActiveCpuThresholdPercent = 1.5;
    public const double DeepReleaseActiveIoThresholdBytesPerSecond = 256d * 1024;
    private readonly Dictionary<string, History> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _resetIdleOnBackgroundActivity;
    private double? _activeCpuThresholdPercent;
    private double? _activeIoThresholdBytesPerSecond;

    public BackgroundActivityTracker(bool resetIdleOnBackgroundActivity = true)
    {
        _resetIdleOnBackgroundActivity = resetIdleOnBackgroundActivity;
    }

    public void RestoreProgress(
        string familyKey,
        TimeSpan observedFor,
        TimeSpan idleFor,
        int samples,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(familyKey) ||
            observedFor < TimeSpan.Zero || idleFor < TimeSpan.Zero || samples < 0)
        {
            return;
        }

        observedFor = NonNegative(observedFor);
        idleFor = NonNegative(idleFor > observedFor ? observedFor : idleFor);
        if (_history.TryGetValue(familyKey, out var current))
        {
            observedFor = observedFor > now - current.FirstObserved
                ? observedFor
                : NonNegative(now - current.FirstObserved);
            idleFor = idleFor > now - current.LastIdleReset
                ? idleFor
                : NonNegative(now - current.LastIdleReset);
            samples = Math.Max(samples, current.Samples);
        }
        _history[familyKey] = new History(
            now - observedFor,
            now - idleFor,
            samples,
            ConsecutiveActiveSamples: 0);
    }

    public IReadOnlyDictionary<string, BackgroundActivity> Observe(
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset now) => Observe(
            families,
            now,
            DeepReleaseActiveCpuThresholdPercent,
            DeepReleaseActiveIoThresholdBytesPerSecond);

    public IReadOnlyDictionary<string, BackgroundActivity> Observe(
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset now,
        double activeCpuThresholdPercent,
        double activeIoThresholdBytesPerSecond)
    {
        activeCpuThresholdPercent = Math.Max(0, activeCpuThresholdPercent);
        activeIoThresholdBytesPerSecond = Math.Max(0, activeIoThresholdBytesPerSecond);
        if (_resetIdleOnBackgroundActivity &&
            (_activeCpuThresholdPercent != activeCpuThresholdPercent ||
             _activeIoThresholdBytesPerSecond != activeIoThresholdBytesPerSecond))
        {
            _history.Clear();
        }
        _activeCpuThresholdPercent = activeCpuThresholdPercent;
        _activeIoThresholdBytesPerSecond = activeIoThresholdBytesPerSecond;

        var result = new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            seen.Add(family.Key);
            var isForeground = family.HasForegroundProcess;
            var reliableProcesses = family.Processes
                .Where(process => process.HasReliableActivitySample)
                .ToArray();
            if (!_history.TryGetValue(family.Key, out var history))
                history = new History(now, now, 0, 0);
            if (reliableProcesses.Length == 0)
            {
                if (_resetIdleOnBackgroundActivity)
                    history = new History(now, now, 0, 0);
                else if (isForeground)
                    history = history with { LastIdleReset = now };
                _history[family.Key] = history;
                result[family.Key] = new BackgroundActivity(
                    family.Key,
                    isForeground
                        ? BackgroundActivityState.Working
                        : family.HasVisibleWindow
                        ? BackgroundActivityState.Visible
                        : BackgroundActivityState.Observing,
                    NonNegative(now - history.FirstObserved),
                    NonNegative(now - history.LastIdleReset),
                    history.Samples);
                continue;
            }

            var active = isForeground ||
                reliableProcesses.Sum(process => Math.Max(0, process.CpuPercent)) >= activeCpuThresholdPercent ||
                reliableProcesses.Sum(process => Math.Max(0, process.IoBytesPerSecond)) >= activeIoThresholdBytesPerSecond;
            var consecutiveActiveSamples = active ? history.ConsecutiveActiveSamples + 1 : 0;
            var confirmedBackgroundActivity = active &&
                (!family.HasMinimizedWindow ||
                 family.HasVisibleWindow ||
                 isForeground ||
                 consecutiveActiveSamples >= 2);
            history = history with
            {
                LastIdleReset = (_resetIdleOnBackgroundActivity ? confirmedBackgroundActivity : isForeground)
                    ? now
                    : history.LastIdleReset,
                Samples = history.Samples + 1,
                ConsecutiveActiveSamples = consecutiveActiveSamples
            };
            _history[family.Key] = history;

            var observed = NonNegative(now - history.FirstObserved);
            var idle = NonNegative(now - history.LastIdleReset);
            var state = active
                ? BackgroundActivityState.Working
                : observed >= MinimumObservation && idle >= MinimumObservation && history.Samples >= MinimumSamples
                    ? BackgroundActivityState.Idle
                    : family.HasVisibleWindow
                        ? BackgroundActivityState.Visible
                        : BackgroundActivityState.Observing;
            result[family.Key] = new BackgroundActivity(family.Key, state, observed, idle, history.Samples);
        }

        foreach (var key in _history.Keys.Where(key => !seen.Contains(key)).ToArray()) _history.Remove(key);
        return result;
    }

    public static IReadOnlyList<DeepReleaseCandidate> CreateDeepReleaseCandidates(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, BackgroundActivity> activity,
        ProtectionRules protection)
    {
        var protectionContext = protection.CreateContext(families.SelectMany(family => family.Processes));
        return families
            .Select(family => protection.FilterUnprotectedProcesses(family, protectionContext))
            .Where(family => family is not null)
            .Select(family => family!)
            .Where(family => family.Processes.All(process => process.ProcessId != Environment.ProcessId))
            .Where(family => family.Processes.All(process => !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath)))
            .Select(family => CreateDeepReleaseCandidate(family, activity))
            .Where(candidate => candidate.Family.WorkingSetBytes >=
                (candidate.Family.HasVisibleWindow ? 32L.MiB() : 12L.MiB()))
            .OrderBy(candidate => ActivitySortOrder(candidate.Activity.State))
            .ThenByDescending(candidate => candidate.Family.WorkingSetBytes)
            .ThenBy(candidate => candidate.Family.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
    }

    private static DeepReleaseCandidate CreateDeepReleaseCandidate(
        ProcessFamilySnapshot family,
        IReadOnlyDictionary<string, BackgroundActivity> activity)
    {
        var assessment = activity.GetValueOrDefault(family.Key) ??
            new BackgroundActivity(family.Key, BackgroundActivityState.Observing, TimeSpan.Zero, TimeSpan.Zero, 0);
        var isActive = !family.HasReliableActivitySample ||
            family.CpuPercent >= DeepReleaseExecutionSafetyPolicy.ActiveCpuThresholdPercent ||
            family.IoBytesPerSecond >= DeepReleaseExecutionSafetyPolicy.ActiveIoThresholdBytesPerSecond;
        var isSuggested = family.WorkingSetBytes >= 96L.MiB() &&
            !family.HasForegroundProcess &&
            !family.HasVisibleWindow &&
            !isActive &&
            assessment.State == BackgroundActivityState.Idle;
        return new DeepReleaseCandidate(family, assessment, isSuggested);
    }

    private static int ActivitySortOrder(BackgroundActivityState state) => state switch
    {
        BackgroundActivityState.Idle => 0,
        BackgroundActivityState.Observing => 1,
        BackgroundActivityState.Working => 2,
        BackgroundActivityState.Visible => 3,
        _ => 4
    };

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private sealed record History(
        DateTimeOffset FirstObserved,
        DateTimeOffset LastIdleReset,
        int Samples,
        int ConsecutiveActiveSamples);
}

public static class CandidatePreviewPolicy
{
    private static readonly IReadOnlySet<CandidateExclusionReason> LifecycleReasons =
        new HashSet<CandidateExclusionReason>
        {
            CandidateExclusionReason.AutomaticBackoff,
            CandidateExclusionReason.ReboundObservationPending
        };

    private static readonly IReadOnlySet<CandidateExclusionReason> TemporaryReasons =
        new HashSet<CandidateExclusionReason>
        {
            CandidateExclusionReason.AutomaticBackoff,
            CandidateExclusionReason.ReboundObservationPending,
            CandidateExclusionReason.StableStateSuppression,
            CandidateExclusionReason.UnreliableActivitySample,
            CandidateExclusionReason.IdleConfirmationPending,
            CandidateExclusionReason.CurrentCpuActivity,
            CandidateExclusionReason.CurrentIoActivity,
            CandidateExclusionReason.ActiveProcessRelationship
        };

    public static ProcessFamilySnapshot? CreateBaseEligibleFamily(
        ProcessFamilySnapshot? family,
        OptimizationSettings settings)
    {
        if (family is null) return null;
        var processes = family.Processes
            .Where(process =>
                process.ProcessId != Environment.ProcessId &&
                !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath) &&
                process.WorkingSetBytes >= settings.MinimumProcessWorkingSetBytes)
            .ToArray();
        if (processes.Length == 0) return null;

        var eligible = family with { Processes = processes };
        return eligible.WorkingSetBytes >= settings.MinimumFamilyWorkingSetBytes
            ? eligible
            : null;
    }

    public static ProcessFamilySnapshot? CreateLifecycleVisibleFamily(ProcessFamilySnapshot? family)
    {
        if (family is null) return null;
        var processes = family.Processes
            .Where(process =>
                process.ProcessId != Environment.ProcessId &&
                !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath) &&
                process.WorkingSetBytes > 0)
            .ToArray();
        return processes.Length == 0 ? null : family with { Processes = processes };
    }

    public static bool IsTemporarilyBlocked(
        CandidateEvaluation evaluation,
        bool hasBaseEligibility,
        bool hasLifecycleVisibility = false)
    {
        if (evaluation.IsEligible) return false;
        if (hasLifecycleVisibility && evaluation.ExclusionReasons.Any(LifecycleReasons.Contains))
            return true;
        if (!hasBaseEligibility) return false;
        var reasons = evaluation.ExclusionReasons
            .Where(reason => reason != CandidateExclusionReason.Protected &&
                             reason != CandidateExclusionReason.BelowProcessWorkingSet)
            .Distinct()
            .ToArray();
        return reasons.Length > 0 && reasons.All(TemporaryReasons.Contains);
    }
}

public static class DeepReleaseExecutionSafetyPolicy
{
    public const double ActiveCpuThresholdPercent = 20;
    public const double ActiveIoThresholdBytesPerSecond = 16d * 1024 * 1024;

    public static IReadOnlyList<DeepReleaseCandidate> FilterSafeCandidates(
        IReadOnlyList<DeepReleaseCandidate> selectedCandidates,
        IReadOnlyList<ProcessFamilySnapshot> currentFamilies,
        ProtectionRules protection)
    {
        var protectionContext = protection.CreateContext(
            currentFamilies.SelectMany(family => family.Processes));
        var currentByKey = currentFamilies
            .GroupBy(family => family.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var safe = new List<DeepReleaseCandidate>();
        foreach (var candidate in selectedCandidates)
        {
            if (!currentByKey.TryGetValue(candidate.Family.Key, out var currentFamily) ||
                currentFamily.HasForegroundProcess ||
                !currentFamily.HasReliableActivitySample ||
                currentFamily.CpuPercent >= ActiveCpuThresholdPercent ||
                currentFamily.IoBytesPerSecond >= ActiveIoThresholdBytesPerSecond)
            {
                continue;
            }

            var unprotectedFamily = protection.FilterUnprotectedProcesses(currentFamily, protectionContext);
            if (unprotectedFamily is null || unprotectedFamily.Processes.Any(process =>
                    process.ProcessId == Environment.ProcessId ||
                    SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath)))
            {
                continue;
            }

            var currentById = unprotectedFamily.Processes
                .GroupBy(process => process.ProcessId)
                .ToDictionary(group => group.Key, group => group.First());
            var originalProcesses = candidate.Family.Processes
                .Where(expected =>
                    currentById.TryGetValue(expected.ProcessId, out var current) &&
                    ProcessIdentitySafetyPolicy.Evaluate(
                        expected.StartTimeFileTimeUtc,
                        current.StartTimeFileTimeUtc).CanTrim)
                .ToArray();
            if (originalProcesses.Length == 0) continue;

            safe.Add(candidate with
            {
                Family = candidate.Family with { Processes = originalProcesses }
            });
        }

        return safe;
    }
}
