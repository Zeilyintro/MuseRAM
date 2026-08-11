namespace MuseRAM.Core;

public enum OptimizationProfile
{
    Lite = 0,
    Turbo = 1,
    Ultimate = 2
}

public enum StableStateSuppressionMode
{
    FollowBaseProfile,
    ReduceRepeatedOptimization,
    Balanced,
    FasterReevaluation,
    Disabled,
    Custom
}

public sealed record OptimizationSettings(
    int MaxApplications,
    long MinimumFamilyWorkingSetBytes,
    long MinimumProcessWorkingSetBytes,
    double MinimumIdleScore,
    ulong TriggerAvailableBytes,
    int TriggerAvailablePercent,
    bool IgnoreMemoryPressureThreshold,
    bool AllowForegroundProcessTrim,
    TimeSpan ProcessCooldown,
    TimeSpan AutoCooldown,
    bool ProtectGamingProcesses)
{
    public bool EnhancedSafety { get; init; }
    public bool IntelligentCandidateSelection { get; init; }
    public bool QuickCandidateSelection { get; init; }
    public TimeSpan VisibleWindowIdleDelay { get; init; } = TimeSpan.FromMinutes(5);
    public double ActiveCpuThresholdPercent { get; init; } = 8;
    public double ActiveIoThresholdBytesPerSecond { get; init; } = 4d * 1024 * 1024;
    public bool AllowIndependentBackgroundProcessTrim { get; init; } = true;
    public StableStateSuppressionMode StableStateSuppressionMode { get; init; } =
        StableStateSuppressionMode.FollowBaseProfile;

    public static OptimizationSettings For(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => new(
            2, 280L.MiB(), 24L.MiB(), 65, 5UL.GiB(), 26, false, false,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(150), false)
            {
                VisibleWindowIdleDelay = TimeSpan.FromMinutes(10),
                ActiveCpuThresholdPercent = 4,
                ActiveIoThresholdBytesPerSecond = 2d * 1024 * 1024
            },
        OptimizationProfile.Turbo => new(
            7, 88L.MiB(), 8L.MiB(), 45, 12UL.GiB(), 48, false, false,
            TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(90), false)
            { VisibleWindowIdleDelay = TimeSpan.FromMinutes(5) },
        OptimizationProfile.Ultimate => new(
            0, 48L.MiB(), 4L.MiB(), 30, 0, 0, true, true,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), false)
            {
                VisibleWindowIdleDelay = TimeSpan.Zero,
                ActiveCpuThresholdPercent = 12,
                ActiveIoThresholdBytesPerSecond = 8d * 1024 * 1024
            },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    public static OptimizationSettings ForManual(OptimizationProfile profile)
    {
        var settings = For(profile);
        if (profile == OptimizationProfile.Ultimate) return settings;

        var turbo = profile == OptimizationProfile.Turbo;
        return settings with
        {
            MinimumFamilyWorkingSetBytes = turbo ? 64L.MiB() : 128L.MiB(),
            MinimumProcessWorkingSetBytes = Math.Min(settings.MinimumProcessWorkingSetBytes, turbo ? 8L.MiB() : 16L.MiB()),
            MinimumIdleScore = Math.Max(turbo ? 35 : 52, settings.MinimumIdleScore - 12),
            MaxApplications = Math.Min(settings.MaxApplications + (turbo ? 3 : 1), 12),
            ProcessCooldown = TimeSpan.FromSeconds(Math.Min(settings.ProcessCooldown.TotalSeconds, 12))
        };
    }
}

public static class ForegroundTrimPolicy
{
    public static bool IsAllowed(OptimizationSettings settings) =>
        IsAllowed(settings.AllowForegroundProcessTrim, settings.EnhancedSafety);

    public static bool IsAllowed(bool requested, bool enhancedSafety) =>
        requested && !enhancedSafety;
}

public readonly record struct MemorySnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint LoadPercent)
{
    public ulong CommitLimitBytes { get; init; }
    public ulong AvailableCommitBytes { get; init; }

    public ulong UsedPhysicalBytes => TotalPhysicalBytes > AvailablePhysicalBytes
        ? TotalPhysicalBytes - AvailablePhysicalBytes
        : 0;

    public ulong CommittedBytes => CommitLimitBytes > AvailableCommitBytes
        ? CommitLimitBytes - AvailableCommitBytes
        : 0;

    public double CommitLoadPercent => CommitLimitBytes == 0
        ? 0
        : Math.Clamp(CommittedBytes * 100d / CommitLimitBytes, 0, 100);
}

public sealed record AppOverheadSnapshot(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    double CpuPercent,
    double IoBytesPerSecond,
    int ThreadCount,
    int HandleCount,
    bool HasReliableCpuSample,
    bool HasReliableIoSample);

public sealed record ProcessSnapshot(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int? ParentProcessId,
    long WorkingSetBytes,
    double CpuPercent,
    double IoBytesPerSecond,
    bool IsForeground,
    bool HasVisibleWindow,
    bool HasReliableActivitySample,
    double IdleScore,
    long? StartTimeFileTimeUtc = null,
    string? MainWindowTitle = null,
    bool HasMinimizedWindow = false,
    DateTimeOffset? LastForegroundAt = null)
{
    public double IoReadBytesPerSecond { get; init; }
    public double IoWriteBytesPerSecond { get; init; }
    public double IoSampleIntervalSeconds { get; init; }
    public ulong IoReadTransferCount { get; init; }
    public ulong IoWriteTransferCount { get; init; }
    public ulong IoReadDeltaBytes { get; init; }
    public ulong IoWriteDeltaBytes { get; init; }
}

public sealed record ProcessFamilySnapshot(
    string Key,
    string DisplayName,
    string? ExecutableDirectory,
    IReadOnlyList<ProcessSnapshot> Processes)
{
    public long WorkingSetBytes => Processes.Sum(process => Math.Max(0, process.WorkingSetBytes));
    public double CpuPercent => Processes.Sum(process => Math.Max(0, process.CpuPercent));
    public double IoBytesPerSecond => Processes.Sum(process => Math.Max(0, process.IoBytesPerSecond));
    public bool HasForegroundProcess => Processes.Any(process => process.IsForeground);
    public bool HasVisibleWindow => Processes.Any(process => process.HasVisibleWindow);
    public bool HasMinimizedWindow => Processes.Any(process => process.HasMinimizedWindow);
    public bool HasReliableActivitySample => Processes.All(process => process.HasReliableActivitySample);
    public double IdleScore => WeightedIdleScore(Processes);
    public double IdleConfidenceScore => Processes.Count == 0
        ? 0
        : Math.Clamp(Processes.Average(process => ProcessIdleConfidencePolicy.Calculate(
            process.CpuPercent,
            process.IoBytesPerSecond,
            process.IsForeground,
            process.HasVisibleWindow)), 0, 100);
    public DateTimeOffset? LastForegroundAt => Processes
        .Where(process => process.LastForegroundAt.HasValue)
        .Max(process => process.LastForegroundAt);

    private static double WeightedIdleScore(IReadOnlyList<ProcessSnapshot> processes)
    {
        var totalWeight = processes.Sum(process => Math.Max(1, process.WorkingSetBytes));
        return totalWeight <= 0
            ? 0
            : Math.Clamp(processes.Sum(process => process.IdleScore * Math.Max(1, process.WorkingSetBytes)) / totalWeight, 0, 100);
    }
}

public sealed record OptimizationCandidate(
    ProcessFamilySnapshot Family,
    IReadOnlyList<ProcessSnapshot> TargetProcesses,
    double IdleConfidenceScore,
    long PotentialReleaseBytes,
    string Reason);

public enum OptimizationPlanOutcome
{
    LowMemoryPressure,
    NoCandidates,
    CandidatesFound
}

public enum OptimizationTriggerKind
{
    Automatic,
    Manual,
    Scheduled,
    LongIdle,
    ApplicationRule
}

public sealed record OptimizationRunContext(
    string ProfileKey,
    OptimizationProfile BaseProfile,
    OptimizationTriggerKind Trigger,
    string AppVersion)
{
    public string? RunId { get; init; }
}

public sealed record OptimizationPlan(
    bool ShouldRun,
    string Decision,
    IReadOnlyList<OptimizationCandidate> Candidates,
    OptimizationPlanOutcome Outcome)
{
    public IReadOnlyList<CandidateEvaluation> CandidateEvaluations { get; init; } =
        Array.Empty<CandidateEvaluation>();
}

public enum CandidateExclusionReason
{
    Protected,
    AutomaticBackoff,
    ReboundObservationPending,
    StableStateSuppression,
    VisibleWindowWait,
    MuseRamProcess,
    BelowProcessWorkingSet,
    UnreliableActivitySample,
    IdleConfirmationPending,
    Foreground,
    CurrentCpuActivity,
    CurrentIoActivity,
    ActiveProcessRelationship,
    GamingProtection,
    ProcessCooldown,
    BelowFamilyWorkingSet,
    BelowIdleScore,
    ApplicationRuleDelayNotDue,
    ApplicationRuleWorkingSetObservationPending,
    ApplicationRuleWorkingSetCooldown,
    ApplicationRuleInvalidProcessIdentity,
    ApplicationRuleZeroWorkingSet,
    ApplicationRuleSystemProcess,
    ApplicationRuleForegroundBlocked
}

public sealed record CandidateEvaluation(
    string FamilyKey,
    string DisplayName,
    bool IsEligible,
    int ProcessCount,
    int TargetProcessCount,
    IReadOnlyList<CandidateExclusionReason> ExclusionReasons)
{
    public double LegacyIdleScore { get; init; }
    public double IdleConfidenceScore { get; init; }
    public long TargetWorkingSetBytes { get; init; }
    public long TotalWorkingSetBytes { get; init; }
    public IReadOnlyList<int> TargetProcessIds { get; init; } = Array.Empty<int>();
    // Application-rule diagnostics retain the current process identity so a family-level
    // summary cannot hide which target was blocked and why.
    public IReadOnlyDictionary<string, IReadOnlyList<CandidateExclusionReason>> ProcessExclusionReasons { get; init; } =
        new Dictionary<string, IReadOnlyList<CandidateExclusionReason>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record CandidateIdleReadiness(
    int ProcessId,
    int ConsecutiveReliableLowActivitySamples,
    bool IsReady);

public sealed record TrimResult(
    int ProcessId,
    bool Success,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    string? Error,
    bool Skipped = false)
{
    public bool SetProcessWorkingSetSucceeded { get; init; }
    public bool EmptyWorkingSetSucceeded { get; init; }
    public int? SetProcessWorkingSetErrorCode { get; init; }
    public int? EmptyWorkingSetErrorCode { get; init; }
    public bool HasReliableWorkingSetMeasurement { get; init; }
    public string? Warning { get; init; }
    public double TotalMilliseconds { get; init; }
    public double OpenProcessMilliseconds { get; init; }
    public double IdentityCheckMilliseconds { get; init; }
    public double RelationshipCheckMilliseconds { get; init; }
    public double SetProcessWorkingSetMilliseconds { get; init; }
    public double EmptyWorkingSetMilliseconds { get; init; }
    public double MeasurementMilliseconds { get; init; }
    public uint? PageFaultCountBefore { get; init; }
    public uint? PageFaultCountAfter { get; init; }
    public uint? PageFaultCountDelta => PageFaultCountBefore.HasValue &&
                                         PageFaultCountAfter >= PageFaultCountBefore
        ? PageFaultCountAfter.Value - PageFaultCountBefore.Value
        : null;
    public long WorkingSetReductionBytes => HasReliableWorkingSetMeasurement
        ? Math.Max(0, WorkingSetBeforeBytes - WorkingSetAfterBytes)
        : 0;
}

public readonly record struct ProcessTrimSafetyCheck(bool CanTrim, string? SkipReason)
{
    public static ProcessTrimSafetyCheck Allow() => new(true, null);
    public static ProcessTrimSafetyCheck Skip(string reason) => new(false, reason);
}

public static class ProcessIdentitySafetyPolicy
{
    public static ProcessTrimSafetyCheck Evaluate(
        long? expectedStartTimeFileTimeUtc,
        long? actualStartTimeFileTimeUtc)
    {
        if (!expectedStartTimeFileTimeUtc.HasValue || !actualStartTimeFileTimeUtc.HasValue)
        {
            return ProcessTrimSafetyCheck.Skip("无法确认进程身份。");
        }

        return expectedStartTimeFileTimeUtc.Value == actualStartTimeFileTimeUtc.Value
            ? ProcessTrimSafetyCheck.Allow()
            : ProcessTrimSafetyCheck.Skip("PID 已属于另一个进程。");
    }
}

public static class ProcessInteractionSafetyPolicy
{
    public static ProcessTrimSafetyCheck Evaluate(
        IReadOnlySet<int> relatedProcessIds,
        int? foregroundProcessId,
        IReadOnlySet<int> visibleWindowProcessIds,
        bool allowForegroundProcessTrim = false)
    {
        if (!allowForegroundProcessTrim &&
            foregroundProcessId.HasValue &&
            relatedProcessIds.Contains(foregroundProcessId.Value))
        {
            return ProcessTrimSafetyCheck.Skip("应用已切换到前台。");
        }

        return ProcessTrimSafetyCheck.Allow();
    }
}

public static class ProcessExecutionSafetyPolicy
{
    private static readonly IReadOnlySet<int> EmptyProcessIds = new HashSet<int>();

    public static ProcessTrimSafetyCheck EvaluateInteraction(
        bool foregroundSnapshotSucceeded,
        IReadOnlySet<int> relatedProcessIds,
        int? foregroundProcessId,
        bool allowForegroundProcessTrim)
    {
        if (!foregroundSnapshotSucceeded)
            return ProcessTrimSafetyCheck.Skip("无法确认执行前的前台状态。");
        return ProcessInteractionSafetyPolicy.Evaluate(
            relatedProcessIds,
            foregroundProcessId,
            EmptyProcessIds,
            allowForegroundProcessTrim);
    }

    public static ProcessTrimSafetyCheck EvaluateEnhanced(
        int processId,
        long? expectedStartTimeFileTimeUtc,
        long? actualStartTimeFileTimeUtc,
        IReadOnlySet<int> relatedProcessIds,
        bool foregroundSnapshotSucceeded,
        int? foregroundProcessId,
        bool visibleWindowSnapshotSucceeded,
        IReadOnlySet<int> visibleWindowProcessIds)
    {
        if (!foregroundSnapshotSucceeded || !visibleWindowSnapshotSucceeded)
            return ProcessTrimSafetyCheck.Skip("无法确认执行前的窗口状态。");
        return ProcessTrimSafetyPolicy.Evaluate(
            processId,
            expectedStartTimeFileTimeUtc,
            actualStartTimeFileTimeUtc,
            relatedProcessIds,
            foregroundProcessId,
            visibleWindowProcessIds);
    }
}

public static class DeepReleaseProcessSafetyPolicy
{
    public static ProcessTrimSafetyCheck Evaluate(
        long? expectedStartTimeFileTimeUtc,
        long? actualStartTimeFileTimeUtc,
        IReadOnlySet<int> relatedProcessIds,
        int? foregroundProcessId)
    {
        var identity = ProcessIdentitySafetyPolicy.Evaluate(
            expectedStartTimeFileTimeUtc,
            actualStartTimeFileTimeUtc);
        if (!identity.CanTrim) return identity;

        return ProcessInteractionSafetyPolicy.Evaluate(
            relatedProcessIds,
            foregroundProcessId,
            new HashSet<int>());
    }
}

public static class ProcessTrimSafetyPolicy
{
    public static ProcessTrimSafetyCheck Evaluate(
        int processId,
        long? expectedStartTimeFileTimeUtc,
        long? actualStartTimeFileTimeUtc,
        IReadOnlySet<int> relatedProcessIds,
        int? foregroundProcessId,
        IReadOnlySet<int> visibleWindowProcessIds)
    {
        var identity = ProcessIdentitySafetyPolicy.Evaluate(
            expectedStartTimeFileTimeUtc,
            actualStartTimeFileTimeUtc);
        if (!identity.CanTrim) return identity;

        if (foregroundProcessId.HasValue && relatedProcessIds.Contains(foregroundProcessId.Value))
        {
            return ProcessTrimSafetyCheck.Skip("应用已切换到前台。");
        }

        if (relatedProcessIds.Any(visibleWindowProcessIds.Contains))
        {
            return ProcessTrimSafetyCheck.Skip("应用已有可见或最小化窗口。");
        }

        return relatedProcessIds.Contains(processId)
            ? ProcessTrimSafetyCheck.Allow()
            : ProcessTrimSafetyCheck.Skip("目标进程已不属于原应用族。");
    }
}

public static class ProcessRelationshipPolicy
{
    public static IReadOnlySet<int> BuildSafetyScope(
        int processId,
        IReadOnlyList<ProcessSnapshot> processes)
    {
        var parentById = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First().ParentProcessId);
        var result = new HashSet<int> { processId };

        AddAncestors(processId, parentById, result);
        AddDescendants(processId, parentById, result);
        return result;
    }

    internal static bool TryRefreshSafetyScope(
        int processId,
        IReadOnlyCollection<int> sampledSafetyScope,
        IReadOnlyDictionary<int, int?> currentParentProcessIds,
        out IReadOnlySet<int> refreshedSafetyScope)
    {
        // Retain sampled links if a process exited, then add relationships recovered or created since planning.
        var result = sampledSafetyScope.ToHashSet();
        result.Add(processId);
        refreshedSafetyScope = result;
        if (!currentParentProcessIds.ContainsKey(processId)) return false;

        AddAncestors(processId, currentParentProcessIds, result);
        AddDescendants(processId, currentParentProcessIds, result);
        return true;
    }

    private static void AddAncestors(
        int processId,
        IReadOnlyDictionary<int, int?> parentById,
        ISet<int> result)
    {
        var parent = parentById.GetValueOrDefault(processId);
        while (parent.HasValue && result.Add(parent.Value))
        {
            parent = parentById.GetValueOrDefault(parent.Value);
        }
    }

    private static void AddDescendants(
        int processId,
        IReadOnlyDictionary<int, int?> parentById,
        ISet<int> result)
    {
        foreach (var candidate in parentById)
        {
            var current = candidate.Value;
            var visited = new HashSet<int>();
            while (current.HasValue && visited.Add(current.Value))
            {
                if (current.Value == processId)
                {
                    result.Add(candidate.Key);
                    break;
                }
                current = parentById.GetValueOrDefault(current.Value);
            }
        }
    }
}

public enum BackgroundActivityState
{
    Observing,
    Idle,
    Working,
    Visible
}

public sealed record BackgroundActivity(
    string FamilyKey,
    BackgroundActivityState State,
    TimeSpan ObservedFor,
    TimeSpan IdleFor,
    int SampleCount);

public sealed record DeepReleaseCandidate(
    ProcessFamilySnapshot Family,
    BackgroundActivity Activity,
    bool IsSuggested);

internal static class ByteUnits
{
    public static long MiB(this long value) => checked(value * 1024 * 1024);
    public static ulong GiB(this ulong value) => checked(value * 1024 * 1024 * 1024);
}
