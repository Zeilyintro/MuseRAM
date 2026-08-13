namespace MuseRAM.Core;

public sealed record ReboundBackoffSettings(
    TimeSpan EarlyWindow,
    double EarlyReboundPercent,
    TimeSpan LateWindow,
    double LateReboundPercent,
    TimeSpan FirstBackoff,
    TimeSpan SecondBackoff)
{
    public bool Enabled { get; init; } = true;
    public bool CycleAfterSecondBackoff { get; init; }
    public bool AllowSecondBackoffForegroundIdleRetry { get; init; }

    public static ReboundBackoffSettings Default { get; } = For(OptimizationProfile.Turbo);

    public static ReboundBackoffSettings For(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => Create(50, 70, 30, 60),
        OptimizationProfile.Turbo => Create(60, 80, 5, 10),
        OptimizationProfile.Ultimate => Create(75, 90, 2, 5) with
        {
            CycleAfterSecondBackoff = true,
            AllowSecondBackoffForegroundIdleRetry = true
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private static ReboundBackoffSettings Create(
        double earlyPercent,
        double latePercent,
        double firstBackoffMinutes,
        double secondBackoffMinutes) => new(
            TimeSpan.FromSeconds(30),
            earlyPercent,
            TimeSpan.FromSeconds(120),
            latePercent,
            TimeSpan.FromMinutes(firstBackoffMinutes),
            TimeSpan.FromMinutes(secondBackoffMinutes));
}

public sealed record StableStateSuppressionSettings(
    int MinimumSamples,
    TimeSpan MaximumRecordAge,
    double RelativeGrowthMargin,
    long AbsoluteGrowthMarginBytes)
{
    private TimeSpan _maximumStableValidationDuration = DefaultMaximumStableValidationDuration;
    public static readonly TimeSpan DefaultMaximumStableValidationDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultNaturalStableSampleInterval = TimeSpan.FromMinutes(15);
    public TimeSpan MaximumStableValidationDuration
    {
        get => _maximumStableValidationDuration;
        init => _maximumStableValidationDuration = value;
    }
    public bool IgnoreRegularObservationUnderSeverePressure { get; init; }
    public TimeSpan NaturalStableSampleInterval { get; init; } = DefaultNaturalStableSampleInterval;
    public int MaximumStableSamplesPerLaunch { get; init; } = 3;
    public int MaximumStableSamplePool { get; init; } = StableWorkingSetLearningPolicy.DefaultRecentSamples;
    public long MaximumStableWorkingSetBytes { get; init; } = long.MaxValue;

    public static StableStateSuppressionSettings For(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => Create(0.50d, 128, 1024, 10),
        OptimizationProfile.Turbo => Create(0.35d, 96, 768, 5),
        OptimizationProfile.Ultimate => Create(0.20d, 64, 256, 3,
            ignoreRegularObservationUnderSeverePressure: true),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    public static StableStateSuppressionSettings For(StableStateSuppressionMode mode) => mode switch
    {
        StableStateSuppressionMode.ReduceRepeatedOptimization => For(OptimizationProfile.Lite),
        StableStateSuppressionMode.Balanced => For(OptimizationProfile.Turbo),
        StableStateSuppressionMode.FasterReevaluation => For(OptimizationProfile.Ultimate),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public StableStateSuppressionSettings Normalize()
    {
        var minimumSamples = Math.Clamp(MinimumSamples, 1, 20);
        var samplePool = Math.Max(minimumSamples, Math.Clamp(
            MaximumStableSamplePool,
            StableWorkingSetLearningPolicy.DefaultRecentSamples,
            StableWorkingSetLearningPolicy.MaximumRecentSamples));
        return new StableStateSuppressionSettings(
            minimumSamples,
            TimeSpan.FromDays(Math.Clamp(MaximumRecordAge.TotalDays, 1, 90)),
            Math.Clamp(RelativeGrowthMargin, 0, 1.5d),
            Math.Clamp(AbsoluteGrowthMarginBytes, 0, 1024L * 1024 * 1024))
        {
            MaximumStableValidationDuration = TimeSpan.FromMinutes(Math.Clamp(
                MaximumStableValidationDuration.TotalMinutes, 3, 10)),
            IgnoreRegularObservationUnderSeverePressure =
                IgnoreRegularObservationUnderSeverePressure,
            NaturalStableSampleInterval = TimeSpan.FromMinutes(Math.Clamp(
                NaturalStableSampleInterval.TotalMinutes, 5, 60)),
            MaximumStableSamplesPerLaunch = Math.Clamp(MaximumStableSamplesPerLaunch, 1, 3),
            MaximumStableSamplePool = samplePool,
            MaximumStableWorkingSetBytes = MaximumStableWorkingSetBytes == long.MaxValue
                ? long.MaxValue
                : Math.Clamp(MaximumStableWorkingSetBytes, 256L * 1024 * 1024, 4096L * 1024 * 1024)
        };
    }

    private static StableStateSuppressionSettings Create(
        double relativeMargin,
        long absoluteMarginMiB,
        long maximumStableWorkingSetMiB,
        double maximumValidationMinutes,
        bool ignoreRegularObservationUnderSeverePressure = false) =>
        new(3, TimeSpan.FromDays(30), relativeMargin, absoluteMarginMiB * 1024 * 1024)
        {
            MaximumStableWorkingSetBytes = maximumStableWorkingSetMiB * 1024 * 1024,
            MaximumStableValidationDuration = TimeSpan.FromMinutes(maximumValidationMinutes),
            IgnoreRegularObservationUnderSeverePressure = ignoreRegularObservationUnderSeverePressure
        };
}

public sealed class CustomStableStateSuppressionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public OptimizationProfile BaseProfile { get; set; } = OptimizationProfile.Turbo;
    public int SortOrder { get; set; }
    public StableStateSuppressionSettings Settings { get; set; } =
        StableStateSuppressionSettings.For(OptimizationProfile.Turbo);

    public CustomStableStateSuppressionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseProfile = BaseProfile,
        SortOrder = SortOrder,
        Settings = (Settings ?? StableStateSuppressionSettings.For(BaseProfile)) with { }
    };
}

public static class CustomStableStateSuppressionProfilePolicy
{
    public const int MaximumCustomProfiles = 8;

    public static CustomStableStateSuppressionProfile Create(
        OptimizationProfile baseProfile,
        string name,
        int sortOrder) => Normalize(new CustomStableStateSuppressionProfile
        {
            Name = name,
            BaseProfile = baseProfile,
            SortOrder = sortOrder,
            Settings = StableStateSuppressionSettings.For(baseProfile)
        });

    public static CustomStableStateSuppressionProfile Copy(
        CustomStableStateSuppressionProfile source,
        string name,
        int sortOrder) => Normalize(new CustomStableStateSuppressionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            BaseProfile = source.BaseProfile,
            SortOrder = sortOrder,
            Settings = (source.Settings ?? StableStateSuppressionSettings.For(source.BaseProfile)) with { }
        });

    public static CustomStableStateSuppressionProfile Normalize(
        CustomStableStateSuppressionProfile profile)
    {
        var baseProfile = Enum.IsDefined(profile.BaseProfile)
            ? profile.BaseProfile
            : OptimizationProfile.Turbo;
        return new CustomStableStateSuppressionProfile
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
            Name = (profile.Name ?? string.Empty).Trim(),
            BaseProfile = baseProfile,
            SortOrder = Math.Max(0, profile.SortOrder),
            Settings = (profile.Settings ?? StableStateSuppressionSettings.For(baseProfile)).Normalize()
        };
    }

    public static bool IsUniqueName(
        IEnumerable<CustomStableStateSuppressionProfile> profiles,
        string candidate,
        string? exceptId = null)
    {
        var normalized = candidate.Trim();
        if (normalized.Length == 0 || BuiltInNames.Contains(normalized)) return false;
        return profiles.All(profile =>
            string.Equals(profile.Id, exceptId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> BuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lite", "Turbo", "Ultimate"
    };
}

public sealed class CustomOptimizationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public OptimizationProfile BaseProfile { get; set; } = OptimizationProfile.Turbo;
    public int SortOrder { get; set; }
    public OptimizationSettings Settings { get; set; } = OptimizationSettings.For(OptimizationProfile.Turbo);
    public ReboundBackoffSettings Rebound { get; set; } = ReboundBackoffSettings.Default;
    public StableStateSuppressionSettings StableStateSuppression { get; set; } = null!;
    // Retained only so existing settings JSON can still be loaded.
    public StableStateSuppressionMode StableStateSuppressionMode { get; set; } =
        StableStateSuppressionMode.FollowBaseProfile;

    public CustomOptimizationProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseProfile = BaseProfile,
        SortOrder = SortOrder,
        Settings = Settings with { EnhancedSafety = false },
        Rebound = Rebound with { },
        StableStateSuppression = (StableStateSuppression ??
                                  StableStateSuppressionSettings.For(BaseProfile)) with { },
        StableStateSuppressionMode = StableStateSuppressionMode
    };
}

public static class CustomProfilePolicy
{
    public const int MaximumCustomProfiles = 8;

    public static CustomOptimizationProfile Create(
        OptimizationProfile baseProfile,
        string name,
        int sortOrder) => Normalize(new CustomOptimizationProfile
        {
            Name = name,
            BaseProfile = baseProfile,
            SortOrder = sortOrder,
            Settings = OptimizationSettings.For(baseProfile),
            Rebound = ReboundBackoffSettings.For(baseProfile),
            StableStateSuppression = StableStateSuppressionSettings.For(baseProfile)
        });

    public static CustomOptimizationProfile Copy(
        CustomOptimizationProfile source,
        string name,
        int sortOrder) => Normalize(new CustomOptimizationProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            BaseProfile = source.BaseProfile,
            SortOrder = sortOrder,
            Settings = (source.Settings ?? OptimizationSettings.For(source.BaseProfile)) with { },
            Rebound = (source.Rebound ?? ReboundBackoffSettings.For(source.BaseProfile)) with { },
            StableStateSuppression = (source.StableStateSuppression ??
                                      StableStateSuppressionSettings.For(source.BaseProfile)) with { },
            StableStateSuppressionMode = source.StableStateSuppressionMode
        });

    public static CustomOptimizationProfile Normalize(CustomOptimizationProfile profile)
    {
        var baseProfile = Enum.IsDefined(profile.BaseProfile)
            ? profile.BaseProfile
            : OptimizationProfile.Turbo;
        var bounds = BoundsFor(baseProfile);
        var settings = profile.Settings ?? OptimizationSettings.For(baseProfile);
        var rebound = profile.Rebound ?? ReboundBackoffSettings.For(baseProfile);
        var stableStateSuppression = (profile.StableStateSuppression ??
                                      StableStateSuppressionSettings.For(baseProfile)).Normalize();
        var stableStateSuppressionMode = Enum.IsDefined(profile.StableStateSuppressionMode)
            ? profile.StableStateSuppressionMode
            : StableStateSuppressionMode.FollowBaseProfile;
        var earlyWindowSeconds = Math.Clamp(rebound.EarlyWindow.TotalSeconds, 10, 60);
        var lateWindowSeconds = Math.Clamp(rebound.LateWindow.TotalSeconds, earlyWindowSeconds + 10, 300);
        var earlyPercent = Math.Clamp(rebound.EarlyReboundPercent, 20, 95);
        var latePercent = Math.Clamp(rebound.LateReboundPercent, earlyPercent, 99);
        var firstBackoffMinutes = Math.Clamp(rebound.FirstBackoff.TotalMinutes, 1, 120);
        var secondBackoffMinutes = Math.Clamp(rebound.SecondBackoff.TotalMinutes, firstBackoffMinutes, 360);
        var ultimateBackoffPolicy = baseProfile == OptimizationProfile.Ultimate;

        return new CustomOptimizationProfile
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
            Name = (profile.Name ?? string.Empty).Trim(),
            BaseProfile = baseProfile,
            SortOrder = Math.Max(0, profile.SortOrder),
            Settings = settings with
            {
                MaxApplications = Math.Clamp(settings.MaxApplications <= 0 ? bounds.MaximumApplications : settings.MaxApplications, bounds.MinimumApplications, bounds.MaximumApplications),
                MinimumFamilyWorkingSetBytes = Math.Clamp(settings.MinimumFamilyWorkingSetBytes, bounds.MinimumFamilyWorkingSetBytes, bounds.MaximumFamilyWorkingSetBytes),
                MinimumProcessWorkingSetBytes = Math.Clamp(settings.MinimumProcessWorkingSetBytes, bounds.MinimumProcessWorkingSetBytes, bounds.MaximumProcessWorkingSetBytes),
                MinimumIdleScore = Math.Clamp(settings.MinimumIdleScore, bounds.MinimumIdleScore, bounds.MaximumIdleScore),
                TriggerAvailableBytes = Math.Clamp(settings.TriggerAvailableBytes == 0 ? bounds.MinimumTriggerAvailableBytes : settings.TriggerAvailableBytes, bounds.MinimumTriggerAvailableBytes, bounds.MaximumTriggerAvailableBytes),
                TriggerAvailablePercent = Math.Clamp(settings.TriggerAvailablePercent <= 0 ? bounds.MinimumTriggerAvailablePercent : settings.TriggerAvailablePercent, bounds.MinimumTriggerAvailablePercent, bounds.MaximumTriggerAvailablePercent),
                IgnoreMemoryPressureThreshold = baseProfile == OptimizationProfile.Ultimate && settings.IgnoreMemoryPressureThreshold,
                AllowForegroundProcessTrim = baseProfile == OptimizationProfile.Ultimate && settings.AllowForegroundProcessTrim,
                ProtectGamingProcesses = false,
                ProcessCooldown = TimeSpan.FromSeconds(Math.Clamp(settings.ProcessCooldown.TotalSeconds <= 0 ? bounds.MinimumProcessCooldownSeconds : settings.ProcessCooldown.TotalSeconds, bounds.MinimumProcessCooldownSeconds, bounds.MaximumProcessCooldownSeconds)),
                AutoCooldown = TimeSpan.FromSeconds(Math.Clamp(settings.AutoCooldown.TotalSeconds, bounds.MinimumAutoCooldownSeconds, bounds.MaximumAutoCooldownSeconds)),
                VisibleWindowIdleDelay = TimeSpan.FromMinutes(Math.Clamp(
                    settings.VisibleWindowIdleDelay.TotalMinutes,
                    bounds.MinimumVisibleWindowIdleMinutes,
                    bounds.MaximumVisibleWindowIdleMinutes)),
                ActiveCpuThresholdPercent = Math.Clamp(
                    settings.ActiveCpuThresholdPercent,
                    bounds.MinimumActiveCpuPercent,
                    bounds.MaximumActiveCpuPercent),
                ActiveIoThresholdBytesPerSecond = Math.Clamp(
                    settings.ActiveIoThresholdBytesPerSecond,
                    bounds.MinimumActiveIoBytesPerSecond,
                    bounds.MaximumActiveIoBytesPerSecond),
                EnhancedSafety = false
            },
            Rebound = new ReboundBackoffSettings(
                TimeSpan.FromSeconds(earlyWindowSeconds),
                earlyPercent,
                TimeSpan.FromSeconds(lateWindowSeconds),
                latePercent,
                TimeSpan.FromMinutes(firstBackoffMinutes),
                TimeSpan.FromMinutes(secondBackoffMinutes))
            {
                Enabled = rebound.Enabled,
                CycleAfterSecondBackoff = rebound.CycleAfterSecondBackoff || ultimateBackoffPolicy,
                AllowSecondBackoffForegroundIdleRetry =
                    rebound.AllowSecondBackoffForegroundIdleRetry || ultimateBackoffPolicy
            },
            StableStateSuppression = stableStateSuppression,
            StableStateSuppressionMode = stableStateSuppressionMode
        };
    }

    public static bool IsUniqueName(
        IEnumerable<CustomOptimizationProfile> profiles,
        string candidate,
        string? exceptId = null)
    {
        var normalized = candidate.Trim();
        if (normalized.Length == 0 || BuiltInNames.Contains(normalized)) return false;
        return profiles.All(profile =>
            string.Equals(profile.Id, exceptId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> BuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lite", "Turbo", "Ultimate"
    };

    private static ProfileBounds BoundsFor(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => new(
            1, 40, 96L.MiB(), 1024L.MiB(), 8L.MiB(), 128L.MiB(), 45, 90,
            1UL.GiB(), 12UL.GiB(), 5, 48, 18, 600, 90, 900, 3, 15,
            2, 15, 1d * 1024 * 1024, 8d * 1024 * 1024),
        OptimizationProfile.Turbo => new(
            2, 40, 2L.MiB(), 280L.MiB(), 4L.MiB(), 24L.MiB(), 20, 65,
            2UL.GiB(), 32UL.GiB(), 10, 70, 5, 120, 30, 300, 1, 10,
            4, 25, 2d * 1024 * 1024, 16d * 1024 * 1024),
        OptimizationProfile.Ultimate => new(
            7, 40, 2L.MiB(), 96L.MiB(), 1L.MiB(), 8L.MiB(), 5, 45,
            1UL.GiB(), 64UL.GiB(), 1, 95, 1, 18, 15, 120, 0, 5,
            8, 50, 4d * 1024 * 1024, 32d * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private sealed record ProfileBounds(
        int MinimumApplications,
        int MaximumApplications,
        long MinimumFamilyWorkingSetBytes,
        long MaximumFamilyWorkingSetBytes,
        long MinimumProcessWorkingSetBytes,
        long MaximumProcessWorkingSetBytes,
        double MinimumIdleScore,
        double MaximumIdleScore,
        ulong MinimumTriggerAvailableBytes,
        ulong MaximumTriggerAvailableBytes,
        int MinimumTriggerAvailablePercent,
        int MaximumTriggerAvailablePercent,
        double MinimumProcessCooldownSeconds,
        double MaximumProcessCooldownSeconds,
        double MinimumAutoCooldownSeconds,
        double MaximumAutoCooldownSeconds,
        double MinimumVisibleWindowIdleMinutes,
        double MaximumVisibleWindowIdleMinutes,
        double MinimumActiveCpuPercent,
        double MaximumActiveCpuPercent,
        double MinimumActiveIoBytesPerSecond,
        double MaximumActiveIoBytesPerSecond);
}

public sealed record ApplicationBenefitLearningRecord(
    string FamilyKey,
    double AverageOutcomeMultiplier,
    int SampleCount,
    int QuickReturnCount,
    DateTimeOffset LastObservedAt)
{
    public string? ComponentKey { get; init; }
    public string? ExecutablePath { get; init; }
    public long AverageReleasedBytes { get; init; }
    public long AverageRetainedBytes { get; init; }
    public long AverageLateWorkingSetBytes { get; init; }
    public double AverageReboundPercent { get; init; }
    public int BackoffTriggerCount { get; init; }
    public int DistinctLaunchCount { get; init; }
    public string? LastLaunchSignature { get; init; }
    public int LegacySampleCount { get; init; }
    public int ValidSampleCount { get; init; }
    public double RecentBackoffRate { get; init; }
    public double RecentQuickReturnRate { get; init; }
    public IReadOnlyList<long> LateWorkingSetSamplesBytes { get; init; } = Array.Empty<long>();
    public int LastLaunchObservationCount { get; init; }
    public double LastLaunchContributionWeight { get; init; }
    public double LastLaunchAverageOutcomeMultiplier { get; init; }
    public double LastLaunchAverageReleasedBytes { get; init; }
    public double LastLaunchAverageRetainedBytes { get; init; }
    public double LastLaunchAverageLateWorkingSetBytes { get; init; }
    public double LastLaunchAverageReboundPercent { get; init; }
    public double LastLaunchQuickReturnRate { get; init; }
    public double LastLaunchBackoffRate { get; init; }
    public IReadOnlyList<long> StableWorkingSetSamplesBytes { get; init; } = Array.Empty<long>();
    public DateTimeOffset? StableLastObservedAt { get; init; }
    public string? LastStableLaunchSignature { get; init; }
}

public sealed record ApplicationStableLearningRecord(
    string FamilyKey,
    IReadOnlyList<long> StableWorkingSetSamplesBytes,
    DateTimeOffset? StableLastObservedAt,
    string? LastStableLaunchSignature)
{
    public IReadOnlyList<string> ComponentKeys { get; init; } = Array.Empty<string>();
    public int ModelVersion { get; init; }
    public int LastStableLaunchSampleCount { get; init; }
    public IReadOnlyList<ApplicationStableSample> StableSamples { get; init; } =
        Array.Empty<ApplicationStableSample>();
    public int AnchorGeneration { get; init; }
    public long AnchorGenerationBaselineBytes { get; init; }
    public int HistoricalReviewSuccessCount { get; init; }
    public int HistoricalReviewScheduleVersion { get; init; }
}

public sealed record ApplicationStableSample(
    long WorkingSetBytes,
    DateTimeOffset ObservedAt,
    string LaunchSignature,
    string RecoveryCycleId,
    int Generation,
    bool PendingHigh)
{
    public long MinimumWorkingSetBytes { get; init; }
    public long MaximumWorkingSetBytes { get; init; }
}

public enum StableAnchorMode
{
    Adaptive,
    Fixed
}

public sealed record ApplicationStableAnchorSetting(
    string FamilyKey,
    string ScopeKey,
    StableAnchorMode Mode,
    long FixedAnchorBytes);

public static class ApplicationStableScopeIdentity
{
    public static string For(string familyKey, IEnumerable<string> componentKeys) =>
        $"{familyKey.Trim().ToLowerInvariant()}|scope:{string.Join(';',
            componentKeys.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";

    public static string For(ApplicationStableLearningRecord record) =>
        For(record.FamilyKey, record.ComponentKeys);
}

public enum ApplicationStableCandidateState
{
    Provisional,
    Converged,
    Excluded
}

public sealed record ApplicationStableCandidateStatus(
    string FamilyKey,
    string ComponentKey,
    string LaunchSignature,
    ApplicationStableCandidateState State,
    long CandidateBytes,
    long PreviousObservationBytes,
    long LatestObservationBytes,
    int ConsecutiveObservationCount,
    DateTimeOffset LastObservedAt);

public enum ApplicationStableObservationDecision
{
    FirstObservation,
    Converged,
    NotConverged,
    LearningDisabled,
    MissingWorkingSet,
    ReturnedToForeground,
    EarlyBackoff,
    HighRebound,
    MissingLaunchSignature,
    LaunchChanged,
    RestartedAfterExclusion,
    ComponentScopeChanged,
    CandidateExpired,
    ConvergedAcrossLaunch,
    HighAnchorPending,
    FirstBootAnchorExceeded
}

public sealed record ApplicationStableObservation(
    OptimizationRunContext? RunContext,
    string FamilyKey,
    string ScopeKey,
    string LaunchSignature,
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

public sealed record ApplicationBackoffStatus(
    int ReboundCount,
    DateTimeOffset? BlockedUntil,
    bool LongTermObservation)
{
    public bool ObservationPending { get; init; }
    public bool LongTermSawForeground { get; init; }
}

public sealed record ApplicationBackoffProgress(
    string FamilyKey,
    int ReboundCount,
    double RemainingBlockSeconds,
    double? LongTermObservedSeconds,
    bool LongTermSawForeground,
    bool LongTermRetryPermitted)
{
    public string? TargetKey { get; init; }
    public long LongTermBaselineWorkingSetBytes { get; init; }
    public int BackoffStage { get; init; }
    public bool AllowForegroundIdleRetry { get; init; }
    public bool TimedBackoffSawForeground { get; init; }
    public double? TimedBackgroundLowActivitySeconds { get; init; }
}

public sealed record ApplicationReboundOutcome(
    OptimizationRunContext? RunContext,
    string FamilyKey,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double ObservationWindowSeconds,
    long ReleasedBytes,
    long RegainedBytes,
    long RetainedBytes,
    double ReboundPercent,
    bool BackoffTriggered,
    TimeSpan? TimeToForeground)
{
    public string? ComponentKey { get; init; }
    public string? ExecutablePath { get; init; }
    public long LateWorkingSetBytes { get; init; }
}

public sealed class ApplicationReboundBackoffTracker
{
    private const int MaximumLearningSamples = 100;
    private static readonly TimeSpan StableCandidateMaximumAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan LearningDecayStartsAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan LearningExpiresAfter = TimeSpan.FromDays(90);
    public static readonly TimeSpan LongTermIdleObservation = TimeSpan.FromHours(1);
    public static readonly TimeSpan NaturalStableRecoveryEligibilityWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LongTermBackoffStableObservationWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NaturalStableConvergedTail = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan NaturalStableMinimumConfirmation = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan NaturalStableRequiredValidation = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NaturalStableRollingRetention = TimeSpan.FromMinutes(2);
    private const long NaturalStableGrowthMinimumIncreaseBytes = 64L * 1024 * 1024;
    private const long NaturalStableGrowthMaximumIncreaseBytes = 128L * 1024 * 1024;
    private const double NaturalStableGrowthRelativeIncrease = 0.25d;
    public static readonly TimeSpan NaturalStableSampleInterval =
        StableStateSuppressionSettings.DefaultNaturalStableSampleInterval;
    public const int MaximumStableSamplesPerLaunch = 3;
    private readonly Dictionary<string, PendingObservation> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BackoffState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationBenefitLearningRecord> _learning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationStableLearningRecord> _familyStableLearning = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadedStableRecordMigrationPending;
    private readonly Dictionary<string, ApplicationStableCandidateStatus> _stableCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NaturalStableWindow> _naturalStableWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NaturalStableReviewCompletion> _naturalStableReviewCompletions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HistoricalReviewSession> _historicalReviewSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _naturalRecoveryEligibleComponents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _naturalRecoveryCycleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _naturalRecoveryFamilyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NaturalStableObservationOrigin> _naturalRecoveryOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _globalReclaimSuppressedLaunchesByComponent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _naturalRecoveryStartedAts = new(StringComparer.OrdinalIgnoreCase);
    private bool _restorePersistedSessionHoldsOnNextObservation;
    private readonly Dictionary<string, double> _outcomeMultipliers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _learningConfidences = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ApplicationReboundOutcome> _completedOutcomes = new();
    private readonly List<ApplicationStableObservation> _completedStableObservations = new();

    public IReadOnlyDictionary<string, double> OutcomeMultipliers => _outcomeMultipliers;
    public IReadOnlyDictionary<string, double> LearningConfidences => _learningConfidences;
    public IReadOnlyList<ApplicationBenefitLearningRecord> LearningRecords => _learning.Values.ToArray();
    public IReadOnlyList<ApplicationStableLearningRecord> FamilyStableLearningRecords => _familyStableLearning.Values.ToArray();
    public IReadOnlyList<ApplicationStableCandidateStatus> StableCandidateStatuses => _stableCandidates.Values.ToArray();

    public void EnablePersistedSessionHoldRestoration() =>
        _restorePersistedSessionHoldsOnNextObservation = true;
    public IReadOnlySet<string> NaturalStableObservationComponentKeys() =>
        _naturalStableWindows.Values
            .SelectMany(window => window.ComponentKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> NaturalStableReviewComponentKeys() =>
        _naturalStableWindows.Values
            .Where(window =>
                window.Origin == NaturalStableObservationOrigin.HistoricalBoundedConfirmation)
            .SelectMany(window => window.ComponentKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> NaturalStableGrowthReviewComponentKeys() =>
        _naturalStableWindows.Values
            .Where(window => window.GrowthReview is not null)
            .SelectMany(window => window.GrowthReview!.FamilyScopeComponentKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> NaturalStableProvisionalValidationComponentKeys() =>
        _naturalStableWindows.Values
            .Where(window => window.Validation is not null)
            .SelectMany(window => window.ComponentKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public NaturalStableReviewSchedule? GetNaturalStableReviewSchedule(
        string familyKey,
        IEnumerable<string> componentKeys,
        StableStateSuppressionSettings settings,
        string? currentLaunchSignature = null)
    {
        var scopeKey = ApplicationStableScopeIdentity.For(familyKey, componentKeys);
        if (!_familyStableLearning.TryGetValue(scopeKey, out var record) ||
            record.StableLastObservedAt is not { } lastObservedAt ||
            string.IsNullOrWhiteSpace(record.LastStableLaunchSignature))
        {
            return null;
        }

        settings = settings.Normalize();
        var completedReviews = currentLaunchSignature is not null &&
                               _historicalReviewSessions.TryGetValue(scopeKey, out var session) &&
                               string.Equals(session.LaunchSignature, currentLaunchSignature,
                                   StringComparison.Ordinal)
            ? session.CompletedReviewCount
            : 0;
        var highMigrationCycles = StableAnchorLearningPolicy.PendingHighRecoveryCycleCount(record);
        return new NaturalStableReviewSchedule(
            lastObservedAt + NaturalStableReviewInterval(completedReviews),
            completedReviews,
            3,
            highMigrationCycles,
            StableAnchorLearningPolicy.RequiredMigrationRecoveryCycles,
            AwaitingNewRecoveryCycle: false);
    }

    private static TimeSpan NaturalStableReviewInterval(int completedReviews) => completedReviews < 3
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromHours(2);

    private void EnsureHistoricalReviewSession(NaturalStableStateSnapshot snapshot)
    {
        if (_historicalReviewSessions.TryGetValue(snapshot.ScopeKey, out var session) &&
            string.Equals(session.LaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal))
            return;

        _historicalReviewSessions[snapshot.ScopeKey] = new HistoricalReviewSession(
            snapshot.LaunchSignature,
            CompletedReviewCount: 0);
    }

    private int HistoricalReviewCount(NaturalStableStateSnapshot snapshot) =>
        _historicalReviewSessions.TryGetValue(snapshot.ScopeKey, out var session) &&
        string.Equals(session.LaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal)
            ? session.CompletedReviewCount
            : 0;

    private void IncrementHistoricalReviewCount(NaturalStableStateSnapshot snapshot)
    {
        EnsureHistoricalReviewSession(snapshot);
        var session = _historicalReviewSessions[snapshot.ScopeKey];
        _historicalReviewSessions[snapshot.ScopeKey] = session with
        {
            CompletedReviewCount = Math.Min(3, session.CompletedReviewCount + 1)
        };
    }

    public IReadOnlyList<NaturalStableObservationProgress> CaptureNaturalStableObservationProgress() =>
        _naturalStableWindows.Select(pair =>
        {
            var window = pair.Value;
            return new NaturalStableObservationProgress(
                window.FamilyKey, pair.Key, window.LaunchSignature, window.StartedAt, window.Deadline,
                window.ComponentKeys, window.MinimumBytes, window.MaximumBytes, window.LatestBytes,
                window.ObservationCount,
                window.WorkingSetSamples.Select(sample => new NaturalStableTimedSampleProgress(
                    sample.ObservedAt, sample.WorkingSetBytes, sample.IsLowActivity)).ToArray(),
                window.StableDuration, window.TotalObservationDuration, window.LastObservedAt,
                window.AllowsNewBaseline, window.PreserveConvergedStatus, window.Origin)
            {
                Validation = window.Validation is null
                    ? null
                    : new NaturalStableValidationProgress(
                        window.Validation.StartedAt, window.Validation.Deadline,
                        window.Validation.ContinuousStableSince, window.Validation.FamilyScopeKey,
                        window.Validation.FamilyScopeLaunchSignature, window.Validation.UpperLimitBytes,
                        window.Validation.BaselineFamilyWorkingSetBytes, window.Validation.StableBytes,
                        window.Validation.StableMinimumBytes, window.Validation.StableMaximumBytes,
                        window.Validation.RecoveryCycleId, window.Validation.BackoffObservation,
                        window.Validation.CompletesBackoffObservation),
                GrowthReview = window.GrowthReview is null
                    ? null
                    : new NaturalStableGrowthReviewProgress(
                        window.GrowthReview.FamilyScopeKey,
                        window.GrowthReview.FamilyScopeComponentKeys,
                        window.GrowthReview.FamilyScopeLaunchSignature,
                        window.GrowthReview.StartedAt,
                        window.GrowthReview.BaselineFamilyWorkingSetBytes,
                        window.GrowthReview.LatestFamilyWorkingSetBytes,
                        window.GrowthReview.RequiredIncreaseBytes,
                        window.GrowthReview.LastObservedAt)
            };
        }).ToArray();

    public IReadOnlyList<HistoricalReviewSessionProgress> CaptureHistoricalReviewSessionProgress() =>
        _historicalReviewSessions.Select(pair => new HistoricalReviewSessionProgress(
            pair.Key,
            pair.Value.LaunchSignature,
            pair.Value.CompletedReviewCount)).ToArray();

    public void RestoreHistoricalReviewSessionProgress(
        IEnumerable<HistoricalReviewSessionProgress>? progress)
    {
        foreach (var item in progress ?? Array.Empty<HistoricalReviewSessionProgress>())
        {
            if (string.IsNullOrWhiteSpace(item.ScopeKey) ||
                string.IsNullOrWhiteSpace(item.LaunchSignature)) continue;
            _historicalReviewSessions[item.ScopeKey] = new HistoricalReviewSession(
                item.LaunchSignature,
                Math.Clamp(item.CompletedReviewCount, 0, 3));
        }
    }

    public void RestoreNaturalStableObservationProgress(
        IEnumerable<NaturalStableObservationProgress>? progress,
        DateTimeOffset savedAt,
        DateTimeOffset now)
    {
        var shift = now >= savedAt ? now - savedAt : TimeSpan.Zero;
        DateTimeOffset Shift(DateTimeOffset value) => value == DateTimeOffset.MaxValue
            ? value
            : value + shift;
        foreach (var item in progress ?? Array.Empty<NaturalStableObservationProgress>())
        {
            if (string.IsNullOrWhiteSpace(item.ScopeKey) ||
                string.IsNullOrWhiteSpace(item.FamilyKey) ||
                string.IsNullOrWhiteSpace(item.LaunchSignature) ||
                item.ComponentKeys is null || item.ComponentKeys.Count == 0 ||
                item.StartedAt > savedAt + TimeSpan.FromMinutes(1) ||
                item.LastObservedAt > savedAt + TimeSpan.FromMinutes(1)) continue;

            var componentKeys = item.ComponentKeys
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var samples = (item.WorkingSetSamples ?? Array.Empty<NaturalStableTimedSampleProgress>())
                .Where(sample => sample.ObservedAt <= savedAt + TimeSpan.FromMinutes(1) &&
                                 sample.WorkingSetBytes > 0)
                .Select(sample => new TimedWorkingSetSample(
                    Shift(sample.ObservedAt), sample.WorkingSetBytes, sample.IsLowActivity))
                .ToArray();
            if (componentKeys.Length == 0 || samples.Length == 0) continue;

            var window = new NaturalStableWindow(
                item.FamilyKey, item.LaunchSignature, Shift(item.StartedAt), Shift(item.Deadline),
                componentKeys, Math.Max(0, item.MinimumBytes), Math.Max(0, item.MaximumBytes),
                Math.Max(0, item.LatestBytes), Math.Max(1, item.ObservationCount), samples,
                item.StableDuration < TimeSpan.Zero ? TimeSpan.Zero : item.StableDuration,
                item.TotalObservationDuration < TimeSpan.Zero ? TimeSpan.Zero : item.TotalObservationDuration,
                Shift(item.LastObservedAt), item.AllowsNewBaseline, item.PreserveConvergedStatus, item.Origin)
            {
                Validation = item.Validation is null
                    ? null
                    : new NaturalStableValidation(
                        Shift(item.Validation.StartedAt), Shift(item.Validation.Deadline),
                        item.Validation.ContinuousStableSince is { } stableSince ? Shift(stableSince) : null,
                        item.Validation.FamilyScopeKey, item.Validation.FamilyScopeLaunchSignature,
                        Math.Max(0, item.Validation.UpperLimitBytes),
                        Math.Max(0, item.Validation.BaselineFamilyWorkingSetBytes),
                        Math.Max(0, item.Validation.StableBytes),
                        Math.Max(0, item.Validation.StableMinimumBytes),
                        Math.Max(0, item.Validation.StableMaximumBytes),
                        item.Validation.RecoveryCycleId, item.Validation.BackoffObservation,
                        item.Validation.CompletesBackoffObservation),
                GrowthReview = item.GrowthReview is null
                    ? null
                    : new NaturalStableGrowthReview(
                        item.GrowthReview.FamilyScopeKey,
                        item.GrowthReview.FamilyScopeComponentKeys ?? Array.Empty<string>(),
                        item.GrowthReview.FamilyScopeLaunchSignature,
                        Shift(item.GrowthReview.StartedAt),
                        Math.Max(0, item.GrowthReview.BaselineFamilyWorkingSetBytes),
                        Math.Max(0, item.GrowthReview.LatestFamilyWorkingSetBytes),
                        Math.Max(0, item.GrowthReview.RequiredIncreaseBytes),
                        Shift(item.GrowthReview.LastObservedAt))
            };
            _naturalStableWindows[item.ScopeKey] = window;
            _stableCandidates[item.ScopeKey] = new ApplicationStableCandidateStatus(
                item.FamilyKey, item.ScopeKey, item.LaunchSignature,
                item.PreserveConvergedStatus
                    ? ApplicationStableCandidateState.Converged
                    : ApplicationStableCandidateState.Provisional,
                Math.Max(0, item.Validation?.StableBytes ?? item.LatestBytes),
                0, Math.Max(0, item.LatestBytes), Math.Max(1, item.ObservationCount),
                Shift(item.LastObservedAt));
        }
    }

    public IReadOnlyList<NaturalStableObservationStatus> NaturalStableObservationStatuses() =>
        _naturalStableWindows.Select(pair =>
        {
            var window = pair.Value;
            var growthReview = window.GrowthReview;
            return new NaturalStableObservationStatus(
                pair.Key, window.ComponentKeys, window.Origin, window.StartedAt,
                window.Deadline, IsGrowthReview: growthReview is not null,
                LatestWorkingSetBytes: growthReview?.LatestFamilyWorkingSetBytes ?? window.LatestBytes,
                ObservationCount: window.ObservationCount,
                LastObservedAt: window.LastObservedAt,
                LatestIsLowActivity: window.WorkingSetSamples.LastOrDefault()?.IsLowActivity ?? false,
                BaselineWorkingSetBytes: growthReview?.BaselineFamilyWorkingSetBytes,
                RequiredIncreaseBytes: growthReview?.RequiredIncreaseBytes)
            {
                Phase = growthReview is not null
                    ? StableObservationPhase.GrowthReview
                    : window.Validation is null
                        ? StableObservationPhase.Observing
                        : StableObservationPhase.ProvisionalValidation,
                ValidationDeadline = window.Validation?.Deadline,
                ContinuousStableSince = window.Validation?.ContinuousStableSince,
                ValidationUpperLimitBytes = window.Validation?.UpperLimitBytes,
                RequiresFirstBootAnchorGate = window.RequiresFirstBootAnchorGate
            };
        }).ToArray();
    public IReadOnlySet<string> NaturalStableRecoveryEligibleComponentKeys(
        DateTimeOffset? observedAt = null,
        TimeSpan? observationWindow = null)
    {
        var now = observedAt ?? DateTimeOffset.Now;
        var window = observationWindow is { } configured && configured > TimeSpan.Zero
            ? configured
            : NaturalStableRecoveryEligibilityWindow;
        return _naturalRecoveryEligibleComponents
            .Where(component => IsNaturalRecoveryEligibilityActive(component, now, window))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<NaturalStableScopeRequest> NaturalStableScopeRequests(
        DateTimeOffset? observedAt = null,
        TimeSpan? observationWindow = null)
    {
        var now = observedAt ?? DateTimeOffset.Now;
        var window = observationWindow is { } configured && configured > TimeSpan.Zero
            ? configured
            : NaturalStableRecoveryEligibilityWindow;
        var activeBackoffs = _states
            .Where(pair => pair.Value.LongTerm is { RetryPermitted: false } ||
                           pair.Value.BlockedUntil >= now)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var observedWindowComponents = _naturalStableWindows.Values
            .Where(window => window.Deadline >= now)
            .SelectMany(window => window.ComponentKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requests = new List<NaturalStableScopeRequest>();
        var pendingFamilies = _pending.Values
            .Where(item => item.LearnOutcome && !item.ReturnedToForegroundAt.HasValue)
            .Select(item => item.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _pending.Values
                     .Where(item => item.LearnOutcome &&
                                    item.RecoveryOrigin != NaturalStableObservationOrigin.GlobalReclaim &&
                                    !item.ReturnedToForegroundAt.HasValue)
                     .GroupBy(item => item.FamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            var completedComponents = _naturalRecoveryEligibleComponents.Where(component =>
                _naturalRecoveryFamilyKeys.TryGetValue(component, out var familyKey) &&
                string.Equals(familyKey, group.Key, StringComparison.OrdinalIgnoreCase));
            var componentKeys = group.Select(item => item.ComponentKey)
                .Concat(completedComponents)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var backoffStates = componentKeys
                .Where(activeBackoffs.ContainsKey)
                .Select(component => activeBackoffs[component])
                .ToArray();
            var backoff = backoffStates.Length > 0;
            var startedAt = backoff
                ? backoffStates.Min(state => state.StartedAt)
                : group.Min(item => item.StartedAt);
            requests.Add(new NaturalStableScopeRequest(group.First().FamilyKey, componentKeys, startedAt)
            {
                Deadline = backoff
                    ? backoffStates.Min(BackoffStableObservationDeadline)
                    : DateTimeOffset.MaxValue,
                Origin = backoff
                    ? NaturalStableObservationOrigin.BackoffRecovery
                    : group.First().RecoveryOrigin
            });
        }

        foreach (var group in activeBackoffs
                     .Where(pair => !_pending.ContainsKey(pair.Key) &&
                                    !observedWindowComponents.Contains(pair.Key))
                     .GroupBy(pair => pair.Value.FamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            var states = group.Select(pair => pair.Value).ToArray();
            requests.Add(new NaturalStableScopeRequest(
                group.First().Value.FamilyKey,
                group.Select(pair => pair.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                states.Min(state => state.StartedAt))
            {
                Deadline = states.Min(BackoffStableObservationDeadline),
                Origin = NaturalStableObservationOrigin.BackoffRecovery
            });
        }

        foreach (var group in _naturalRecoveryEligibleComponents
                     .Where(component => _naturalRecoveryFamilyKeys.ContainsKey(component) &&
                                         IsNaturalRecoveryEligibilityActive(component, now, window) &&
                                         !_pending.ContainsKey(component) &&
                                         !activeBackoffs.ContainsKey(component) &&
                                         !pendingFamilies.Contains(_naturalRecoveryFamilyKeys[component]) &&
                                         (_naturalRecoveryOrigins.GetValueOrDefault(
                                              component, NaturalStableObservationOrigin.PostTrim) !=
                                          NaturalStableObservationOrigin.GlobalReclaim ||
                                          HasUsableStableAnchor(component)))
                     .GroupBy(component => _naturalRecoveryFamilyKeys[component],
                         StringComparer.OrdinalIgnoreCase))
        {
            requests.Add(new NaturalStableScopeRequest(
                group.Key,
                group.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                group.Min(component => _naturalRecoveryStartedAts[component]))
            {
                Deadline = DateTimeOffset.MaxValue,
                Origin = group.Select(component => _naturalRecoveryOrigins.GetValueOrDefault(
                        component, NaturalStableObservationOrigin.PostTrim))
                    .Contains(NaturalStableObservationOrigin.GlobalReclaim)
                    ? NaturalStableObservationOrigin.GlobalReclaim
                    : NaturalStableObservationOrigin.PostTrim
            });
        }

        var windowRequests = _naturalStableWindows.Select(pair =>
            new NaturalStableScopeRequest(
                pair.Value.FamilyKey,
                pair.Value.ComponentKeys,
                pair.Value.StartedAt)
            {
                Deadline = pair.Value.Deadline,
                Origin = pair.Value.Origin
            });
        requests.AddRange(windowRequests);
        return requests
            .Where(request => request.Deadline is null || request.Deadline >= now)
            .GroupBy(request => ApplicationStableScopeIdentity.For(
                request.FamilyKey,
                request.ComponentKeys), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(request =>
                    request.Origin == NaturalStableObservationOrigin.BackoffRecovery)
                .ThenBy(request => request.StartedAt).First())
            .ToArray();
    }
    public IReadOnlySet<string> PendingObservationFamilyKeys(DateTimeOffset now) =>
        _pending
            .Where(pair => now - pair.Value.StartedAt < pair.Value.Settings.LateWindow)
            .Select(pair => pair.Value.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> PendingObservationComponentKeys(DateTimeOffset now) =>
        _pending
            .Where(pair => now - pair.Value.StartedAt < pair.Value.Settings.LateWindow)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> PendingGlobalReclaimObservationComponentKeys(DateTimeOffset now) =>
        _pending
            .Where(pair => now - pair.Value.StartedAt < pair.Value.Settings.LateWindow &&
                           pair.Value.RecoveryOrigin == NaturalStableObservationOrigin.GlobalReclaim)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public TimeSpan? PendingObservationRemaining(
        IEnumerable<string> componentKeys,
        DateTimeOffset now)
    {
        var remaining = componentKeys
            .Where(_pending.ContainsKey)
            .Select(component => _pending[component])
            .Select(item => item.Settings.LateWindow - (now - item.StartedAt))
            .Where(value => value > TimeSpan.Zero)
            .DefaultIfEmpty()
            .Max();
        return remaining > TimeSpan.Zero ? remaining : null;
    }
    public int LearningRevision { get; private set; }

    public ApplicationReboundBackoffTracker(
        IEnumerable<ApplicationBenefitLearningRecord>? learningRecords = null,
        DateTimeOffset? now = null,
        IEnumerable<ApplicationStableLearningRecord>? familyStableLearningRecords = null)
    {
        var loadedAt = now ?? DateTimeOffset.UtcNow;
        foreach (var record in learningRecords ?? Array.Empty<ApplicationBenefitLearningRecord>())
        {
            if (!IsValid(record) || loadedAt - record.LastObservedAt >= LearningExpiresAfter) continue;
            var validSampleCount = Math.Clamp(record.ValidSampleCount, 0, MaximumLearningSamples);
            var sampleCount = validSampleCount > 0
                ? validSampleCount
                : Math.Clamp(record.SampleCount, 1, MaximumLearningSamples);
            _learning[LearningKey(record)] = record with
            {
                AverageOutcomeMultiplier = Math.Clamp(record.AverageOutcomeMultiplier, 0d, 1d),
                SampleCount = sampleCount,
                QuickReturnCount = Math.Clamp(record.QuickReturnCount, 0, sampleCount),
                LegacySampleCount = validSampleCount > 0
                    ? 0
                    : Math.Clamp(Math.Max(record.LegacySampleCount, sampleCount), 0, MaximumLearningSamples),
                ValidSampleCount = validSampleCount,
                RecentBackoffRate = Math.Clamp(record.RecentBackoffRate, 0d, 1d),
                RecentQuickReturnRate = Math.Clamp(record.RecentQuickReturnRate, 0d, 1d),
                LateWorkingSetSamplesBytes = (record.LateWorkingSetSamplesBytes ?? Array.Empty<long>())
                    .Where(value => value > 0)
                    .TakeLast(MaximumLearningSamples)
                    .ToArray(),
                StableWorkingSetSamplesBytes = (record.StableWorkingSetSamplesBytes ?? Array.Empty<long>())
                    .Where(value => value > 0)
                    .TakeLast(MaximumLearningSamples)
                    .ToArray(),
                StableLastObservedAt = record.StableLastObservedAt is { } stableObservedAt &&
                                       stableObservedAt != default
                    ? stableObservedAt
                    : null,
                LastStableLaunchSignature = string.IsNullOrWhiteSpace(record.LastStableLaunchSignature)
                    ? null
                    : record.LastStableLaunchSignature.Trim(),
                LastLaunchObservationCount = Math.Max(0, record.LastLaunchObservationCount),
                LastLaunchContributionWeight = double.IsFinite(record.LastLaunchContributionWeight) &&
                                               record.LastLaunchContributionWeight > 0d
                    ? Math.Clamp(record.LastLaunchContributionWeight, 0d, 1d)
                    : 0d
            };
        }
        foreach (var record in familyStableLearningRecords ?? Array.Empty<ApplicationStableLearningRecord>())
        {
            if (string.IsNullOrWhiteSpace(record.FamilyKey) ||
                record.ModelVersion != StableStateSuppressionPolicy.NaturalStableStateModelVersion) continue;
            var hadMetadata = (record.StableSamples ?? Array.Empty<ApplicationStableSample>()).Count > 0;
            var stableSamples = StableAnchorLearningPolicy.NormalizeSamples(record)
                .TakeLast(StableWorkingSetLearningPolicy.MaximumRecentSamples)
                .ToArray();
            if (stableSamples.Length == 0 || record.StableLastObservedAt is not { } observedAt || observedAt == default)
                continue;
            var anchorGeneration = hadMetadata ? Math.Max(0, record.AnchorGeneration) : 1;
            var anchorBaseline = hadMetadata
                ? Math.Max(0, record.AnchorGenerationBaselineBytes)
                : StableWorkingSetLearningPolicy.Median(stableSamples
                    .Select(sample => sample.WorkingSetBytes)
                    .OrderBy(value => value)
                    .ToArray());
            var normalizedRecord = record with
            {
                FamilyKey = record.FamilyKey.Trim(),
                StableWorkingSetSamplesBytes = stableSamples
                    .Select(sample => sample.WorkingSetBytes)
                    .ToArray(),
                StableSamples = stableSamples,
                AnchorGeneration = anchorGeneration,
                AnchorGenerationBaselineBytes = anchorBaseline,
                ComponentKeys = (record.ComponentKeys ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                LastStableLaunchSignature = string.IsNullOrWhiteSpace(record.LastStableLaunchSignature)
                    ? null
                    : record.LastStableLaunchSignature.Trim(),
                LastStableLaunchSampleCount = Math.Clamp(
                    record.LastStableLaunchSampleCount > 0 ? record.LastStableLaunchSampleCount : 1,
                    1,
                    MaximumStableSamplesPerLaunch),
                HistoricalReviewSuccessCount = 0
            };
            var unexpiredRecord = StableAnchorLearningPolicy.ExpirePendingEvidence(normalizedRecord, loadedAt);
            var normalized = StableAnchorLearningPolicy.ReclassifyPendingHighSamples(unexpiredRecord);
            if (!ReferenceEquals(normalized, normalizedRecord))
                _loadedStableRecordMigrationPending = true;
            var acceptedLastLaunchCount = Math.Clamp(
                StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
                    normalized,
                    normalized.LastStableLaunchSignature,
                    StableWorkingSetLearningPolicy.MaximumRecentSamples),
                0,
                MaximumStableSamplesPerLaunch);
            const int reviewSuccessCount = 0;
            if (normalized.LastStableLaunchSampleCount != acceptedLastLaunchCount ||
                record.HistoricalReviewScheduleVersion < 2 ||
                normalized.HistoricalReviewSuccessCount != reviewSuccessCount)
                _loadedStableRecordMigrationPending = true;
            normalized = normalized with
            {
                LastStableLaunchSampleCount = acceptedLastLaunchCount,
                HistoricalReviewSuccessCount = reviewSuccessCount,
                HistoricalReviewScheduleVersion = 2
            };
            if (normalized.ComponentKeys.Count == 0) continue;
            _familyStableLearning[ApplicationStableScopeIdentity.For(normalized)] = normalized;
        }
        RefreshOutcomeMultipliers(loadedAt);
    }

    public void Begin(
        string familyKey,
        long workingSetBefore,
        long workingSetAfter,
        ReboundBackoffSettings settings,
        DateTimeOffset now,
        bool learnOutcome = false,
        bool wasForegroundBeforeTrim = false,
        IReadOnlyCollection<int>? targetProcessIds = null,
        OptimizationRunContext? runContext = null,
        IReadOnlyCollection<int>? baselineFamilyProcessIds = null)
    {
        BeginComponent(
            familyKey,
            familyKey,
            executablePath: null,
            workingSetBefore,
            workingSetAfter,
            settings,
            now,
            learnOutcome,
            wasForegroundBeforeTrim,
            targetProcessIds,
            runContext,
            baselineFamilyProcessIds,
            launchSignature: null);
    }

    public void BeginComponent(
        string familyKey,
        string componentKey,
        string? executablePath,
        long workingSetBefore,
        long workingSetAfter,
        ReboundBackoffSettings settings,
        DateTimeOffset now,
        bool learnOutcome = false,
        bool wasForegroundBeforeTrim = false,
        IReadOnlyCollection<int>? targetProcessIds = null,
        OptimizationRunContext? runContext = null,
        IReadOnlyCollection<int>? baselineFamilyProcessIds = null,
        string? launchSignature = null,
        NaturalStableObservationOrigin recoveryOrigin = NaturalStableObservationOrigin.PostTrim)
    {
        var released = Math.Max(0, workingSetBefore - workingSetAfter);
        if (string.IsNullOrWhiteSpace(familyKey) ||
            string.IsNullOrWhiteSpace(componentKey) ||
            released <= 0) return;
        foreach (var windowKey in _naturalStableWindows
                     .Where(pair => pair.Value.ComponentKeys.Contains(
                         componentKey, StringComparer.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _naturalStableWindows.Remove(windowKey);
        }
        launchSignature = ResolveLaunchSignature(launchSignature, targetProcessIds, now);
        if (recoveryOrigin == NaturalStableObservationOrigin.GlobalReclaim)
            _globalReclaimSuppressedLaunchesByComponent[componentKey] = launchSignature;
        else
            _globalReclaimSuppressedLaunchesByComponent.Remove(componentKey);
        if (_pending.TryGetValue(componentKey, out var existing) &&
            now - existing.StartedAt >= existing.Settings.LateWindow)
        {
            _pending.Remove(componentKey);
        }
        var recoveryAttempt = false;
        if (_states.TryGetValue(componentKey, out var state) &&
            state.LongTerm is not null &&
            (state.LongTerm.RetryPermitted ||
             runContext?.Trigger == OptimizationTriggerKind.Manual))
        {
            recoveryAttempt = true;
            _states[componentKey] = state with
            {
                BlockedUntil = DateTimeOffset.MinValue,
                LongTerm = null
            };
        }
        _pending.TryAdd(componentKey, new PendingObservation(
            familyKey,
            componentKey,
            executablePath,
            Math.Max(0, workingSetAfter),
            released,
            settings,
            now,
            Math.Clamp(released / (double)Math.Max(1, workingSetBefore), 0d, 1d),
            targetProcessIds?.ToHashSet(),
            baselineFamilyProcessIds?.ToHashSet(),
            learnOutcome,
            wasForegroundBeforeTrim,
            ReturnedToForegroundAt: null,
            EarlyChecked: false,
            BackoffRegistered: false,
            recoveryAttempt,
            runContext,
            launchSignature,
            recoveryOrigin));
    }

    public void Observe(
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset now,
        bool benefitLearningEnabled = true)
    {
        foreach (var pair in _pending.ToArray())
        {
            var targetKey = pair.Key;
            var pending = pair.Value;
            if (!benefitLearningEnabled && pending.LearnOutcome)
            {
                pending = pending with { LearnOutcome = false };
                _pending[targetKey] = pending;
            }
            var elapsed = now - pending.StartedAt;
            var family = families.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, pending.FamilyKey, StringComparison.OrdinalIgnoreCase));
            var current = CurrentWorkingSet(pending, family);
            if (!pending.WasForegroundBeforeTrim &&
                !pending.ReturnedToForegroundAt.HasValue &&
                HasForegroundProcess(pending, family))
            {
                pending = pending with { ReturnedToForegroundAt = now };
                _pending[targetKey] = pending;
            }
            if (!pending.EarlyChecked)
            {
                var reboundPercent = ReboundPercent(pending, current);
                if (IsSignificantRebound(reboundPercent, pending.Settings.EarlyReboundPercent))
                {
                    if (!pending.BackoffRegistered && pending.Settings.Enabled &&
                        pending.RecoveryOrigin != NaturalStableObservationOrigin.GlobalReclaim)
                        Register(targetKey, pending.FamilyKey, pending.Settings, current, now);
                    pending = pending with
                    {
                        EarlyChecked = true,
                        BackoffRegistered = pending.Settings.Enabled &&
                                            pending.RecoveryOrigin != NaturalStableObservationOrigin.GlobalReclaim
                    };
                    _pending[targetKey] = pending;
                }
                else if (elapsed >= pending.Settings.EarlyWindow)
                {
                    pending = pending with { EarlyChecked = true };
                    _pending[targetKey] = pending;
                }
            }

            if (elapsed < pending.Settings.LateWindow) continue;
            var lateReboundPercent = ReboundPercent(pending, current);
            RecordOutcome(pending, current, lateReboundPercent, now);
            var backoffTriggered = pending.BackoffRegistered;
            if (!backoffTriggered &&
                pending.Settings.Enabled &&
                pending.RecoveryOrigin != NaturalStableObservationOrigin.GlobalReclaim &&
                IsSignificantRebound(lateReboundPercent, pending.Settings.LateReboundPercent))
            {
                Register(targetKey, pending.FamilyKey, pending.Settings, current, now);
                backoffTriggered = true;
            }
            if (backoffTriggered)
                UpdateLongTermBaseline(targetKey, current);
            if (!backoffTriggered && pending.RecoveryAttempt)
            {
                DowngradeAfterSuccessfulRetry(targetKey);
            }
            if (pending.LearnOutcome &&
                (pending.RecoveryOrigin != NaturalStableObservationOrigin.GlobalReclaim ||
                 HasUsableStableAnchor(pending.ComponentKey)) &&
                !pending.ReturnedToForegroundAt.HasValue)
            {
                _naturalRecoveryEligibleComponents.Add(pending.ComponentKey);
                var recoveryCycleId = pending.RunContext?.RunId ??
                    $"recovery:{pending.StartedAt.UtcTicks}:{pending.ComponentKey}";
                if (!backoffTriggered)
                {
                    _naturalRecoveryCycleIds[pending.ComponentKey] = recoveryCycleId;
                    _naturalRecoveryStartedAts[pending.ComponentKey] = pending.StartedAt;
                }
                _naturalRecoveryFamilyKeys[pending.ComponentKey] = pending.FamilyKey;
                _naturalRecoveryOrigins[pending.ComponentKey] = pending.RecoveryOrigin;
            }
            else
            {
                RemoveNaturalRecoveryEligibility(pending.ComponentKey);
            }
            var regainedBytes = RegainedBytes(pending, current);
            _completedOutcomes.Add(new ApplicationReboundOutcome(
                pending.RunContext,
                pending.FamilyKey,
                pending.StartedAt,
                now,
                pending.Settings.LateWindow.TotalSeconds,
                pending.ReleasedBytes,
                regainedBytes,
                Math.Max(0, pending.ReleasedBytes - regainedBytes),
                lateReboundPercent,
                backoffTriggered,
                pending.ReturnedToForegroundAt - pending.StartedAt)
            {
                ComponentKey = pending.ComponentKey,
                ExecutablePath = pending.ExecutablePath,
                LateWorkingSetBytes = Math.Max(0, current)
            });
            _pending.Remove(targetKey);
        }
    }

    public void ObserveNaturalStableStates(
        IReadOnlyList<NaturalStableStateSnapshot> snapshots,
        DateTimeOffset now,
        StableStateSuppressionSettings? suppressionSettings = null,
        bool severeMemoryPressure = false,
        bool enabled = true)
    {
        var restorePersistedSessionHolds = _restorePersistedSessionHoldsOnNextObservation;
        _restorePersistedSessionHoldsOnNextObservation = false;
        if (_loadedStableRecordMigrationPending)
        {
            _loadedStableRecordMigrationPending = false;
            LearningRevision++;
        }
        if (!enabled)
        {
            _naturalStableWindows.Clear();
            _naturalStableReviewCompletions.Clear();
            _historicalReviewSessions.Clear();
            _naturalRecoveryEligibleComponents.Clear();
            _naturalRecoveryCycleIds.Clear();
            _naturalRecoveryFamilyKeys.Clear();
            _naturalRecoveryOrigins.Clear();
            _globalReclaimSuppressedLaunchesByComponent.Clear();
            _naturalRecoveryStartedAts.Clear();
            _stableCandidates.Clear();
            return;
        }

        var normalizedSuppressionSettings = (suppressionSettings ??
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo)).Normalize();
        var stableConfirmationMinimum = NaturalStableMinimumConfirmation;
        var maximumSamplesPerLaunch = normalizedSuppressionSettings.MaximumStableSamplesPerLaunch;
        var maximumStableSamplePool = normalizedSuppressionSettings.MaximumStableSamplePool;

        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            EnsureHistoricalReviewSession(snapshot);
            // A global-reclaim request can expand from component scope to an application scope.
            // Keep its origin on the snapshot as the authoritative isolation marker.
            var globalReclaimSuppressed = snapshot.RecoveryOrigin ==
                                         NaturalStableObservationOrigin.GlobalReclaim ||
                                         snapshot.ComponentKeys.Any(component =>
                                             _globalReclaimSuppressedLaunchesByComponent.TryGetValue(component, out var launch) &&
                                             string.Equals(launch, snapshot.LaunchSignature, StringComparison.Ordinal));
            seenScopes.Add(snapshot.ScopeKey);
            if (!string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey))
                seenScopes.Add(snapshot.FamilyScopeKey);
            seenComponents.UnionWith(snapshot.ComponentKeys);
            var existingStatus = _stableCandidates.GetValueOrDefault(snapshot.ScopeKey);
            var existingRecord = _familyStableLearning.GetValueOrDefault(snapshot.ScopeKey);
            var currentApplicationWorkingSetBytes = snapshot.FamilyScopeWorkingSetBytes > 0
                ? snapshot.FamilyScopeWorkingSetBytes
                : snapshot.WorkingSetBytes;
            if (currentApplicationWorkingSetBytes >
                normalizedSuppressionSettings.MaximumStableWorkingSetBytes)
            {
                var cappedScopeKeys = new[] { snapshot.ScopeKey, snapshot.FamilyScopeKey }
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var scopeKey in cappedScopeKeys)
                {
                    _naturalStableWindows.Remove(scopeKey!);
                    _stableCandidates.Remove(scopeKey!);
                }
                foreach (var component in snapshot.ComponentKeys
                             .Concat(snapshot.FamilyScopeComponentKeys)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    RemoveNaturalRecoveryEligibility(component);
                }
                continue;
            }
            if (restorePersistedSessionHolds &&
                existingStatus is null &&
                existingRecord is not null &&
                string.Equals(existingRecord.LastStableLaunchSignature,
                    snapshot.LaunchSignature, StringComparison.Ordinal) &&
                StableAnchorLearningPolicy.NormalizeSamples(existingRecord)
                    .Where(sample => string.Equals(sample.LaunchSignature,
                        snapshot.LaunchSignature, StringComparison.Ordinal))
                    .Where(sample => !sample.PendingHigh)
                    .OrderByDescending(sample => sample.ObservedAt)
                    .FirstOrDefault() is { } currentLaunchSample)
            {
                existingStatus = new ApplicationStableCandidateStatus(
                    snapshot.FamilyKey,
                    snapshot.ScopeKey,
                    snapshot.LaunchSignature,
                    ApplicationStableCandidateState.Converged,
                    currentLaunchSample.WorkingSetBytes,
                    currentLaunchSample.WorkingSetBytes,
                    snapshot.WorkingSetBytes,
                    1,
                    now);
                _stableCandidates[snapshot.ScopeKey] = existingStatus;
            }
            if (_naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var validatingWindow) &&
                validatingWindow.Validation is { } validation &&
                validatingWindow.GrowthReview is null)
            {
                var currentFamilyScopeKey = string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey)
                    ? snapshot.ScopeKey
                    : snapshot.FamilyScopeKey;
                var currentFamilyScopeLaunchSignature = string.IsNullOrWhiteSpace(
                    snapshot.FamilyScopeLaunchSignature)
                    ? snapshot.LaunchSignature
                    : snapshot.FamilyScopeLaunchSignature;
                var validationScopeChanged =
                    !string.Equals(validatingWindow.LaunchSignature, snapshot.LaunchSignature,
                        StringComparison.Ordinal) ||
                    !string.Equals(validation.FamilyScopeKey, currentFamilyScopeKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validation.FamilyScopeLaunchSignature,
                        currentFamilyScopeLaunchSignature, StringComparison.Ordinal);
                if (validationScopeChanged)
                {
                    _naturalStableWindows.Remove(snapshot.ScopeKey);
                    _stableCandidates.Remove(snapshot.ScopeKey);
                    foreach (var component in validatingWindow.ComponentKeys)
                        RemoveNaturalRecoveryEligibility(component);
                    continue;
                }

                if (ShouldPauseRegularStableObservation(
                        validatingWindow.Origin,
                        severeMemoryPressure,
                        normalizedSuppressionSettings))
                {
                    _naturalStableWindows[snapshot.ScopeKey] = PauseNaturalStableWindow(
                        validatingWindow,
                        now);
                    continue;
                }

                validatingWindow = AppendNaturalStableSample(validatingWindow, snapshot, now);
                validation = validatingWindow.Validation!;

                var effectiveValidationUpperLimit = Math.Min(
                    validation.UpperLimitBytes,
                    StableStateSuppressionPolicy.SuppressionLimitBytes(
                        validation.StableBytes,
                        normalizedSuppressionSettings));
                if (snapshot.WorkingSetBytes > effectiveValidationUpperLimit)
                {
                    if (validatingWindow.Origin ==
                        NaturalStableObservationOrigin.HistoricalBoundedConfirmation)
                    {
                        ExpireNaturalStableWindow(
                            snapshot,
                            now,
                            existingStatus,
                            validatingWindow with { PreserveConvergedStatus = false },
                            ApplicationStableObservationDecision.NotConverged);
                    }
                    else if (now < validatingWindow.Deadline && now < validation.Deadline)
                    {
                        StartNaturalStableGrowthReview(
                            snapshot,
                            now,
                            validatingWindow with
                            {
                                Validation = validation with
                                {
                                    UpperLimitBytes = effectiveValidationUpperLimit,
                                    ContinuousStableSince = null
                                }
                            });
                    }
                    else
                    {
                        ReturnToRollingObservation(snapshot, now, validatingWindow);
                    }
                    continue;
                }

                var reliableLowActivity = snapshot.IsLowActivity &&
                                          !snapshot.IsForeground &&
                                          !snapshot.FamilyScopeIsForeground;
                var previousSampleWasLowActivity = validatingWindow.WorkingSetSamples.Count >= 2 &&
                                                   validatingWindow.WorkingSetSamples[^2].IsLowActivity;
                var tolerateSingleActivityPulse = validation.ContinuousStableSince.HasValue &&
                                                   previousSampleWasLowActivity &&
                                                   now < validation.Deadline &&
                                                   now < validatingWindow.Deadline &&
                                                   !snapshot.IsForeground &&
                                                   !snapshot.FamilyScopeIsForeground;
                DateTimeOffset? continuousStableSince = reliableLowActivity
                    ? validation.ContinuousStableSince ?? now
                    : tolerateSingleActivityPulse
                        ? validation.ContinuousStableSince
                        : null;
                validation = validation with { ContinuousStableSince = continuousStableSince };
                validatingWindow = validatingWindow with { Validation = validation };
                _naturalStableWindows[snapshot.ScopeKey] = validatingWindow;

                var requiredValidation = validatingWindow.Origin ==
                    NaturalStableObservationOrigin.HistoricalBoundedConfirmation
                    ? TimeSpan.Zero
                    : NaturalStableRequiredValidation;
                if (continuousStableSince.HasValue &&
                    now - continuousStableSince.Value >= requiredValidation)
                {
                    var historicalAnchor = validatingWindow.Origin ==
                                           NaturalStableObservationOrigin.HistoricalBoundedConfirmation
                        ? StableStateSuppressionPolicy.StableReferenceBytes(
                            existingRecord!, maximumStableSamplePool)
                        : null;
                    if (validatingWindow.Origin ==
                        NaturalStableObservationOrigin.HistoricalBoundedConfirmation &&
                        validatingWindow.RequiresFirstBootAnchorGate && historicalAnchor.HasValue &&
                        snapshot.WorkingSetBytes > historicalAnchor.Value)
                    {
                        ExpireNaturalStableWindow(
                            snapshot,
                            now,
                            existingStatus,
                            validatingWindow with { PreserveConvergedStatus = false },
                            ApplicationStableObservationDecision.FirstBootAnchorExceeded);
                        continue;
                    }
                    _naturalStableWindows.Remove(snapshot.ScopeKey);
                    var historicalReview = validatingWindow.Origin ==
                                           NaturalStableObservationOrigin.HistoricalBoundedConfirmation;
                    var globalReclaimObservation = validatingWindow.Origin ==
                                                  NaturalStableObservationOrigin.GlobalReclaim;
                    var committedRecord = globalReclaimObservation
                        ? null
                        : CommitNaturalStableSample(
                            snapshot,
                            validation.StableBytes,
                            validation.StableMinimumBytes,
                            validation.StableMaximumBytes,
                            now,
                            normalizedSuppressionSettings.MinimumSamples,
                            maximumSamplesPerLaunch,
                            maximumStableSamplePool,
                            validation.BackoffObservation,
                            validation.RecoveryCycleId,
                            historicalReview);
                    if (historicalReview && !globalReclaimObservation)
                        IncrementHistoricalReviewCount(snapshot);
                    _naturalStableReviewCompletions.Remove(snapshot.ScopeKey);
                    var committedSample = committedRecord?.StableSamples
                        .OrderByDescending(sample => sample.ObservedAt)
                        .FirstOrDefault(sample => sample.ObservedAt == now);
                    var previousStatus = _stableCandidates.GetValueOrDefault(snapshot.ScopeKey);
                    var committedStatus = new ApplicationStableCandidateStatus(
                        snapshot.FamilyKey,
                        snapshot.ScopeKey,
                        snapshot.LaunchSignature,
                        ApplicationStableCandidateState.Converged,
                        validation.StableBytes,
                        validation.StableMinimumBytes,
                        snapshot.WorkingSetBytes,
                        validatingWindow.ObservationCount,
                        now);
                    _stableCandidates[snapshot.ScopeKey] = committedStatus;
                    if (committedSample is { PendingHigh: true })
                    {
                        AddNaturalStableObservation(
                            snapshot,
                            now,
                            previousStatus,
                            committedStatus,
                            ApplicationStableObservationDecision.HighAnchorPending);
                    }
                    if (validation.CompletesBackoffObservation)
                    {
                        CompleteBackoffObservation(validatingWindow.ComponentKeys, now);
                        EstablishFamilySessionHold(snapshot, now);
                    }
                    foreach (var component in validatingWindow.ComponentKeys)
                        RemoveNaturalRecoveryEligibility(component);
                    continue;
                }

                if (now >= validation.Deadline || now >= validatingWindow.Deadline)
                {
                    ReturnToRollingObservation(snapshot, now, validatingWindow);
                }
                continue;
            }
            if (_naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var growthWindow) &&
                growthWindow.Validation is { } growthValidation &&
                growthWindow.GrowthReview is { } growthReview)
            {
                var currentFamilyScopeKey = string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey)
                    ? snapshot.ScopeKey
                    : snapshot.FamilyScopeKey;
                var currentFamilyScopeLaunchSignature = string.IsNullOrWhiteSpace(
                    snapshot.FamilyScopeLaunchSignature)
                    ? snapshot.LaunchSignature
                    : snapshot.FamilyScopeLaunchSignature;
                var currentFamilyWorkingSetBytes = snapshot.FamilyScopeWorkingSetBytes > 0
                    ? snapshot.FamilyScopeWorkingSetBytes
                    : snapshot.WorkingSetBytes;
                var familyScopeChanged =
                    !string.Equals(growthReview.FamilyScopeKey, currentFamilyScopeKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(growthReview.FamilyScopeLaunchSignature,
                        currentFamilyScopeLaunchSignature, StringComparison.Ordinal);
                if (familyScopeChanged)
                {
                    _naturalStableWindows.Remove(snapshot.ScopeKey);
                    _stableCandidates.Remove(snapshot.ScopeKey);
                    foreach (var component in growthWindow.ComponentKeys)
                        RemoveNaturalRecoveryEligibility(component);
                    continue;
                }

                if (ShouldPauseRegularStableObservation(
                        growthWindow.Origin,
                        severeMemoryPressure,
                        normalizedSuppressionSettings))
                {
                    _naturalStableWindows[snapshot.ScopeKey] = PauseNaturalStableWindow(
                        growthWindow,
                        now);
                    continue;
                }

                if (now >= growthWindow.Deadline || now >= growthValidation.Deadline)
                {
                    ReturnToRollingObservation(snapshot, now, growthWindow);
                    continue;
                }
                growthWindow = AppendNaturalStableSample(growthWindow, snapshot, now);
                growthReview = growthReview with
                {
                    LatestFamilyWorkingSetBytes = currentFamilyWorkingSetBytes,
                    LastObservedAt = now
                };
                growthValidation = growthValidation with { ContinuousStableSince = null };
                var effectiveValidationUpperLimit = Math.Min(
                    growthValidation.UpperLimitBytes,
                    StableStateSuppressionPolicy.SuppressionLimitBytes(
                        growthValidation.StableBytes,
                        normalizedSuppressionSettings));
                if (snapshot.WorkingSetBytes <= effectiveValidationUpperLimit)
                {
                    var reliableLowActivity = snapshot.IsLowActivity &&
                                              !snapshot.IsForeground &&
                                              !snapshot.FamilyScopeIsForeground;
                    growthValidation = growthValidation with
                    {
                        ContinuousStableSince = reliableLowActivity ? now : null
                    };
                    growthWindow = growthWindow with
                    {
                        Validation = growthValidation,
                        GrowthReview = null
                    };
                }
                else
                {
                    growthWindow = growthWindow with
                    {
                        Validation = growthValidation,
                        GrowthReview = growthReview
                    };
                }
                _naturalStableWindows[snapshot.ScopeKey] = growthWindow;
                continue;
            }
            if (ShouldPauseRegularStableObservation(
                    snapshot.RecoveryOrigin,
                    severeMemoryPressure,
                    normalizedSuppressionSettings))
            {
                if (_naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var pressuredWindow) &&
                    string.Equals(
                        pressuredWindow.LaunchSignature,
                        snapshot.LaunchSignature,
                        StringComparison.Ordinal))
                {
                    _naturalStableWindows[snapshot.ScopeKey] = PauseNaturalStableWindow(
                        pressuredWindow,
                        now);
                }
                continue;
            }
            var hasBenefitEvidence = snapshot.ComponentKeys.All(componentKey =>
                _learning.TryGetValue(componentKey, out var learning) &&
                learning.ValidSampleCount > 0);
            var hasPendingTrim = snapshot.ComponentKeys.Any(component => _pending.ContainsKey(component));
            if (hasPendingTrim)
            {
                var validRecovery = snapshot.RecoveryStartedAt.HasValue &&
                                    snapshot.ComponentKeys.All(component =>
                                        (_pending.TryGetValue(component, out var pending) &&
                                         pending.LearnOutcome &&
                                         !pending.ReturnedToForegroundAt.HasValue) ||
                                        (_naturalRecoveryEligibleComponents.Contains(component) &&
                                         _naturalRecoveryFamilyKeys.TryGetValue(component, out var familyKey) &&
                                         string.Equals(familyKey, snapshot.FamilyKey,
                                             StringComparison.OrdinalIgnoreCase)));
                if (!validRecovery)
                {
                    _naturalStableWindows.Remove(snapshot.ScopeKey);
                    _stableCandidates.Remove(snapshot.ScopeKey);
                    continue;
                }
                if (!_naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var recoveryWindow) ||
                    !string.Equals(recoveryWindow.LaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal))
                {
                    StartNaturalStableWindow(
                        snapshot,
                        now,
                        existingStatus,
                        allowsNewBaseline: true,
                        preserveConvergedStatus: existingStatus?.State == ApplicationStableCandidateState.Converged,
                        startedAt: snapshot.RecoveryStartedAt,
                        deadline: snapshot.RecoveryDeadline ?? DateTimeOffset.MaxValue,
                        origin: snapshot.RecoveryOrigin);
                }
                else
                {
                    _naturalStableWindows[snapshot.ScopeKey] = AppendNaturalStableSample(
                        recoveryWindow,
                        snapshot,
                        now);
                }
                continue;
            }
            var observationBlocked = !hasBenefitEvidence ||
                snapshot.WorkingSetBytes <= 0 ||
                string.IsNullOrWhiteSpace(snapshot.LaunchSignature);
            if (observationBlocked)
            {
                _naturalStableWindows.Remove(snapshot.ScopeKey);
                foreach (var component in snapshot.ComponentKeys)
                    RemoveNaturalRecoveryEligibility(component);
                continue;
            }
            var activeBackoffObservation = ActiveBackoffObservationFor(
                snapshot.ComponentKeys,
                now);

            var sameRuntimeLaunch = existingStatus is not null && string.Equals(
                existingStatus.LaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal);
            var convergedRuntimeReference = existingStatus?.State == ApplicationStableCandidateState.Converged
                ? existingStatus.CandidateBytes
                : (long?)null;
            var learnedReference = existingRecord is null
                ? null
                : StableStateSuppressionPolicy.StableReferenceBytes(
                    existingRecord,
                    maximumStableSamplePool);
            var longTermReferenceLimit = existingRecord is null || suppressionSettings is null
                ? null
                : StableStateSuppressionPolicy.SuppressionLimitBytes(
                    existingRecord,
                    normalizedSuppressionSettings,
                    now);
            var reference = convergedRuntimeReference ?? learnedReference;
            var referenceLimit = reference.HasValue && suppressionSettings is not null &&
                                 reference.Value <= normalizedSuppressionSettings.MaximumStableWorkingSetBytes
                ? StableStateSuppressionPolicy.SuppressionLimitBytes(reference.Value, suppressionSettings)
                : (long?)null;
            if (sameRuntimeLaunch &&
                existingStatus is { State: ApplicationStableCandidateState.Converged } &&
                referenceLimit.HasValue &&
                !_naturalStableWindows.ContainsKey(snapshot.ScopeKey) &&
                snapshot.WorkingSetBytes > referenceLimit.Value)
            {
                _naturalStableWindows.Remove(snapshot.ScopeKey);
                existingStatus = existingStatus with
                {
                    State = ApplicationStableCandidateState.Excluded,
                    LatestObservationBytes = snapshot.WorkingSetBytes,
                    LastObservedAt = now
                };
                _stableCandidates[snapshot.ScopeKey] = existingStatus;
                continue;
            }
            if (!sameRuntimeLaunch &&
                convergedRuntimeReference.HasValue &&
                referenceLimit.HasValue &&
                snapshot.WorkingSetBytes <= referenceLimit.Value)
            {
                var previousLaunchSignature = existingStatus!.LaunchSignature;
                existingStatus = existingStatus with
                {
                    LaunchSignature = snapshot.LaunchSignature,
                    LatestObservationBytes = snapshot.WorkingSetBytes,
                    LastObservedAt = now
                };
                _stableCandidates[snapshot.ScopeKey] = existingStatus;
                sameRuntimeLaunch = true;
                if (existingRecord is not null && string.Equals(
                        existingRecord.LastStableLaunchSignature,
                        previousLaunchSignature,
                        StringComparison.Ordinal))
                {
                    var migratedSamples = StableAnchorLearningPolicy.NormalizeSamples(existingRecord)
                        .Select(sample => string.Equals(
                            sample.LaunchSignature,
                            previousLaunchSignature,
                            StringComparison.Ordinal)
                                ? sample with { LaunchSignature = snapshot.LaunchSignature }
                                : sample)
                        .ToArray();
                    existingRecord = existingRecord with
                    {
                        LastStableLaunchSignature = snapshot.LaunchSignature,
                        StableSamples = migratedSamples,
                        StableWorkingSetSamplesBytes = migratedSamples
                            .Select(sample => sample.WorkingSetBytes)
                            .ToArray()
                    };
                    _familyStableLearning[snapshot.ScopeKey] = existingRecord;
                    LearningRevision++;
                }
            }
            var runtimeReference = sameRuntimeLaunch ? convergedRuntimeReference : null;

            if (snapshot.RecoveryStartedAt is { } recoveryStartedAt &&
                _naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var preBenefitWindow) &&
                preBenefitWindow.Origin != NaturalStableObservationOrigin.PostTrim &&
                recoveryStartedAt > preBenefitWindow.StartedAt)
            {
                _naturalStableWindows.Remove(snapshot.ScopeKey);
                StartNaturalStableWindow(
                    snapshot,
                    now,
                    existingStatus,
                    allowsNewBaseline: true,
                    preserveConvergedStatus: runtimeReference.HasValue,
                    startedAt: recoveryStartedAt,
                    deadline: snapshot.RecoveryDeadline ?? DateTimeOffset.MaxValue,
                    origin: snapshot.RecoveryOrigin);
                continue;
            }

            if (!_naturalStableWindows.TryGetValue(snapshot.ScopeKey, out var window) ||
                !string.Equals(window.LaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal))
            {
                var postTrimRecovery = snapshot.ComponentKeys.All(
                    component => _naturalRecoveryEligibleComponents.Contains(component));
                var sameSampleLaunch = string.Equals(
                    existingRecord?.LastStableLaunchSignature,
                    snapshot.LaunchSignature,
                    StringComparison.Ordinal);
                var hasCurrentStableHold = existingStatus?.State ==
                                           ApplicationStableCandidateState.Converged &&
                                           string.Equals(existingStatus.LaunchSignature,
                                               snapshot.LaunchSignature,
                                               StringComparison.Ordinal);
                var boundedConfirmation = !globalReclaimSuppressed && referenceLimit.HasValue &&
                                          snapshot.WorkingSetBytes <= referenceLimit.Value &&
                                          (snapshot.RequiresFirstBootAnchorGate ||
                                           hasCurrentStableHold &&
                                           (sameSampleLaunch || longTermReferenceLimit.HasValue));
                if (!postTrimRecovery &&
                    activeBackoffObservation is null &&
                    !boundedConfirmation) continue;
                if (_naturalStableReviewCompletions.TryGetValue(
                        snapshot.ScopeKey,
                        out var reviewCompletion))
                {
                    if (!string.Equals(
                            reviewCompletion.LaunchSignature,
                            snapshot.LaunchSignature,
                            StringComparison.Ordinal))
                    {
                        _naturalStableReviewCompletions.Remove(snapshot.ScopeKey);
                    }
                    else if (activeBackoffObservation is null &&
                             now - reviewCompletion.CompletedAt < NaturalStableReviewInterval(
                                 HistoricalReviewCount(snapshot)))
                    {
                        continue;
                    }
                }
                if (activeBackoffObservation is null &&
                    sameSampleLaunch &&
                    existingRecord!.StableLastObservedAt is { } lastStableObservedAt &&
                    now - lastStableObservedAt < NaturalStableReviewInterval(
                        HistoricalReviewCount(snapshot)))
                {
                    continue;
                }
                StartNaturalStableWindow(
                    snapshot,
                    now,
                    existingStatus,
                    allowsNewBaseline: postTrimRecovery || activeBackoffObservation is not null,
                    preserveConvergedStatus: runtimeReference.HasValue,
                    startedAt: snapshot.RecoveryStartedAt,
                    deadline: snapshot.RecoveryDeadline ?? DateTimeOffset.MaxValue,
                    origin: boundedConfirmation
                        ? NaturalStableObservationOrigin.HistoricalBoundedConfirmation
                        : snapshot.RecoveryOrigin);
                continue;
            }

            if (!window.AllowsNewBaseline &&
                referenceLimit.HasValue && snapshot.WorkingSetBytes > referenceLimit.Value)
            {
                ExpireNaturalStableWindow(
                    snapshot,
                    now,
                    existingStatus,
                    window with { PreserveConvergedStatus = false },
                    ApplicationStableObservationDecision.NotConverged);
                continue;
            }

            if (now > window.Deadline)
            {
                ExpireNaturalStableWindow(
                    snapshot,
                    now,
                    existingStatus,
                    window,
                    ApplicationStableObservationDecision.CandidateExpired,
                    recordReviewCompletion: activeBackoffObservation is null);
                continue;
            }
            window = AppendNaturalStableSample(window, snapshot, now);
            _naturalStableWindows[snapshot.ScopeKey] = window;
            var totalObservation = now > window.StartedAt
                ? now - window.StartedAt
                : TimeSpan.Zero;
            var minimumObservation = window.Origin ==
                                     NaturalStableObservationOrigin.HistoricalBoundedConfirmation
                ? NaturalStableConvergedTail
                : stableConfirmationMinimum;
            if (totalObservation < minimumObservation) continue;

            var stableEstimate = ConvergedTailEstimate(window.WorkingSetSamples, now);
            if (stableEstimate is null)
            {
                if (now >= window.Deadline)
                    ReturnToRollingObservation(snapshot, now, window);
                continue;
            }

            var provisional = new ApplicationStableCandidateStatus(
                snapshot.FamilyKey, snapshot.ScopeKey, snapshot.LaunchSignature,
                ApplicationStableCandidateState.Provisional,
                stableEstimate.CenterBytes,
                window.MinimumBytes, snapshot.WorkingSetBytes, window.ObservationCount, now);
            _stableCandidates[snapshot.ScopeKey] = provisional;
            _naturalStableWindows[snapshot.ScopeKey] = StartNaturalStableValidation(
                snapshot,
                window,
                stableEstimate.CenterBytes,
                stableEstimate.MinimumBytes,
                stableEstimate.MaximumBytes,
                now,
                normalizedSuppressionSettings,
                activeBackoffObservation is not null,
                activeBackoffObservation is not null);
            AddNaturalStableObservation(snapshot, now, existingStatus, provisional,
                ApplicationStableObservationDecision.Converged);
        }

        foreach (var scopeKey in _naturalStableWindows.Keys.Where(key => !seenScopes.Contains(key)).ToArray())
            _naturalStableWindows.Remove(scopeKey);
        foreach (var scopeKey in _naturalStableReviewCompletions.Keys
                     .Where(key => !seenScopes.Contains(key)).ToArray())
            _naturalStableReviewCompletions.Remove(scopeKey);
        foreach (var scopeKey in _stableCandidates.Keys.Where(key =>
                     key.Contains("|scope:", StringComparison.OrdinalIgnoreCase) &&
                     !seenScopes.Contains(key)).ToArray())
            _stableCandidates.Remove(scopeKey);
        foreach (var component in _naturalRecoveryEligibleComponents
                     .Where(component => !seenComponents.Contains(component)).ToArray())
            RemoveNaturalRecoveryEligibility(component);
    }

    private static NaturalStableWindow AppendNaturalStableSample(
        NaturalStableWindow window,
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now)
    {
        if (now <= window.LastObservedAt) return window;
        var elapsed = now - window.LastObservedAt;
        var cutoff = now - NaturalStableRollingRetention;
        var recentSamples = window.WorkingSetSamples
            .Where(sample => sample.ObservedAt >= cutoff)
            .ToArray();
        var priorAnchor = window.WorkingSetSamples
            .Where(sample => sample.ObservedAt < cutoff)
            .TakeLast(1);
        return window with
        {
            MinimumBytes = Math.Min(window.MinimumBytes, snapshot.WorkingSetBytes),
            MaximumBytes = Math.Max(window.MaximumBytes, snapshot.WorkingSetBytes),
            LatestBytes = snapshot.WorkingSetBytes,
            ObservationCount = window.ObservationCount + 1,
            WorkingSetSamples = priorAnchor
                .Concat(recentSamples)
            .Append(new TimedWorkingSetSample(now, snapshot.WorkingSetBytes, snapshot.IsLowActivity))
                .ToArray(),
            StableDuration = window.StableDuration + elapsed,
            TotalObservationDuration = now - window.StartedAt,
            LastObservedAt = now
        };
    }

    private static StableSampleEstimate? ConvergedTailEstimate(
        IReadOnlyList<TimedWorkingSetSample> samples,
        DateTimeOffset now)
    {
        var tailStart = now - NaturalStableConvergedTail;
        var tail = samples.Where(sample => sample.ObservedAt >= tailStart).ToArray();
        if (tail.Length < 3 ||
            tail[0].ObservedAt > tailStart + TimeSpan.FromSeconds(10) ||
            now - tail[^1].ObservedAt > TimeSpan.FromSeconds(10))
        {
            var endpoints = samples.TakeLast(2).ToArray();
            if (endpoints.Length < 2 ||
                endpoints.Any(sample => !sample.IsLowActivity) ||
                endpoints[1].ObservedAt - endpoints[0].ObservedAt < NaturalStableConvergedTail ||
                now - endpoints[1].ObservedAt > TimeSpan.FromSeconds(10)) return null;
            var endpointTolerance = Math.Max(
                8L * 1024 * 1024,
                (long)Math.Round(endpoints[0].WorkingSetBytes * 0.03d));
            if (Math.Abs((double)endpoints[1].WorkingSetBytes - endpoints[0].WorkingSetBytes) >
                endpointTolerance) return null;
            return new StableSampleEstimate(
                Math.Min(endpoints[0].WorkingSetBytes, endpoints[1].WorkingSetBytes),
                endpoints[1].WorkingSetBytes,
                Math.Max(endpoints[0].WorkingSetBytes, endpoints[1].WorkingSetBytes));
        }
        if (tail.Count(sample => !sample.IsLowActivity) > 1) return null;

        var middle = tailStart + TimeSpan.FromTicks(NaturalStableConvergedTail.Ticks / 2);
        var first = tail.Where(sample => sample.ObservedAt < middle)
            .Select(sample => sample.WorkingSetBytes)
            .OrderBy(value => value)
            .ToArray();
        var second = tail.Where(sample => sample.ObservedAt >= middle)
            .Select(sample => sample.WorkingSetBytes)
            .OrderBy(value => value)
            .ToArray();
        if (first.Length == 0 || second.Length == 0) return null;
        var firstMedian = StableWorkingSetLearningPolicy.Median(first);
        var secondMedian = StableWorkingSetLearningPolicy.Median(second);
        var tolerance = Math.Max(8L * 1024 * 1024, (long)Math.Round(firstMedian * 0.03d));
        if (Math.Abs((double)secondMedian - firstMedian) > tolerance) return null;
        var all = tail.Select(sample => sample.WorkingSetBytes).OrderBy(value => value).ToArray();
        return new StableSampleEstimate(
            StablePercentile(all, 0.25d),
            secondMedian,
            StablePercentile(all, 0.75d));
    }

    private static long StablePercentile(IReadOnlyList<long> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        var index = (int)Math.Round((sortedValues.Count - 1) * Math.Clamp(percentile, 0d, 1d));
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private static bool HasSustainedNaturalStableGrowth(
        IReadOnlyList<TimedWorkingSetSample> samples,
        DateTimeOffset now)
    {
        var start = now - TimeSpan.FromSeconds(90);
        var recent = samples
            .Where(sample => sample.ObservedAt >= start && sample.IsLowActivity)
            .ToArray();
        if (recent.Length < 4 || recent[0].ObservedAt > start + TimeSpan.FromSeconds(10)) return false;
        var medians = Enumerable.Range(0, 3)
            .Select(index =>
            {
                var bucketStart = start + TimeSpan.FromSeconds(index * 30);
                var bucketEnd = bucketStart + TimeSpan.FromSeconds(30);
                var values = recent
                    .Where(sample => sample.ObservedAt >= bucketStart &&
                                     (index == 2 ? sample.ObservedAt <= bucketEnd : sample.ObservedAt < bucketEnd))
                    .Select(sample => sample.WorkingSetBytes)
                    .OrderBy(value => value)
                    .ToArray();
                return values.Length == 0
                    ? (long?)null
                    : StableWorkingSetLearningPolicy.Median(values);
            })
            .ToArray();
        if (medians.Any(value => !value.HasValue)) return false;

        static bool Meaningful(long earlier, long later)
        {
            var tolerance = Math.Max(8L * 1024 * 1024, (long)Math.Round(earlier * 0.03d));
            return later - earlier > tolerance;
        }

        return Meaningful(medians[0]!.Value, medians[1]!.Value) &&
               Meaningful(medians[1]!.Value, medians[2]!.Value);
    }

    private void StartNaturalStableGrowthReview(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now,
        NaturalStableWindow window)
    {
        var validation = window.Validation ??
            throw new InvalidOperationException("Growth review requires provisional validation.");
        var familyScopeKey = string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey)
            ? snapshot.ScopeKey
            : snapshot.FamilyScopeKey;
        var familyScopeComponents = snapshot.FamilyScopeComponentKeys.Count == 0
            ? snapshot.ComponentKeys
            : snapshot.FamilyScopeComponentKeys;
        var familyScopeLaunchSignature = string.IsNullOrWhiteSpace(snapshot.FamilyScopeLaunchSignature)
            ? snapshot.LaunchSignature
            : snapshot.FamilyScopeLaunchSignature;
        var currentFamilyScopeWorkingSet = snapshot.FamilyScopeWorkingSetBytes > 0
            ? snapshot.FamilyScopeWorkingSetBytes
            : snapshot.WorkingSetBytes;
        var familyScopeWorkingSet = validation.BaselineFamilyWorkingSetBytes > 0
            ? validation.BaselineFamilyWorkingSetBytes
            : currentFamilyScopeWorkingSet;
        _naturalStableWindows[snapshot.ScopeKey] = window with
        {
            GrowthReview = new NaturalStableGrowthReview(
            familyScopeKey,
            familyScopeComponents,
            familyScopeLaunchSignature,
            now,
            familyScopeWorkingSet,
            currentFamilyScopeWorkingSet,
            Math.Clamp(
                (long)Math.Round(Math.Max(0, familyScopeWorkingSet) * NaturalStableGrowthRelativeIncrease),
                NaturalStableGrowthMinimumIncreaseBytes,
                NaturalStableGrowthMaximumIncreaseBytes),
            now)
        };
        _stableCandidates[snapshot.ScopeKey] = new ApplicationStableCandidateStatus(
            snapshot.FamilyKey, snapshot.ScopeKey, snapshot.LaunchSignature,
            ApplicationStableCandidateState.Provisional, 0,
            window.LatestBytes, snapshot.WorkingSetBytes, window.ObservationCount + 1, now);
    }

    private NaturalStableWindow StartNaturalStableValidation(
        NaturalStableStateSnapshot snapshot,
        NaturalStableWindow window,
        long stableBytes,
        long stableMinimumBytes,
        long stableMaximumBytes,
        DateTimeOffset now,
        StableStateSuppressionSettings settings,
        bool backoffObservation,
        bool completesBackoffObservation,
        string? recoveryCycleIdOverride = null,
        DateTimeOffset? preserveValidationDeadline = null)
    {
        var familyScopeKey = string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey)
            ? snapshot.ScopeKey
            : snapshot.FamilyScopeKey;
        var familyScopeLaunchSignature = string.IsNullOrWhiteSpace(snapshot.FamilyScopeLaunchSignature)
            ? snapshot.LaunchSignature
            : snapshot.FamilyScopeLaunchSignature;
        var familyScopeWorkingSetBytes = snapshot.FamilyScopeWorkingSetBytes > 0
            ? snapshot.FamilyScopeWorkingSetBytes
            : snapshot.WorkingSetBytes;
        var validationDeadline = preserveValidationDeadline ??
            MinDeadline(now + settings.MaximumStableValidationDuration, window.Deadline);
        return window with
        {
            Validation = new NaturalStableValidation(
            now,
            validationDeadline,
            snapshot.IsLowActivity && !snapshot.IsForeground && !snapshot.FamilyScopeIsForeground
                ? now
                : null,
            familyScopeKey,
            familyScopeLaunchSignature,
            StableStateSuppressionPolicy.SuppressionLimitBytes(stableBytes, settings),
            familyScopeWorkingSetBytes,
            stableBytes,
            stableMinimumBytes,
            stableMaximumBytes,
            string.IsNullOrWhiteSpace(recoveryCycleIdOverride)
                ? ResolveNaturalStableRecoveryCycleId(snapshot, backoffObservation)
                : recoveryCycleIdOverride,
            backoffObservation,
            completesBackoffObservation)
        };
    }

    private static DateTimeOffset MinDeadline(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private void StartNaturalStableWindow(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now,
        ApplicationStableCandidateStatus? previous,
        bool allowsNewBaseline,
        bool preserveConvergedStatus,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? deadline = null,
        NaturalStableObservationOrigin origin = NaturalStableObservationOrigin.Unknown)
    {
        var windowStartedAt = startedAt is { } recoveryStartedAt && recoveryStartedAt < now
            ? recoveryStartedAt
            : now;
        var windowDeadline = deadline ?? DateTimeOffset.MaxValue;
        var status = new ApplicationStableCandidateStatus(
            snapshot.FamilyKey, snapshot.ScopeKey, snapshot.LaunchSignature,
            ApplicationStableCandidateState.Provisional, snapshot.WorkingSetBytes,
            previous?.LatestObservationBytes ?? 0, snapshot.WorkingSetBytes, 1, now);
        if (!preserveConvergedStatus)
            _stableCandidates[snapshot.ScopeKey] = status;
        _naturalStableWindows[snapshot.ScopeKey] = new NaturalStableWindow(
            snapshot.FamilyKey, snapshot.LaunchSignature, windowStartedAt, windowDeadline,
            snapshot.ComponentKeys, snapshot.WorkingSetBytes,
            snapshot.WorkingSetBytes, snapshot.WorkingSetBytes, 1,
            new[] { new TimedWorkingSetSample(now, snapshot.WorkingSetBytes, snapshot.IsLowActivity) },
            TimeSpan.Zero, TimeSpan.Zero, now,
            allowsNewBaseline, preserveConvergedStatus, origin)
        {
            RequiresFirstBootAnchorGate = snapshot.RequiresFirstBootAnchorGate
        };
        AddNaturalStableObservation(snapshot, now, previous, status,
            ApplicationStableObservationDecision.FirstObservation);
    }

    private static bool ShouldPauseRegularStableObservation(
        NaturalStableObservationOrigin origin,
        bool severeMemoryPressure,
        StableStateSuppressionSettings settings) =>
        severeMemoryPressure &&
        settings.IgnoreRegularObservationUnderSeverePressure &&
        origin != NaturalStableObservationOrigin.BackoffRecovery;

    private static NaturalStableWindow PauseNaturalStableWindow(
        NaturalStableWindow window,
        DateTimeOffset now)
    {
        if (now <= window.LastObservedAt) return window;
        var paused = now - window.LastObservedAt;
        DateTimeOffset Shift(DateTimeOffset value) => value == DateTimeOffset.MaxValue
            ? value
            : value + paused;
        return window with
        {
            StartedAt = Shift(window.StartedAt),
            Deadline = Shift(window.Deadline),
            LastObservedAt = now,
            Validation = window.Validation is null
                ? null
                : window.Validation with
                {
                    StartedAt = Shift(window.Validation.StartedAt),
                    Deadline = Shift(window.Validation.Deadline),
                    ContinuousStableSince = window.Validation.ContinuousStableSince is { } stableSince
                        ? Shift(stableSince)
                        : null
                },
            GrowthReview = window.GrowthReview is null
                ? null
                : window.GrowthReview with
                {
                    StartedAt = Shift(window.GrowthReview.StartedAt),
                    LastObservedAt = now
                }
        };
    }

    private void ReturnToRollingObservation(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now,
        NaturalStableWindow window)
    {
        if (now >= window.Deadline)
        {
            _naturalStableWindows.Remove(snapshot.ScopeKey);
            _stableCandidates.Remove(snapshot.ScopeKey);
            foreach (var component in window.ComponentKeys)
                RemoveNaturalRecoveryEligibility(component);
            return;
        }

        var reset = new NaturalStableWindow(
            snapshot.FamilyKey,
            snapshot.LaunchSignature,
            now,
            window.Deadline,
            snapshot.ComponentKeys,
            snapshot.WorkingSetBytes,
            snapshot.WorkingSetBytes,
            snapshot.WorkingSetBytes,
            1,
            new[] { new TimedWorkingSetSample(now, snapshot.WorkingSetBytes, snapshot.IsLowActivity) },
            TimeSpan.Zero,
            TimeSpan.Zero,
            now,
            window.AllowsNewBaseline,
            PreserveConvergedStatus: false,
            window.Origin);
        _naturalStableWindows[snapshot.ScopeKey] = reset;
        _stableCandidates[snapshot.ScopeKey] = new ApplicationStableCandidateStatus(
            snapshot.FamilyKey,
            snapshot.ScopeKey,
            snapshot.LaunchSignature,
            ApplicationStableCandidateState.Provisional,
            snapshot.WorkingSetBytes,
            window.LatestBytes,
            snapshot.WorkingSetBytes,
            1,
            now);
    }

    private ApplicationStableLearningRecord? CommitNaturalStableSample(
        NaturalStableStateSnapshot snapshot,
        long stableBytes,
        long stableMinimumBytes,
        long stableMaximumBytes,
        DateTimeOffset now,
        int minimumSamples,
        int maximumSamplesPerLaunch,
        int maximumStableSamplePool,
        bool backoffObservation = false,
        string? recoveryCycleIdOverride = null,
        bool historicalReview = false)
    {
        var previous = _familyStableLearning.GetValueOrDefault(snapshot.ScopeKey);
        var sameLaunch = string.Equals(
            previous?.LastStableLaunchSignature, snapshot.LaunchSignature, StringComparison.Ordinal);
        var recoveryCycleId = string.IsNullOrWhiteSpace(recoveryCycleIdOverride)
            ? ResolveNaturalStableRecoveryCycleId(snapshot, backoffObservation)
            : recoveryCycleIdOverride;
        var seed = previous ?? new ApplicationStableLearningRecord(
            snapshot.FamilyKey,
            Array.Empty<long>(),
            null,
            null)
        {
            ComponentKeys = snapshot.ComponentKeys.ToArray(),
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 0
        };
        if (sameLaunch)
        {
            seed = RollStableLaunchSlot(
                seed,
                snapshot.LaunchSignature,
                stableBytes,
                maximumSamplesPerLaunch);
        }
        var committed = StableAnchorLearningPolicy.CommitSample(
            seed,
            new ApplicationStableSample(
                stableBytes,
                now,
                snapshot.LaunchSignature,
                recoveryCycleId,
                seed.AnchorGeneration,
                PendingHigh: false)
            {
                MinimumWorkingSetBytes = stableMinimumBytes,
                MaximumWorkingSetBytes = stableMaximumBytes
            },
            minimumSamples,
            maximumStableSamplePool);
        var anchorGenerationChanged = previous is not null &&
                                      committed.AnchorGeneration != previous.AnchorGeneration;
        var committedSampleIsAccepted = committed.AnchorGeneration > 0 &&
                                        committed.AnchorGenerationBaselineBytes > 0 &&
                                        committed.StableSamples.Any(sample =>
                                            sample.ObservedAt == now &&
                                            sample.Generation == committed.AnchorGeneration &&
                                            !sample.PendingHigh);
        var updated = committed with
        {
            FamilyKey = snapshot.FamilyKey,
            ComponentKeys = snapshot.ComponentKeys.ToArray(),
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableLastObservedAt = now,
            LastStableLaunchSignature = snapshot.LaunchSignature,
            LastStableLaunchSampleCount = Math.Clamp(
                StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
                    committed,
                    snapshot.LaunchSignature,
                    maximumStableSamplePool),
                0,
                maximumSamplesPerLaunch),
            HistoricalReviewSuccessCount = 0,
            HistoricalReviewScheduleVersion = 2
        };
        _familyStableLearning[snapshot.ScopeKey] = updated;
        LearningRevision++;
        return updated;
    }

    private string ResolveNaturalStableRecoveryCycleId(
        NaturalStableStateSnapshot snapshot,
        bool backoffObservation)
    {
        var recoveryCycleIds = snapshot.ComponentKeys
            .Select(component => _naturalRecoveryCycleIds.GetValueOrDefault(component))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return recoveryCycleIds.Length == 0
            ? backoffObservation
                ? $"backoff:observation:{snapshot.LaunchSignature}"
                : $"passive:{snapshot.LaunchSignature}"
            : string.Join('|', recoveryCycleIds);
    }

    private static ApplicationStableLearningRecord RollStableLaunchSlot(
        ApplicationStableLearningRecord record,
        string launchSignature,
        long incomingWorkingSetBytes,
        int maximumSamplesPerLaunch)
    {
        var samples = StableAnchorLearningPolicy.NormalizeSamples(record).ToList();
        var launchSamples = samples
            .Select((sample, index) => (Sample: sample, Index: index))
            .Where(entry => string.Equals(
                entry.Sample.LaunchSignature,
                launchSignature,
                StringComparison.Ordinal))
            .OrderBy(entry => entry.Sample.ObservedAt)
            .ThenBy(entry => entry.Index)
            .ToList();
        var limit = Math.Max(1, maximumSamplesPerLaunch);
        var acceptedLaunchCount = launchSamples.Count(entry => !entry.Sample.PendingHigh);
        var incomingReference = StableAnchorLearningPolicy.EffectiveAnchorBytes(record) ??
                                record.AnchorGenerationBaselineBytes;
        var incomingPendingHigh = record.AnchorGeneration > 0 &&
                                  record.AnchorGenerationBaselineBytes > 0 &&
                                  StableAnchorLearningPolicy.IsHighSample(
                                      incomingReference,
                                      incomingWorkingSetBytes);
        var matchingLimit = incomingPendingHigh
            ? Math.Max(1, limit - Math.Min(limit, acceptedLaunchCount))
            : limit;
        var matchingSamples = launchSamples
            .Where(entry => entry.Sample.PendingHigh == incomingPendingHigh)
            .ToList();
        while (matchingSamples.Count >= matchingLimit)
        {
            var replacement = matchingSamples[0];
            samples.RemoveAt(replacement.Index);
            launchSamples = samples
                .Select((sample, index) => (Sample: sample, Index: index))
                .Where(entry => string.Equals(
                    entry.Sample.LaunchSignature,
                    launchSignature,
                    StringComparison.Ordinal))
                .OrderBy(entry => entry.Sample.ObservedAt)
                .ThenBy(entry => entry.Index)
                .ToList();
            matchingSamples = launchSamples
                .Where(entry => entry.Sample.PendingHigh == incomingPendingHigh)
                .ToList();
        }

        return record with
        {
            StableSamples = samples,
            StableWorkingSetSamplesBytes = samples.Select(sample => sample.WorkingSetBytes).ToArray()
        };
    }

    private void RemoveNaturalRecoveryEligibility(string componentKey)
    {
        _naturalRecoveryEligibleComponents.Remove(componentKey);
        _naturalRecoveryCycleIds.Remove(componentKey);
        _naturalRecoveryFamilyKeys.Remove(componentKey);
        _naturalRecoveryOrigins.Remove(componentKey);
        _globalReclaimSuppressedLaunchesByComponent.Remove(componentKey);
        _naturalRecoveryStartedAts.Remove(componentKey);
    }

    private bool HasUsableStableAnchor(string componentKey) =>
        _familyStableLearning.Values.Any(record =>
            record.ComponentKeys.Contains(componentKey, StringComparer.OrdinalIgnoreCase) &&
            StableAnchorLearningPolicy.AcceptedSampleCount(record) > 0);

    private bool IsNaturalRecoveryEligibilityActive(
        string componentKey,
        DateTimeOffset now,
        TimeSpan observationWindow) =>
        _naturalRecoveryStartedAts.TryGetValue(componentKey, out var startedAt) &&
        startedAt + observationWindow > now;

    private void ExpireNaturalStableWindow(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now,
        ApplicationStableCandidateStatus? existingStatus,
        NaturalStableWindow window,
        ApplicationStableObservationDecision decision,
        bool recordReviewCompletion = true)
    {
        _naturalStableWindows.Remove(snapshot.ScopeKey);
        if (recordReviewCompletion && !window.AllowsNewBaseline)
        {
            _naturalStableReviewCompletions[snapshot.ScopeKey] = new NaturalStableReviewCompletion(
                snapshot.LaunchSignature,
                now);
        }
        if (window.PreserveConvergedStatus)
        {
            foreach (var component in window.ComponentKeys)
                RemoveNaturalRecoveryEligibility(component);
            return;
        }
        foreach (var component in window.ComponentKeys)
            RemoveNaturalRecoveryEligibility(component);
        var expired = new ApplicationStableCandidateStatus(
            snapshot.FamilyKey, snapshot.ScopeKey, snapshot.LaunchSignature,
            ApplicationStableCandidateState.Excluded, 0,
            window.LatestBytes, snapshot.WorkingSetBytes, window.ObservationCount + 1, now);
        _stableCandidates[snapshot.ScopeKey] = expired;
        AddNaturalStableObservation(snapshot, now, existingStatus, expired, decision);
    }

    private void AddNaturalStableObservation(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now,
        ApplicationStableCandidateStatus? previous,
        ApplicationStableCandidateStatus next,
        ApplicationStableObservationDecision decision) =>
        _completedStableObservations.Add(new ApplicationStableObservation(
            null, snapshot.FamilyKey, snapshot.ScopeKey, snapshot.LaunchSignature, now,
            snapshot.ComponentKeys.Count, snapshot.WorkingSetBytes,
            previous?.LatestObservationBytes ?? 0,
            StableWorkingSetLearningPolicy.ConvergenceToleranceBytes(
                previous?.LatestObservationBytes ?? 0, snapshot.WorkingSetBytes),
            true, previous?.State, next.State, decision));

    private ActiveBackoffObservationContext? ActiveBackoffObservationFor(
        IReadOnlyCollection<string> componentKeys,
        DateTimeOffset now)
    {
        var states = componentKeys
            .Where(component => _states.ContainsKey(component))
            .Select(component => _states[component])
            .ToArray();
        var longTerm = states
            .Select(state => state.LongTerm)
            .FirstOrDefault(state => state is { RetryPermitted: false });
        if (longTerm is not null)
        {
            return new ActiveBackoffObservationContext(
                IsLongTerm: true,
                longTerm.BaselineWorkingSetBytes);
        }

        return states.Any(state => state.BlockedUntil > now)
            ? new ActiveBackoffObservationContext(IsLongTerm: false, BaselineWorkingSetBytes: 0)
            : null;
    }

    private void CompleteBackoffObservation(
        IReadOnlyCollection<string> componentKeys,
        DateTimeOffset now)
    {
        foreach (var component in componentKeys)
        {
            if (!_states.TryGetValue(component, out var state) ||
                (state.LongTerm is not { RetryPermitted: false } && state.BlockedUntil <= now))
            {
                continue;
            }

            _states[component] = state with
            {
                BlockedUntil = DateTimeOffset.MinValue,
                LongTerm = null
            };
        }
    }

    private void EstablishFamilySessionHold(
        NaturalStableStateSnapshot snapshot,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FamilyScopeKey) ||
            snapshot.FamilyScopeComponentKeys.Count == 0 ||
            string.IsNullOrWhiteSpace(snapshot.FamilyScopeLaunchSignature) ||
            snapshot.FamilyScopeWorkingSetBytes <= 0)
        {
            return;
        }

        _stableCandidates[snapshot.FamilyScopeKey] = new ApplicationStableCandidateStatus(
            snapshot.FamilyKey,
            snapshot.FamilyScopeKey,
            snapshot.FamilyScopeLaunchSignature,
            ApplicationStableCandidateState.Converged,
            snapshot.FamilyScopeWorkingSetBytes,
            snapshot.FamilyScopeWorkingSetBytes,
            snapshot.FamilyScopeWorkingSetBytes,
            1,
            now);
    }

    public void UpdateLongTermRetryPermissions(
        IReadOnlyList<ProcessFamilySnapshot> families,
        bool severeMemoryPressure,
        long minimumFamilyWorkingSetBytes,
        StableStateSuppressionSettings growthSettings,
        DateTimeOffset now)
    {
        var familiesByKey = families.ToDictionary(
            family => family.Key,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _states.ToArray())
        {
            var state = pair.Value;
            if (state.AllowForegroundIdleRetry && state.Stage == 2 &&
                state.BlockedUntil > now &&
                familiesByKey.TryGetValue(state.FamilyKey, out var timedFamily))
            {
                var timedProcesses = ComponentProcesses(pair.Key, timedFamily);
                if (timedProcesses.Count > 0)
                {
                    var timedForeground = timedProcesses.Any(process => process.IsForeground);
                    var timedLowActivity = !timedForeground &&
                        timedProcesses.All(process => process.HasReliableActivitySample) &&
                        timedProcesses.Sum(process => Math.Max(0, process.CpuPercent)) <
                        BackgroundActivityTracker.DeepReleaseActiveCpuThresholdPercent &&
                        timedProcesses.Sum(process => Math.Max(0, process.IoBytesPerSecond)) <
                        BackgroundActivityTracker.DeepReleaseActiveIoThresholdBytesPerSecond;
                    var sawForeground = state.TimedSawForeground || timedForeground;
                    DateTimeOffset? timedLowActivityStartedAt = timedForeground || !timedLowActivity
                        ? null
                        : state.TimedBackgroundLowActivityStartedAt ?? now;
                    state = state with
                    {
                        TimedSawForeground = sawForeground,
                        TimedBackgroundLowActivityStartedAt = timedLowActivityStartedAt
                    };
                    if (sawForeground && timedLowActivityStartedAt.HasValue &&
                        now - timedLowActivityStartedAt.Value >= BackgroundActivityTracker.MinimumObservation)
                    {
                        state = state with { BlockedUntil = DateTimeOffset.MinValue };
                    }
                    _states[pair.Key] = state;
                }
            }
            if (state.LongTerm is not { RetryPermitted: false } longTerm ||
                !familiesByKey.TryGetValue(state.FamilyKey, out var family))
            {
                continue;
            }

            var componentProcesses = ComponentProcesses(pair.Key, family);
            if (componentProcesses.Count == 0) continue;

            var isForeground = componentProcesses.Any(process => process.IsForeground);
            if (isForeground)
            {
                longTerm = longTerm with
                {
                    SawForeground = true,
                    BackgroundLowActivityStartedAt = null
                };
                state = state with { LongTerm = longTerm };
                _states[pair.Key] = state;
            }

            if (_pending.ContainsKey(pair.Key)) continue;

            var currentWorkingSet = componentProcesses.Aggregate(0L, (total, process) =>
            {
                var bytes = Math.Max(0, process.WorkingSetBytes);
                return bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
            });
            var growthLimit = longTerm.BaselineWorkingSetBytes > 0
                ? StableStateSuppressionPolicy.SuppressionLimitBytes(
                    longTerm.BaselineWorkingSetBytes,
                    growthSettings)
                : long.MaxValue;
            var growthRetry = !isForeground && currentWorkingSet > growthLimit;
            var severePressureRetry = !isForeground && severeMemoryPressure &&
                currentWorkingSet >= minimumFamilyWorkingSetBytes;

            var strictLowActivity = !isForeground &&
                componentProcesses.All(process => process.HasReliableActivitySample) &&
                componentProcesses.Sum(process => Math.Max(0, process.CpuPercent)) <
                BackgroundActivityTracker.DeepReleaseActiveCpuThresholdPercent &&
                componentProcesses.Sum(process => Math.Max(0, process.IoBytesPerSecond)) <
                BackgroundActivityTracker.DeepReleaseActiveIoThresholdBytesPerSecond;
            DateTimeOffset? lowActivityStartedAt = strictLowActivity
                ? longTerm.BackgroundLowActivityStartedAt ?? now
                : null;
            longTerm = longTerm with { BackgroundLowActivityStartedAt = lowActivityStartedAt };
            _states[pair.Key] = state with { LongTerm = longTerm };
            var sustainedComponentIdle = lowActivityStartedAt.HasValue &&
                now - lowActivityStartedAt.Value >= BackgroundActivityTracker.MinimumObservation;
            var phaseChanged = longTerm.SawForeground && sustainedComponentIdle;
            var idleTimeoutElapsed = sustainedComponentIdle &&
                now - longTerm.StartedAt >= LongTermIdleObservation;
            if (!growthRetry && !severePressureRetry && !phaseChanged && !idleTimeoutElapsed) continue;

            _states[pair.Key] = state with
            {
                LongTerm = longTerm with { RetryPermitted = true }
            };
        }
    }

    public IReadOnlyList<ApplicationReboundOutcome> DrainCompletedOutcomes()
    {
        var outcomes = _completedOutcomes.ToArray();
        _completedOutcomes.Clear();
        return outcomes;
    }

    public IReadOnlyList<ApplicationStableObservation> DrainCompletedStableObservations()
    {
        if (_completedStableObservations.Count == 0) return Array.Empty<ApplicationStableObservation>();
        var completed = _completedStableObservations.ToArray();
        _completedStableObservations.Clear();
        return completed;
    }

    public bool IsBlocked(string familyKey, DateTimeOffset now) =>
        _states.Any(pair =>
            (string.Equals(pair.Key, familyKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(pair.Value.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase)) &&
            (pair.Value.LongTerm is { RetryPermitted: false } || pair.Value.BlockedUntil > now));

    public ApplicationBackoffStatus? GetBackoffStatus(string familyKey, DateTimeOffset now)
        => GetBackoffStatus(familyKey, componentKeys: null, now);

    public ApplicationBackoffStatus? GetBackoffStatus(
        string familyKey,
        IReadOnlyCollection<string> componentKeys,
        DateTimeOffset now) => GetBackoffStatus(familyKey, componentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase), now);

    private ApplicationBackoffStatus? GetBackoffStatus(
        string familyKey,
        IReadOnlySet<string>? componentKeys,
        DateTimeOffset now)
    {
        var states = _states
            .Where(pair =>
                componentKeys is null
                    ? string.Equals(pair.Key, familyKey, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(pair.Value.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase)
                    : componentKeys.Contains(pair.Key))
            .Select(pair => pair.Value)
            .Where(state => state.LongTerm is { RetryPermitted: false } || state.BlockedUntil > now)
            .ToArray();
        if (states.Length > 0)
        {
            var longTerm = states.FirstOrDefault(state => state.LongTerm is not null);
            var state = longTerm ?? states.OrderByDescending(candidate => candidate.BlockedUntil).First();
            return new ApplicationBackoffStatus(
                states.Max(candidate => candidate.Count),
                state.LongTerm is null ? state.BlockedUntil : null,
                state.LongTerm is not null)
            {
                LongTermSawForeground = state.LongTerm?.SawForeground ?? false
            };
        }

        var pending = _pending.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase) &&
            (componentKeys is null || componentKeys.Contains(candidate.ComponentKey)) &&
            now - candidate.StartedAt < candidate.Settings.LateWindow);
        return pending is not null
            ? new ApplicationBackoffStatus(ReboundCount(familyKey), null, false)
            {
                ObservationPending = true
            }
            : null;
    }

    public IReadOnlySet<string> BlockedFamilyKeys(DateTimeOffset now) =>
        _states
            .Where(pair =>
                pair.Value.LongTerm is { RetryPermitted: false } ||
                pair.Value.BlockedUntil > now)
            .Select(pair => pair.Value.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> BlockedComponentKeys(DateTimeOffset now) =>
        _states
            .Where(pair =>
                pair.Value.LongTerm is { RetryPermitted: false } ||
                pair.Value.BlockedUntil > now)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public int ReboundCount(string familyKey) =>
        _states.TryGetValue(familyKey, out var state)
            ? state.Count
            : _states.Values
                .Where(candidate => string.Equals(
                    candidate.FamilyKey,
                    familyKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Count)
                .DefaultIfEmpty(0)
                .Max();

    public IReadOnlyList<ApplicationBackoffProgress> CaptureProgress(DateTimeOffset now) =>
        _states
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => new ApplicationBackoffProgress(
                pair.Value.FamilyKey,
                pair.Value.Count,
                Math.Max(0, (pair.Value.BlockedUntil - now).TotalSeconds),
                pair.Value.LongTerm is null
                    ? null
                    : Math.Max(0, (now - pair.Value.LongTerm.StartedAt).TotalSeconds),
                pair.Value.LongTerm?.SawForeground ?? false,
                pair.Value.LongTerm?.RetryPermitted ?? false)
            {
                TargetKey = pair.Key,
                LongTermBaselineWorkingSetBytes = pair.Value.LongTerm?.BaselineWorkingSetBytes ?? 0,
                BackoffStage = pair.Value.Stage,
                AllowForegroundIdleRetry = pair.Value.AllowForegroundIdleRetry,
                TimedBackoffSawForeground = pair.Value.TimedSawForeground,
                TimedBackgroundLowActivitySeconds = pair.Value.TimedBackgroundLowActivityStartedAt is { } lowActivityAt
                    ? Math.Max(0, (now - lowActivityAt).TotalSeconds)
                    : null
            })
            .ToArray();

    public void RestoreProgress(
        IEnumerable<ApplicationBackoffProgress>? progress,
        DateTimeOffset now)
    {
        foreach (var item in progress ?? Array.Empty<ApplicationBackoffProgress>())
        {
            if (string.IsNullOrWhiteSpace(item.FamilyKey) || item.ReboundCount <= 0 ||
                !double.IsFinite(item.RemainingBlockSeconds) || item.RemainingBlockSeconds < 0 ||
                item.LongTermObservedSeconds is { } observed &&
                (!double.IsFinite(observed) || observed < 0))
            {
                continue;
            }

            var targetKey = string.IsNullOrWhiteSpace(item.TargetKey)
                ? item.FamilyKey
                : item.TargetKey;
            _states[targetKey] = new BackoffState(
                item.FamilyKey,
                item.ReboundCount,
                item.LongTermObservedSeconds is { } restoredLongTermObserved
                    ? now - TimeSpan.FromSeconds(restoredLongTermObserved)
                    : now,
                now + TimeSpan.FromSeconds(item.RemainingBlockSeconds),
                item.LongTermObservedSeconds is null
                    ? null
                    : new LongTermObservationState(
                        now - TimeSpan.FromSeconds(item.LongTermObservedSeconds.Value),
                        item.LongTermSawForeground,
                        item.LongTermRetryPermitted,
                        Math.Max(0, item.LongTermBaselineWorkingSetBytes),
                        BackgroundLowActivityStartedAt: null),
                Stage: item.BackoffStage is 1 or 2 ? item.BackoffStage : Math.Min(item.ReboundCount, 2),
                AllowForegroundIdleRetry: item.AllowForegroundIdleRetry,
                TimedSawForeground: item.TimedBackoffSawForeground,
                TimedBackgroundLowActivityStartedAt: item.TimedBackgroundLowActivitySeconds is { } seconds
                    ? now - TimeSpan.FromSeconds(seconds)
                    : null);
            if (item.LongTermObservedSeconds is not null || item.RemainingBlockSeconds > 0)
            {
                _naturalRecoveryStartedAts[targetKey] = item.LongTermObservedSeconds is { } longTermObserved
                    ? now - TimeSpan.FromSeconds(longTermObserved)
                    : now;
            }
        }
    }

    public void ClearLearning()
    {
        _learning.Clear();
        _familyStableLearning.Clear();
        _stableCandidates.Clear();
        _naturalStableWindows.Clear();
        _naturalStableReviewCompletions.Clear();
        _naturalRecoveryEligibleComponents.Clear();
        _naturalRecoveryCycleIds.Clear();
        _naturalRecoveryFamilyKeys.Clear();
        _naturalRecoveryStartedAts.Clear();
        _outcomeMultipliers.Clear();
        _learningConfidences.Clear();
        LearningRevision++;
    }

    public int RemoveLearningForFamily(string familyKey, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(familyKey)) return 0;
        var keys = _learning
            .Where(pair => string.Equals(
                pair.Value.FamilyKey,
                familyKey,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
        {
            _learning.Remove(key);
            _stableCandidates.Remove(key);
        }
        var stableKeys = _familyStableLearning
                     .Where(pair => string.Equals(pair.Value.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray();
        var componentKeys = keys
            .Concat(stableKeys.SelectMany(stableKey =>
                _familyStableLearning.GetValueOrDefault(stableKey)?.ComponentKeys ?? Array.Empty<string>()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var stableKey in stableKeys)
            _familyStableLearning.Remove(stableKey);
        var scopeKeys = _stableCandidates
            .Where(pair => string.Equals(pair.Value.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .Concat(stableKeys)
            .Concat(_naturalStableWindows
                .Where(pair => string.Equals(pair.Value.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var scopeKey in scopeKeys)
        {
            _stableCandidates.Remove(scopeKey);
            _naturalStableWindows.Remove(scopeKey);
            _naturalStableReviewCompletions.Remove(scopeKey);
        }
        foreach (var componentKey in componentKeys)
        {
            RemoveNaturalRecoveryEligibility(componentKey);
        }
        var removedCount = keys.Length + stableKeys.Length;
        if (removedCount == 0) return 0;
        LearningRevision++;
        RefreshOutcomeMultipliers(now ?? DateTimeOffset.UtcNow);
        return removedCount;
    }

    public bool ResetStableAnchorLearning(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) ||
            !_familyStableLearning.Remove(scopeKey, out var removed)) return false;

        _stableCandidates.Remove(scopeKey);
        _naturalStableWindows.Remove(scopeKey);
        _naturalStableReviewCompletions.Remove(scopeKey);
        foreach (var componentKey in removed.ComponentKeys)
            RemoveNaturalRecoveryEligibility(componentKey);
        LearningRevision++;
        return true;
    }

    public int RemoveLegacyOnlyLearning(DateTimeOffset? now = null)
    {
        var keys = _learning
            .Where(pair => pair.Value.ValidSampleCount <= 0)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
        {
            _learning.Remove(key);
            _stableCandidates.Remove(key);
        }
        if (keys.Length == 0) return 0;
        LearningRevision++;
        RefreshOutcomeMultipliers(now ?? DateTimeOffset.UtcNow);
        return keys.Length;
    }

    private static double ReboundPercent(PendingObservation pending, long currentWorkingSet)
    {
        var regained = RegainedBytes(pending, currentWorkingSet);
        return Math.Clamp(regained / (double)pending.ReleasedBytes * 100d, 0d, 100d);
    }

    private static long RegainedBytes(PendingObservation pending, long currentWorkingSet) => Math.Clamp(
        currentWorkingSet - pending.WorkingSetAfter,
        0,
        pending.ReleasedBytes);

    private static long CurrentWorkingSet(PendingObservation pending, ProcessFamilySnapshot? family)
    {
        if (family is null) return 0;
        if (pending.TargetProcessIds is null) return Math.Max(0, family.WorkingSetBytes);
        return family.Processes
            .Where(process =>
                pending.TargetProcessIds.Contains(process.ProcessId) ||
                (pending.BaselineFamilyProcessIds is not null &&
                 !pending.BaselineFamilyProcessIds.Contains(process.ProcessId) &&
                 ComponentMatches(pending, process)))
            .Sum(process => Math.Max(0, process.WorkingSetBytes));
    }

    private static bool HasForegroundProcess(PendingObservation pending, ProcessFamilySnapshot? family)
    {
        if (family is null) return false;
        if (pending.TargetProcessIds is null) return family.HasForegroundProcess;
        return family.Processes.Any(process =>
            process.IsForeground &&
            (pending.TargetProcessIds.Contains(process.ProcessId) ||
             (pending.BaselineFamilyProcessIds is not null &&
              !pending.BaselineFamilyProcessIds.Contains(process.ProcessId) &&
              ComponentMatches(pending, process))));
    }

    private static bool ComponentMatches(PendingObservation pending, ProcessSnapshot process) =>
        string.IsNullOrWhiteSpace(pending.ExecutablePath) ||
        ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var currentPath) &&
        string.Equals(currentPath, pending.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProcessSnapshot> ComponentProcesses(
        string componentKey,
        ProcessFamilySnapshot family) => ApplicationComponentIdentity.GroupProcesses(family)
        .GetValueOrDefault(componentKey) ?? Array.Empty<ProcessSnapshot>();

    private static bool IsSignificantRebound(double reboundPercent, double thresholdPercent) =>
        reboundPercent >= thresholdPercent;

    private void RecordOutcome(
        PendingObservation pending,
        long currentWorkingSet,
        double reboundPercent,
        DateTimeOffset now)
    {
        var retentionRate = 1d - Math.Clamp(reboundPercent, 0d, 100d) / 100d;
        var outcome = Math.Clamp(pending.InitialYieldRate * retentionRate, 0d, 1d);
        if (!pending.LearnOutcome || currentWorkingSet <= 0) return;

        var learningKey = pending.ComponentKey;
        var previous = _learning.GetValueOrDefault(learningKey);
        var sameLaunch = previous is not null &&
                         previous.LastLaunchObservationCount > 0 &&
                         !string.IsNullOrWhiteSpace(pending.LaunchSignature) &&
                         string.Equals(
                             previous.LastLaunchSignature,
                             pending.LaunchSignature,
                             StringComparison.Ordinal);
        var previousSamples = previous?.SampleCount ?? 0;
        var validSampleCount = previous?.ValidSampleCount ?? 0;
        var sampleCount = sameLaunch
            ? previousSamples
            : Math.Min(MaximumLearningSamples, previousSamples + 1);
        var nextValidSampleCount = sameLaunch
            ? validSampleCount
            : Math.Min(MaximumLearningSamples, validSampleCount + 1);
        var alpha = sameLaunch
            ? 0d
            : nextValidSampleCount <= 20
                ? 1d / nextValidSampleCount
                : 0.05d;
        var launchContributionWeight = sameLaunch
            ? ResolveLaunchContributionWeight(previous!, validSampleCount)
            : ContributionWeightForSampleCount(nextValidSampleCount);
        var retainedBytes = Math.Max(0, pending.ReleasedBytes - RegainedBytes(pending, currentWorkingSet));
        var reboundRate = Math.Clamp(reboundPercent / 100d, 0d, 1d);
        var quickReturnRate = pending.ReturnedToForegroundAt.HasValue ? 1d : 0d;
        var backoffRate = pending.BackoffRegistered ||
                          IsSignificantRebound(reboundPercent, pending.Settings.LateReboundPercent)
            ? 1d
            : 0d;
        var launchObservationCount = sameLaunch
            ? previous!.LastLaunchObservationCount + 1
            : 1;
        var launchAverageOutcome = sameLaunch
            ? AverageContribution(previous!.LastLaunchAverageOutcomeMultiplier, outcome, launchObservationCount)
            : outcome;
        var launchAverageReleased = sameLaunch
            ? AverageContribution(previous!.LastLaunchAverageReleasedBytes, pending.ReleasedBytes, launchObservationCount)
            : pending.ReleasedBytes;
        var launchAverageRetained = sameLaunch
            ? AverageContribution(previous!.LastLaunchAverageRetainedBytes, retainedBytes, launchObservationCount)
            : retainedBytes;
        var launchAverageLateWorkingSet = sameLaunch
            ? AverageContribution(previous!.LastLaunchAverageLateWorkingSetBytes, currentWorkingSet, launchObservationCount)
            : currentWorkingSet;
        var launchAverageRebound = sameLaunch
            ? AverageContribution(previous!.LastLaunchAverageReboundPercent, reboundPercent, launchObservationCount)
            : reboundPercent;
        var launchQuickReturnRate = sameLaunch
            ? AverageContribution(previous!.LastLaunchQuickReturnRate, quickReturnRate, launchObservationCount)
            : quickReturnRate;
        var launchBackoffRate = sameLaunch
            ? AverageContribution(previous!.LastLaunchBackoffRate, backoffRate, launchObservationCount)
            : backoffRate;
        var average = previous is null
            ? launchAverageOutcome
            : sameLaunch
                ? ReplaceLastContribution(previous.AverageOutcomeMultiplier, previous.LastLaunchAverageOutcomeMultiplier,
                    launchAverageOutcome, launchContributionWeight)
                : WeightedAverage(previous.AverageOutcomeMultiplier, launchAverageOutcome, alpha);
        var averageReleased = previous is null
            ? launchAverageReleased
            : sameLaunch
                ? ReplaceLastContribution(previous.AverageReleasedBytes, previous.LastLaunchAverageReleasedBytes,
                    launchAverageReleased, launchContributionWeight)
                : WeightedAverage(previous.AverageReleasedBytes, launchAverageReleased, alpha);
        var averageRetained = previous is null
            ? launchAverageRetained
            : sameLaunch
                ? ReplaceLastContribution(previous.AverageRetainedBytes, previous.LastLaunchAverageRetainedBytes,
                    launchAverageRetained, launchContributionWeight)
                : WeightedAverage(previous.AverageRetainedBytes, launchAverageRetained, alpha);
        var averageLateWorkingSet = previous is null
            ? launchAverageLateWorkingSet
            : sameLaunch
                ? ReplaceLastContribution(previous.AverageLateWorkingSetBytes, previous.LastLaunchAverageLateWorkingSetBytes,
                    launchAverageLateWorkingSet, launchContributionWeight)
                : WeightedAverage(previous.AverageLateWorkingSetBytes, launchAverageLateWorkingSet, alpha);
        var averageRebound = previous is null
            ? launchAverageRebound
            : sameLaunch
                ? ReplaceLastContribution(previous.AverageReboundPercent, previous.LastLaunchAverageReboundPercent,
                    launchAverageRebound, launchContributionWeight)
                : WeightedAverage(previous.AverageReboundPercent, launchAverageRebound, alpha);
        var previousBackoffRate = previous?.RecentBackoffRate ?? 0d;
        var recentBackoffRate = previous is null
            ? launchBackoffRate
            : sameLaunch
                ? ReplaceLastContribution(previousBackoffRate, previous.LastLaunchBackoffRate,
                    launchBackoffRate, launchContributionWeight)
                : WeightedAverage(previousBackoffRate, launchBackoffRate, alpha);
        var previousQuickReturnRate = previous?.RecentQuickReturnRate ?? 0d;
        var quickReturnRateOverall = previous is null
            ? launchQuickReturnRate
            : sameLaunch
                ? ReplaceLastContribution(previousQuickReturnRate, previous.LastLaunchQuickReturnRate,
                    launchQuickReturnRate, launchContributionWeight)
                : WeightedAverage(previousQuickReturnRate, launchQuickReturnRate, alpha);
        var quickReturns = Math.Clamp(
            (int)Math.Round(quickReturnRateOverall * Math.Max(1, sampleCount)),
            0,
            Math.Max(1, sampleCount));
        var launchChanged = !sameLaunch &&
                            !string.IsNullOrWhiteSpace(pending.LaunchSignature) &&
                            !string.Equals(previous?.LastLaunchSignature, pending.LaunchSignature, StringComparison.Ordinal);
        var lateWorkingSetSamples = (previous?.LateWorkingSetSamplesBytes ?? Array.Empty<long>())
            .ToList();
        if (sameLaunch && lateWorkingSetSamples.Count > 0)
            lateWorkingSetSamples[^1] = Math.Max(0, (long)Math.Round((double)launchAverageLateWorkingSet));
        else
            lateWorkingSetSamples.Add(Math.Max(0, currentWorkingSet));
        if (lateWorkingSetSamples.Count > MaximumLearningSamples)
        {
            lateWorkingSetSamples = lateWorkingSetSamples
                .TakeLast(MaximumLearningSamples)
                .ToList();
        }
        _learning[learningKey] = new ApplicationBenefitLearningRecord(
            pending.FamilyKey,
            Math.Clamp(average, 0d, 1d),
            sampleCount,
            quickReturns,
            now)
        {
            ComponentKey = pending.ComponentKey,
            ExecutablePath = pending.ExecutablePath,
            AverageReleasedBytes = RoundNonNegative(averageReleased),
            AverageRetainedBytes = RoundNonNegative(averageRetained),
            AverageLateWorkingSetBytes = RoundNonNegative(averageLateWorkingSet),
            AverageReboundPercent = averageRebound,
            BackoffTriggerCount = Math.Clamp((int)Math.Round(recentBackoffRate * nextValidSampleCount), 0, MaximumLearningSamples),
            DistinctLaunchCount = Math.Min(
                MaximumLearningSamples,
                (previous?.DistinctLaunchCount ?? 0) + (launchChanged || previous is null ? 1 : 0)),
            LastLaunchSignature = string.IsNullOrWhiteSpace(pending.LaunchSignature)
                ? previous?.LastLaunchSignature
                : pending.LaunchSignature,
            LegacySampleCount = 0,
            ValidSampleCount = nextValidSampleCount,
            RecentBackoffRate = Math.Clamp(recentBackoffRate, 0d, 1d),
            RecentQuickReturnRate = Math.Clamp(quickReturnRateOverall, 0d, 1d),
            LateWorkingSetSamplesBytes = lateWorkingSetSamples,
            LastLaunchObservationCount = launchObservationCount,
            LastLaunchContributionWeight = launchContributionWeight,
            LastLaunchAverageOutcomeMultiplier = launchAverageOutcome,
            LastLaunchAverageReleasedBytes = launchAverageReleased,
            LastLaunchAverageRetainedBytes = launchAverageRetained,
            LastLaunchAverageLateWorkingSetBytes = launchAverageLateWorkingSet,
            LastLaunchAverageReboundPercent = launchAverageRebound,
            LastLaunchQuickReturnRate = launchQuickReturnRate,
            LastLaunchBackoffRate = launchBackoffRate,
            StableWorkingSetSamplesBytes = Array.Empty<long>(),
            StableLastObservedAt = null,
            LastStableLaunchSignature = null
        };
        LearningRevision++;
        RefreshOutcomeMultipliers(now);
    }

    private void RefreshOutcomeMultipliers(DateTimeOffset now)
    {
        _outcomeMultipliers.Clear();
        _learningConfidences.Clear();
        var components = new List<(ApplicationBenefitLearningRecord Record, double Multiplier, double Confidence)>();
        foreach (var pair in _learning.ToArray())
        {
            var record = pair.Value;
            var age = now - record.LastObservedAt;
            if (age >= LearningExpiresAfter)
            {
                _learning.Remove(pair.Key);
                _stableCandidates.Remove(pair.Key);
                continue;
            }

            var sampleConfidence = record.ValidSampleCount switch
            {
                0 => 0d,
                1 => 0.1d,
                2 => 0.2d,
                3 => 0.4d,
                4 => 0.6d,
                5 => 0.8d,
                _ => 1d
            };
            var freshness = age <= LearningDecayStartsAfter
                ? 1d
                : 1d - (age - LearningDecayStartsAfter).TotalDays /
                    (LearningExpiresAfter - LearningDecayStartsAfter).TotalDays;
            var confidence = sampleConfidence * Math.Clamp(freshness, 0d, 1d);
            var quickReturnRate = Math.Clamp(record.RecentQuickReturnRate, 0d, 1d);
            var habitMultiplier = 1d - 0.4d * quickReturnRate * confidence;
            components.Add((
                record,
                Math.Clamp(
                    1d + (record.AverageOutcomeMultiplier * habitMultiplier - 1d) * confidence,
                    0d,
                    1d),
                confidence));
        }
        foreach (var family in components.GroupBy(
                     component => component.Record.FamilyKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            var totalWeight = family.Sum(component => Math.Max(1, component.Record.ValidSampleCount));
            _outcomeMultipliers[family.Key] = family.Sum(component =>
                component.Multiplier * Math.Max(1, component.Record.ValidSampleCount)) / totalWeight;
            _learningConfidences[family.Key] = family.Max(component => component.Confidence);
        }
    }

    private static double WeightedAverage(double previous, double current, double alpha) =>
        previous + alpha * (current - previous);

    private static double AverageContribution(double previous, double current, int count) =>
        count <= 1 ? current : previous + (current - previous) / count;

    private static double ReplaceLastContribution(
        double overall,
        double previousContribution,
        double currentContribution,
        double contributionWeight) =>
        overall + contributionWeight * (currentContribution - previousContribution);

    private static double ResolveLaunchContributionWeight(
        ApplicationBenefitLearningRecord previous,
        int validSampleCount) =>
        previous.LastLaunchContributionWeight > 0d &&
        double.IsFinite(previous.LastLaunchContributionWeight)
            ? Math.Clamp(previous.LastLaunchContributionWeight, 0d, 1d)
            : ContributionWeightForSampleCount(validSampleCount);

    private static double ContributionWeightForSampleCount(int validSampleCount) =>
        validSampleCount <= 0
            ? 0d
            : validSampleCount <= 20
                ? 1d / validSampleCount
                : 0.05d;

    private static long RoundNonNegative(double value) =>
        Math.Max(0, (long)Math.Round(value));

    private sealed record NaturalStableWindow(
        string FamilyKey,
        string LaunchSignature,
        DateTimeOffset StartedAt,
        DateTimeOffset Deadline,
        IReadOnlyList<string> ComponentKeys,
        long MinimumBytes,
        long MaximumBytes,
        long LatestBytes,
        int ObservationCount,
        IReadOnlyList<TimedWorkingSetSample> WorkingSetSamples,
        TimeSpan StableDuration,
        TimeSpan TotalObservationDuration,
        DateTimeOffset LastObservedAt,
        bool AllowsNewBaseline,
        bool PreserveConvergedStatus,
        NaturalStableObservationOrigin Origin)
    {
        public NaturalStableValidation? Validation { get; init; }
        public NaturalStableGrowthReview? GrowthReview { get; init; }
        public bool RequiresFirstBootAnchorGate { get; init; }
    }

    private sealed record TimedWorkingSetSample(
        DateTimeOffset ObservedAt,
        long WorkingSetBytes,
        bool IsLowActivity);

    private sealed record StableSampleEstimate(long MinimumBytes, long CenterBytes, long MaximumBytes);

    private sealed record NaturalStableReviewCompletion(
        string LaunchSignature,
        DateTimeOffset CompletedAt);

    private sealed record HistoricalReviewSession(
        string LaunchSignature,
        int CompletedReviewCount);

    private sealed record NaturalStableGrowthReview(
        string FamilyScopeKey,
        IReadOnlyList<string> FamilyScopeComponentKeys,
        string FamilyScopeLaunchSignature,
        DateTimeOffset StartedAt,
        long BaselineFamilyWorkingSetBytes,
        long LatestFamilyWorkingSetBytes,
        long RequiredIncreaseBytes,
        DateTimeOffset LastObservedAt);

    private sealed record NaturalStableValidation(
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

    private sealed record ActiveBackoffObservationContext(
        bool IsLongTerm,
        long BaselineWorkingSetBytes);

    private static DateTimeOffset BackoffStableObservationDeadline(BackoffState state) =>
        state.LongTerm is null
            ? state.BlockedUntil
            : state.StartedAt + LongTermBackoffStableObservationWindow;

    private static string LearningKey(ApplicationBenefitLearningRecord record) =>
        string.IsNullOrWhiteSpace(record.ComponentKey) ? record.FamilyKey : record.ComponentKey;

    private static string ResolveLaunchSignature(
        string? launchSignature,
        IReadOnlyCollection<int>? targetProcessIds,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(launchSignature))
            return launchSignature.Trim();

        // A PID list is only a session snapshot; PID reuse cannot prove that two observations
        // belong to the same launch. Callers must provide a complete launch signature when
        // same-launch replacement is intended.
        return $"observation:{now.UtcTicks}:{Guid.NewGuid():N}";
    }

    private static bool IsValid(ApplicationBenefitLearningRecord record) =>
        !string.IsNullOrWhiteSpace(record.FamilyKey) &&
        double.IsFinite(record.AverageOutcomeMultiplier) &&
        record.SampleCount > 0 &&
        record.LastObservedAt != default;

    private void Register(
        string targetKey,
        string familyKey,
        ReboundBackoffSettings settings,
        long currentWorkingSetBytes,
        DateTimeOffset now)
    {
        if (!settings.Enabled) return;
        foreach (var scopeKey in _naturalStableWindows
                     .Where(pair => pair.Value.ComponentKeys.Contains(
                         targetKey,
                         StringComparer.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _naturalStableWindows.Remove(scopeKey);
            _stableCandidates.Remove(scopeKey);
        }
        var previous = _states.GetValueOrDefault(targetKey);
        var count = previous?.Count + 1 ?? 1;
        var stage = settings.CycleAfterSecondBackoff
            ? previous?.Stage == 1 ? 2 : 1
            : Math.Min(count, 3);
        _states[targetKey] = stage switch
        {
            1 => new BackoffState(familyKey, count, now, now + settings.FirstBackoff, null,
                Stage: 1),
            2 => new BackoffState(familyKey, count, now, now + settings.SecondBackoff, null,
                Stage: 2,
                AllowForegroundIdleRetry: settings.AllowSecondBackoffForegroundIdleRetry),
            _ => new BackoffState(
                familyKey,
                count,
                now,
                DateTimeOffset.MinValue,
                new LongTermObservationState(
                    now,
                    SawForeground: false,
                    RetryPermitted: false,
                    Math.Max(0, currentWorkingSetBytes),
                    BackgroundLowActivityStartedAt: null),
                Stage: 3)
        };
        _naturalRecoveryStartedAts[targetKey] = now;
        _naturalRecoveryCycleIds[targetKey] = $"backoff:{now.UtcTicks}:{targetKey}";
        _naturalRecoveryFamilyKeys[targetKey] = familyKey;
    }

    private void UpdateLongTermBaseline(string targetKey, long currentWorkingSetBytes)
    {
        if (!_states.TryGetValue(targetKey, out var state) || state.LongTerm is null) return;
        _states[targetKey] = state with
        {
            LongTerm = state.LongTerm with
            {
                BaselineWorkingSetBytes = Math.Max(0, currentWorkingSetBytes)
            }
        };
    }

    private void DowngradeAfterSuccessfulRetry(string familyKey)
    {
        if (!_states.TryGetValue(familyKey, out var state)) return;
        _states[familyKey] = new BackoffState(
            state.FamilyKey,
            Math.Max(0, state.Count - 1),
            state.StartedAt,
            DateTimeOffset.MinValue,
            LongTerm: null);
    }

    private sealed record PendingObservation(
        string FamilyKey,
        string ComponentKey,
        string? ExecutablePath,
        long WorkingSetAfter,
        long ReleasedBytes,
        ReboundBackoffSettings Settings,
        DateTimeOffset StartedAt,
        double InitialYieldRate,
        IReadOnlySet<int>? TargetProcessIds,
        IReadOnlySet<int>? BaselineFamilyProcessIds,
        bool LearnOutcome,
        bool WasForegroundBeforeTrim,
        DateTimeOffset? ReturnedToForegroundAt,
        bool EarlyChecked,
        bool BackoffRegistered,
        bool RecoveryAttempt,
        OptimizationRunContext? RunContext,
        string? LaunchSignature,
        NaturalStableObservationOrigin RecoveryOrigin);

    private sealed record BackoffState(
        string FamilyKey,
        int Count,
        DateTimeOffset StartedAt,
        DateTimeOffset BlockedUntil,
        LongTermObservationState? LongTerm,
        int Stage = 0,
        bool AllowForegroundIdleRetry = false,
        bool TimedSawForeground = false,
        DateTimeOffset? TimedBackgroundLowActivityStartedAt = null);

    private sealed record LongTermObservationState(
        DateTimeOffset StartedAt,
        bool SawForeground,
        bool RetryPermitted,
        long BaselineWorkingSetBytes,
        DateTimeOffset? BackgroundLowActivityStartedAt);
}
