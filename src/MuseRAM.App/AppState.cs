using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MuseRAM.Core;

namespace MuseRAM.App;

public sealed class AppState : INotifyPropertyChanged
{
    private string _status = "正在建立可靠的进程活动样本...";
    private string _memoryUsage = "--";
    private double _memoryLoadPercent;
    private string _availableMemory = "--";
    private string _usedMemory = "--";
    private string _physicalMemorySummary = "-- / --";
    private double _commitLoadPercent;
    private string _committedMemorySummary = "-- / --";
    private string _memoryChange = "--";
    private string _recentTrim = "+0 B";
    private string _cumulativeTrim = "+0 B";
    private string _boostNetGain = "+0 B";
    private string _cumulativeNetGain = "+0 B";
    private string _lastUpdated = "--";
    private string _sessionUptime = "--";
    private string _lastResult = "尚未运行";
    private string _autoStatus = "已关闭";
    private string _reboundRate = "回弹率：--";
    private string _reboundSummary = "暂无应用明细";
    private string _candidateSorting = "当前排序：综合优先级";
    private string _benefitLearningStatus = "等待自动优化样本";
    private string _selfOverhead = "正在采样...";
    private bool _isBusy;
    private bool _hasReboundDetails;

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ObservableCollection<ProcessRow> Candidates { get; } = new();
    public ObservableCollection<ProtectedApplicationGroup> ProtectedApplications { get; } = new();
    public ObservableCollection<ApplicationRuleRow> ApplicationRules { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public ObservableCollection<ReboundHistoryRunRow> ReboundRuns { get; } = new();
    public ObservableCollection<ApplicationReboundDetailRow> ReboundDetails { get; } = new();
    public ObservableCollection<BenefitLearningRow> BenefitLearningRows { get; } = new();

    public string Status { get => _status; set => Set(ref _status, value); }
    public string MemoryUsage { get => _memoryUsage; set => Set(ref _memoryUsage, value); }
    public double MemoryLoadPercent { get => _memoryLoadPercent; set => Set(ref _memoryLoadPercent, value); }
    public string AvailableMemory { get => _availableMemory; set => Set(ref _availableMemory, value); }
    public string UsedMemory { get => _usedMemory; set => Set(ref _usedMemory, value); }
    public string PhysicalMemorySummary { get => _physicalMemorySummary; set => Set(ref _physicalMemorySummary, value); }
    public double CommitLoadPercent { get => _commitLoadPercent; set => Set(ref _commitLoadPercent, value); }
    public string CommittedMemorySummary { get => _committedMemorySummary; set => Set(ref _committedMemorySummary, value); }
    public string MemoryChange { get => _memoryChange; set => Set(ref _memoryChange, value); }
    public string RecentTrim { get => _recentTrim; set => Set(ref _recentTrim, value); }
    public string CumulativeTrim { get => _cumulativeTrim; set => Set(ref _cumulativeTrim, value); }
    public string BoostNetGain { get => _boostNetGain; set => Set(ref _boostNetGain, value); }
    public string CumulativeNetGain { get => _cumulativeNetGain; set => Set(ref _cumulativeNetGain, value); }
    public string LastUpdated { get => _lastUpdated; set => Set(ref _lastUpdated, value); }
    public string SessionUptime { get => _sessionUptime; set => Set(ref _sessionUptime, value); }
    public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }
    public string AutoStatus { get => _autoStatus; set => Set(ref _autoStatus, value); }
    public string ReboundRate { get => _reboundRate; set => Set(ref _reboundRate, value); }
    public string ReboundSummary { get => _reboundSummary; set => Set(ref _reboundSummary, value); }
    public string CandidateSorting { get => _candidateSorting; set => Set(ref _candidateSorting, value); }
    public string BenefitLearningStatus { get => _benefitLearningStatus; set => Set(ref _benefitLearningStatus, value); }
    public string SelfOverhead { get => _selfOverhead; set => Set(ref _selfOverhead, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public bool HasReboundDetails { get => _hasReboundDetails; set => Set(ref _hasReboundDetails, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddHistory(string message)
    {
        History.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ApplicationRuleRow : INotifyPropertyChanged
{
    private bool _isEnabled;

    public ApplicationRuleRow(
        string id,
        string targetSummary,
        string triggerSummary,
        string nextCheck,
        string lastExecution,
        string lastSkip,
        bool isEnabled)
    {
        Id = id;
        TargetSummary = targetSummary;
        TriggerSummary = triggerSummary;
        NextCheck = nextCheck;
        LastExecution = lastExecution;
        LastSkip = lastSkip;
        _isEnabled = isEnabled;
    }

    public string Id { get; }
    public string TargetSummary { get; }
    public string TriggerSummary { get; }
    public string NextCheck { get; }
    public string LastExecution { get; }
    public string LastSkip { get; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ProcessRetentionIndicator
{
    None,
    EntireFamilyProtection,
    PartialProtection,
    SessionStableState,
    LongTermStableState,
    NaturalStableObservation,
    NaturalStableReview,
    NaturalStableGrowthReview,
    BenefitObservation,
    BenefitObservationWithHistoricalStable,
    AutomaticBackoff,
    Foreground,
    IoActivity,
    CpuActivity,
    Sampling,
    Cooldown,
    VisibleWindowWait,
    BelowWorkingSetThreshold,
    RelationshipActivity,
    BelowIdleScore,
    CandidateReady
}

public static class ProcessRetentionPresentation
{
    public static ProcessRetentionIndicator Resolve(
        bool isProtected,
        bool isPartiallyProtected,
        IReadOnlyCollection<CandidateExclusionReason>? exclusionReasons,
        bool naturalStableObservation = false,
        bool hasLongTermStableReference = false,
        bool isEligible = false,
        bool hasProcessableTargets = false,
        bool naturalStableReview = false,
        bool naturalStableGrowthReview = false,
        bool naturalStableProvisionalValidation = false,
        bool? reboundObservationPending = null)
    {
        if (isProtected) return ProcessRetentionIndicator.EntireFamilyProtection;
        var lifecycle = ResolveLifecycle(
            exclusionReasons,
            naturalStableObservation,
            hasLongTermStableReference,
            isEligible,
            hasProcessableTargets,
            naturalStableReview,
            naturalStableGrowthReview,
            naturalStableProvisionalValidation,
            reboundObservationPending);
        return lifecycle != ProcessRetentionIndicator.None
            ? lifecycle
            : isPartiallyProtected
                ? ProcessRetentionIndicator.PartialProtection
                : ProcessRetentionIndicator.None;
    }

    public static ProcessRetentionIndicator ResolveLifecycle(
        IReadOnlyCollection<CandidateExclusionReason>? exclusionReasons,
        bool naturalStableObservation = false,
        bool hasLongTermStableReference = false,
        bool isEligible = false,
        bool hasProcessableTargets = false,
        bool naturalStableReview = false,
        bool naturalStableGrowthReview = false,
        bool naturalStableProvisionalValidation = false,
        bool? reboundObservationPending = null)
    {
        if (naturalStableGrowthReview) return ProcessRetentionIndicator.NaturalStableGrowthReview;
        if (reboundObservationPending ??
            exclusionReasons?.Contains(CandidateExclusionReason.ReboundObservationPending) == true)
            return hasLongTermStableReference
                ? ProcessRetentionIndicator.BenefitObservationWithHistoricalStable
                : ProcessRetentionIndicator.BenefitObservation;
        if (naturalStableReview) return ProcessRetentionIndicator.NaturalStableReview;
        if (naturalStableObservation) return ProcessRetentionIndicator.NaturalStableObservation;
        if (hasLongTermStableReference)
            return ProcessRetentionIndicator.LongTermStableState;
        if (naturalStableProvisionalValidation) return ProcessRetentionIndicator.SessionStableState;
        if (exclusionReasons is null) return ProcessRetentionIndicator.None;
        if (exclusionReasons.Contains(CandidateExclusionReason.StableStateSuppression))
            return hasLongTermStableReference
                ? ProcessRetentionIndicator.LongTermStableState
                : ProcessRetentionIndicator.SessionStableState;
        if (exclusionReasons.Contains(CandidateExclusionReason.AutomaticBackoff))
            return ProcessRetentionIndicator.AutomaticBackoff;
        if (exclusionReasons.Contains(CandidateExclusionReason.BelowFamilyWorkingSet) ||
            (!hasProcessableTargets &&
             exclusionReasons.Contains(CandidateExclusionReason.BelowProcessWorkingSet)))
            return ProcessRetentionIndicator.BelowWorkingSetThreshold;
        if (exclusionReasons.Contains(CandidateExclusionReason.Foreground))
            return ProcessRetentionIndicator.Foreground;
        if (exclusionReasons.Contains(CandidateExclusionReason.CurrentIoActivity))
            return ProcessRetentionIndicator.IoActivity;
        if (exclusionReasons.Contains(CandidateExclusionReason.CurrentCpuActivity))
            return ProcessRetentionIndicator.CpuActivity;
        if (exclusionReasons.Contains(CandidateExclusionReason.UnreliableActivitySample) ||
            exclusionReasons.Contains(CandidateExclusionReason.IdleConfirmationPending))
            return ProcessRetentionIndicator.Sampling;
        if (exclusionReasons.Contains(CandidateExclusionReason.ProcessCooldown))
            return ProcessRetentionIndicator.Cooldown;
        if (exclusionReasons.Contains(CandidateExclusionReason.VisibleWindowWait))
            return ProcessRetentionIndicator.VisibleWindowWait;
        if (exclusionReasons.Contains(CandidateExclusionReason.ActiveProcessRelationship) ||
            exclusionReasons.Contains(CandidateExclusionReason.GamingProtection))
            return ProcessRetentionIndicator.RelationshipActivity;
        if (exclusionReasons.Contains(CandidateExclusionReason.BelowIdleScore))
            return ProcessRetentionIndicator.BelowIdleScore;
        return isEligible ? ProcessRetentionIndicator.CandidateReady : ProcessRetentionIndicator.None;
    }
}

public enum RetentionStatusIcon
{
    None,
    Protected,
    PartiallyProtected,
    Stable,
    SessionStable,
    Review,
    GrowthReview,
    Observing,
    ActivityObserving,
    StableObserving,
    Idle,
    Waiting,
    Threshold,
    Candidate,
    Backoff,
    Activity
}

public sealed record ProcessRow(
    int ProcessId,
    string Name,
    string Memory,
    string? MemoryDetail,
    long MemoryBytes,
    string IdleScore,
    string? IdleScoreDetail,
    string IdleStatus,
    string? IdleStatusDetail,
    string Protection,
    string? ProtectionDetail,
    RetentionStatusIcon RetentionIcon,
    string AutoOptimizationStatus,
    string Ranking,
    string? Path,
    ProcessFamilySnapshot Family)
{
    public bool HasRetentionIcon => RetentionIcon != RetentionStatusIcon.None;
}

public sealed record ApplicationReboundDetailRow(
    string Application,
    string InitialTrim,
    string Regained,
    string ReboundRate,
    string Status);

public sealed record ReboundHistoryRunRow(
    int Sequence,
    string PrimaryText,
    string SecondaryText);

public sealed record BenefitLearningRow(
    string FamilyKey,
    string ScopeKey,
    string Application,
    string StableScopeDescription,
    string PostOptimizationWorkingSet,
    string StableAnchor,
    string StableUpperLimit,
    string StableAnchorSummaryHelp,
    string StableAnchorSettingsHelp,
    string StableTrendGlyph,
    string StableTrendHelp,
    bool IsFixedAnchor,
    bool CanConfigureAnchor,
    bool HasAnchorReferenceRange,
    double AdaptiveAnchorMiB,
    double FixedAnchorMiB,
    double AnchorMinimumMiB,
    double AnchorMaximumMiB,
    long AnchorMinimumBytes,
    long AnchorMaximumBytes,
    string SustainedRelease,
    double WorkingSetPercent,
    double SustainedReleasePercent,
    string AverageRebound,
    string BenefitSamples,
    string StableSamples,
    string StableSamplesHelp,
    string LastObserved,
    string Suggestion)
{
    public long CurrentWorkingSetBytes { get; init; }
}

public sealed class ProtectedApplicationGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public ProtectedApplicationGroup(
        string key,
        string familyKey,
        string name,
        string path,
        ApplicationProtectionState protectionState,
        IReadOnlyList<string> ruleApplicationPaths,
        IReadOnlyList<ProtectedExecutableEntry> executables,
        int instanceCount,
        long workingSetBytes,
        bool isExpanded,
        bool hasApplicationRule,
        string applicationRuleStatus,
        string applicationRuleHistory,
        string applicationRuleSkip,
        string applicationRuleDetail)
    {
        Key = key;
        FamilyKey = familyKey;
        Name = name;
        Path = path;
        ProtectionState = protectionState;
        RuleApplicationPaths = ruleApplicationPaths;
        Executables = executables;
        InstanceCount = instanceCount;
        WorkingSetBytes = workingSetBytes;
        HasApplicationRule = hasApplicationRule;
        ApplicationRuleStatus = applicationRuleStatus;
        ApplicationRuleHistory = applicationRuleHistory;
        ApplicationRuleSkip = applicationRuleSkip;
        ApplicationRuleDetail = applicationRuleDetail;
        _isExpanded = isExpanded;
    }

    public string Key { get; }
    public string FamilyKey { get; }
    public string Name { get; }
    public string Path { get; }
    public ApplicationProtectionState ProtectionState { get; }
    public IReadOnlyList<string> RuleApplicationPaths { get; }
    public IReadOnlyList<ProtectedExecutableEntry> Executables { get; }
    public int InstanceCount { get; }
    public int ProtectedInstanceCount => Executables.Sum(executable => executable.InstanceCount);
    public long WorkingSetBytes { get; }
    public long ProtectedWorkingSetBytes => Executables.Sum(executable => executable.WorkingSetBytes);
    public bool HasApplicationRule { get; }
    public string ApplicationRuleStatus { get; }
    public string ApplicationRuleHistory { get; }
    public string ApplicationRuleSkip { get; }
    public string ApplicationRuleDetail { get; }
    public bool IsRunning => InstanceCount > 0;
    public string Memory => !IsRunning
        ? "--"
        : ProtectionState == ApplicationProtectionState.Partial
            ? $"{DisplayFormat.Bytes(WorkingSetBytes)} / {DisplayFormat.Bytes(ProtectedWorkingSetBytes)}"
            : DisplayFormat.Bytes(WorkingSetBytes);
    public int ExecutableCount => Executables.Count;
    public bool HasExecutables => Executables.Count > 0;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProtectedExecutableEntry : INotifyPropertyChanged
{
    private bool _isExpanded;

    public ProtectedExecutableEntry(
        string familyKey,
        string name,
        string path,
        int instanceCount,
        long workingSetBytes,
        IReadOnlyList<ProtectedProcessEntry> processes,
        bool isExpanded)
    {
        FamilyKey = familyKey;
        Name = name;
        Path = path;
        InstanceCount = instanceCount;
        WorkingSetBytes = workingSetBytes;
        Processes = processes;
        _isExpanded = isExpanded;
    }

    public string FamilyKey { get; }
    public string Name { get; }
    public string Path { get; }
    public int InstanceCount { get; }
    public long WorkingSetBytes { get; }
    public bool IsRunning => InstanceCount > 0;
    public IReadOnlyList<ProtectedProcessEntry> Processes { get; }
    public string Memory => DisplayFormat.Bytes(WorkingSetBytes);
    public bool HasProcesses => Processes.Count > 0;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ProtectedProcessEntry(int ProcessId, long WorkingSetBytes, string? Status = null)
{
    public string Label => string.IsNullOrWhiteSpace(Status)
        ? $"PID {ProcessId}"
        : $"PID {ProcessId} · {Status}";
    public string Memory => DisplayFormat.Bytes(WorkingSetBytes);
}

public sealed record ProtectedOptimizationTarget(
    string FamilyKey,
    string DisplayName,
    IReadOnlyList<string> ExecutablePaths);

internal static class DisplayFormat
{
    public static string Bytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        return value >= 1024L * 1024 * 1024
            ? $"{value / (1024d * 1024 * 1024):0.0} GB"
            : $"{value / (1024d * 1024):0} MB";
    }

    public static string Bytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024d * 1024):0} MB";

    public static string BinaryThreshold(long bytes)
    {
        var value = Math.Max(0, bytes);
        return value >= 1024L * 1024 * 1024
            ? $"{value / (1024d * 1024 * 1024):0.0} GiB"
            : $"{value / (1024d * 1024):0} MiB";
    }
}
