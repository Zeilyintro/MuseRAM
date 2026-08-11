namespace MuseRAM.Core;

public enum ApplicationOptimizationTargetType
{
    ApplicationFamily,
    Executable,
    ExecutableGroup
}

public enum ApplicationOptimizationDelayAnchor
{
    MuseRamStartup,
    TargetApplicationStartup
}

public enum ApplicationOptimizationRuleTriggerMode
{
    Delayed,
    FollowAutomatic
}

public sealed class ApplicationOptimizationRuleTarget
{
    public ApplicationOptimizationTargetType TargetType { get; set; }
    public string Path { get; set; } = string.Empty;
    public List<string> ExecutablePaths { get; set; } = new();
    // Nullable preserves the distinction between a legacy target (missing the field) and a
    // newly added target that has not received explicit authorization.
    public bool? BypassProtectionConfirmed { get; set; }
}

public sealed class ApplicationOptimizationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public List<ApplicationOptimizationRuleTarget> Targets { get; set; } = new();
    public ApplicationOptimizationRuleTriggerMode TriggerMode { get; set; } =
        ApplicationOptimizationRuleTriggerMode.Delayed;
    public bool DelayTriggerEnabled { get; set; }
    public ApplicationOptimizationDelayAnchor DelayAnchor { get; set; } =
        ApplicationOptimizationDelayAnchor.TargetApplicationStartup;
    public int DelayMinutes { get; set; } = 30;
    public int ExecutionCount { get; set; } = 1;
    public int ExecutionIntervalMinutes { get; set; } = 30;
    public bool RepeatIndefinitely { get; set; }
    public bool RestartWithApplication { get; set; } = true;
    public bool WorkingSetTriggerEnabled { get; set; }
    // Null preserves the fixed threshold semantics of rules saved before profile following existed.
    public bool? WorkingSetThresholdFollowsProfile { get; set; }
    public long WorkingSetThresholdBytes { get; set; } = 512L * 1024 * 1024;
    public int CooldownMinutes { get; set; } = 10;
    public int ConfigurationRevision { get; set; } = 1;
    // Retained for settings written by the first implementation. Runtime decisions use the target flag.
    public bool BypassProtection { get; set; }
}

public sealed record ApplicationOptimizationRuleTargetMatch(
    string RuleId,
    ApplicationOptimizationRuleTarget Target,
    ProcessFamilySnapshot Family,
    IReadOnlyList<ProcessSnapshot> Processes);

public sealed record ApplicationOptimizationRuleTargetDecision(
    string TargetIdentity,
    ApplicationOptimizationRuleTarget Target,
    IReadOnlyList<ApplicationOptimizationRuleTargetMatch> Matches,
    bool DelayDue,
    IReadOnlyList<ProcessSnapshot> WorkingSetDueProcesses,
    bool WorkingSetThresholdSatisfied)
{
    public bool IsDue => DelayDue && WorkingSetThresholdSatisfied;
}

public sealed record AutomaticOptimizationThresholdOverride(
    string TargetIdentity,
    string FamilyKey,
    IReadOnlySet<int> ProcessIds,
    long ThresholdBytes)
{
    public bool BypassProtection { get; init; }
}

public sealed class ApplicationOptimizationRuleProcessRuntimeState
{
    public int ConsecutiveReliableWorkingSetSamples { get; internal set; }
    public DateTimeOffset? LastSuccessfulTrimAt { get; internal set; }
    public DateTimeOffset? LastWorkingSetExecutionAt { get; internal set; }
}

public sealed class ApplicationOptimizationRuleTargetRuntimeState
{
    public string TargetIdentity { get; internal set; } = string.Empty;
    public int ConfigurationRevision { get; internal set; }
    public string ConfigurationKey { get; internal set; } = string.Empty;
    public string? CurrentLaunchSignature { get; internal set; }
    public int DelayExecutionsCompleted { get; internal set; }
    public DateTimeOffset? LastDelayExecutionAt { get; internal set; }
    public DateTimeOffset? LastExecutionStartedAt { get; internal set; }
    public long LastReleasedBytes { get; internal set; }
    public long? LastRetainedBytes { get; internal set; }
    public string? LastSkippedReason { get; internal set; }
    public long? LastObservedWorkingSetThresholdBytes { get; internal set; }
    public Dictionary<string, ApplicationOptimizationRuleProcessRuntimeState> ProcessStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ApplicationOptimizationRuleProcessProgress(
    string ProcessIdentity,
    double? LastSuccessfulTrimElapsedSeconds,
    double? LastWorkingSetExecutionElapsedSeconds);

public sealed record ApplicationOptimizationRuleTargetProgress(
    string RuleId,
    string TargetIdentity,
    int ConfigurationRevision,
    string ConfigurationKey,
    string LaunchSignature,
    int DelayExecutionsCompleted,
    double? LastDelayExecutionElapsedSeconds,
    double? LastExecutionStartedElapsedSeconds,
    long LastReleasedBytes,
    long? LastRetainedBytes,
    string? LastSkippedReason,
    IReadOnlyList<ApplicationOptimizationRuleProcessProgress> Processes);

public sealed class ApplicationOptimizationRuleRuntime
{
    private readonly Dictionary<string, ApplicationOptimizationRuleTargetRuntimeState> _targetStates =
        new(StringComparer.OrdinalIgnoreCase);

    public ApplicationOptimizationRuleTargetRuntimeState GetTargetState(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTarget target)
    {
        var targetIdentity = ApplicationOptimizationRulePolicy.TargetIdentity(target);
        return GetTargetState(rule, targetIdentity);
    }

    public IReadOnlyList<ApplicationOptimizationRuleTargetDecision> GetTargetDecisions(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset museRamStartedAt,
        DateTimeOffset now)
    {
        var matches = ApplicationOptimizationRulePolicy.ResolveMatches(rule, families);
        var decisions = new List<ApplicationOptimizationRuleTargetDecision>();
        foreach (var targetGroup in matches.GroupBy(
                     match => ApplicationOptimizationRulePolicy.TargetIdentity(match.Target),
                     StringComparer.OrdinalIgnoreCase))
        {
            var target = targetGroup.First().Target;
            var targetMatches = targetGroup.ToArray();
            var state = GetTargetState(rule, target);
            var delayDue = IsDelayDue(rule, target, targetMatches, museRamStartedAt, now, state);
            var workingSetDueProcesses = rule.WorkingSetTriggerEnabled
                ? targetMatches
                    .SelectMany(match => match.Processes)
                    .GroupBy(ProcessIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Where(process => IsWorkingSetReady(rule, target, process))
                    .ToArray()
                : Array.Empty<ProcessSnapshot>();
            decisions.Add(new ApplicationOptimizationRuleTargetDecision(
                targetGroup.Key,
                target,
                targetMatches,
                delayDue,
                workingSetDueProcesses,
                !rule.WorkingSetTriggerEnabled || workingSetDueProcesses.Length > 0));
        }
        return decisions;
    }

    public void ObserveWorkingSet(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ProcessFamilySnapshot> families,
        long? profileWorkingSetThresholdBytes = null)
    {
        var matchesByTarget = ApplicationOptimizationRulePolicy.ResolveMatches(rule, families)
            .GroupBy(match => ApplicationOptimizationRulePolicy.TargetIdentity(match.Target),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var target in rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
        {
            var targetIdentity = ApplicationOptimizationRulePolicy.TargetIdentity(target);
            var state = GetTargetState(rule, target);
            var matches = matchesByTarget.GetValueOrDefault(targetIdentity) ?? Array.Empty<ApplicationOptimizationRuleTargetMatch>();
            var currentProcesses = matches
                .SelectMany(match => match.Processes)
                .GroupBy(ProcessIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var thresholdBytes = rule.WorkingSetThresholdFollowsProfile == true &&
                                 profileWorkingSetThresholdBytes is > 0
                ? profileWorkingSetThresholdBytes.Value
                : Math.Max(1L.MiB(), rule.WorkingSetThresholdBytes);
            if (state.LastObservedWorkingSetThresholdBytes != thresholdBytes)
            {
                foreach (var processState in state.ProcessStates.Values)
                    processState.ConsecutiveReliableWorkingSetSamples = 0;
                state.LastObservedWorkingSetThresholdBytes = thresholdBytes;
            }
            var reliableWorkingSetBytes = currentProcesses
                .Where(process => process.HasReliableActivitySample)
                .Sum(process => Math.Max(0, process.WorkingSetBytes));
            var targetMeetsWorkingSetThreshold = currentProcesses.Length > 0 &&
                                                 reliableWorkingSetBytes >= thresholdBytes;
            var launchSignature = LaunchSignature(target, matches);
            if (state.CurrentLaunchSignature is not null &&
                !string.Equals(state.CurrentLaunchSignature, launchSignature, StringComparison.Ordinal) &&
                rule.RestartWithApplication &&
                rule.DelayTriggerEnabled &&
                rule.DelayAnchor == ApplicationOptimizationDelayAnchor.TargetApplicationStartup)
            {
                state.DelayExecutionsCompleted = 0;
                state.LastDelayExecutionAt = null;
                state.LastExecutionStartedAt = null;
                state.ProcessStates.Clear();
            }
            state.CurrentLaunchSignature = launchSignature;
            var currentIdentities = currentProcesses
                .Where(process => process.StartTimeFileTimeUtc is > 0)
                .Select(ProcessIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var process in currentProcesses)
            {
                if (process.StartTimeFileTimeUtc is not > 0) continue;
                var processState = GetProcessState(state, process);
                if (!rule.WorkingSetTriggerEnabled || !process.HasReliableActivitySample)
                {
                    processState.ConsecutiveReliableWorkingSetSamples = 0;
                    continue;
                }
                processState.ConsecutiveReliableWorkingSetSamples = targetMeetsWorkingSetThreshold
                    ? processState.ConsecutiveReliableWorkingSetSamples + 1
                    : 0;
            }
            foreach (var processIdentity in state.ProcessStates.Keys
                         .Where(identity => !currentIdentities.Contains(identity))
                         .ToArray())
            {
                state.ProcessStates.Remove(processIdentity);
            }
        }
    }

    public bool IsWorkingSetReady(ApplicationOptimizationRule rule, ProcessSnapshot process) =>
        process.StartTimeFileTimeUtc is > 0 &&
        (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Select(target => GetTargetState(rule, target))
            .SelectMany(state => state.ProcessStates)
            .Any(pair => string.Equals(pair.Key, ProcessIdentity(process), StringComparison.OrdinalIgnoreCase) &&
                         pair.Value.ConsecutiveReliableWorkingSetSamples >= 2);

    public bool IsWorkingSetReady(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTarget target,
        ProcessSnapshot process) =>
        process.StartTimeFileTimeUtc is > 0 &&
        GetTargetState(rule, target).ProcessStates.GetValueOrDefault(ProcessIdentity(process)) is
            { ConsecutiveReliableWorkingSetSamples: >= 2 };

    public bool IsWorkingSetCooling(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTarget target,
        ProcessSnapshot process,
        DateTimeOffset now,
        int cooldownMinutes)
    {
        var lastExecution = GetTargetState(rule, target).ProcessStates
            .GetValueOrDefault(ProcessIdentity(process))?.LastWorkingSetExecutionAt;
        return lastExecution.HasValue &&
               now - lastExecution.Value < TimeSpan.FromMinutes(NormalizeMinutes(cooldownMinutes));
    }

    public bool IsDelayDue(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTarget target,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches,
        DateTimeOffset museRamStartedAt,
        DateTimeOffset now) =>
        IsDelayDue(rule, target, matches, museRamStartedAt, now, GetTargetState(rule, target));

    public void RecordSuccessfulExecution(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ApplicationOptimizationRuleTargetDecision> decisions,
        IReadOnlyCollection<ProcessSnapshot> successfulProcesses,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long releasedBytes = 0,
        long? retainedBytes = null,
        IReadOnlyDictionary<string, long>? releasedBytesByProcessIdentity = null)
    {
        var successfulIdentities = successfulProcesses
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .Select(ProcessIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
        {
            var targetProcesses = decision.Matches
                .SelectMany(match => match.Processes)
                .Where(process => successfulIdentities.Contains(ProcessIdentity(process)))
                .GroupBy(ProcessIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (targetProcesses.Length == 0) continue;
            var state = GetTargetState(rule, decision.Target);
            if (decision.DelayDue)
            {
                state.DelayExecutionsCompleted = state.DelayExecutionsCompleted == int.MaxValue
                    ? int.MaxValue
                    : state.DelayExecutionsCompleted + 1;
                state.LastDelayExecutionAt = completedAt;
            }
            state.LastExecutionStartedAt = startedAt;
            state.LastReleasedBytes = releasedBytesByProcessIdentity is null
                ? Math.Max(0, releasedBytes)
                : targetProcesses.Sum(process => Math.Max(
                    0,
                    releasedBytesByProcessIdentity.GetValueOrDefault(ProcessIdentity(process))));
            state.LastRetainedBytes = retainedBytes;
            state.LastSkippedReason = null;
            foreach (var processState in state.ProcessStates.Values)
                processState.ConsecutiveReliableWorkingSetSamples = 0;
            foreach (var process in targetProcesses)
            {
                var processState = GetProcessState(state, process);
                processState.LastSuccessfulTrimAt = completedAt;
                if (decision.WorkingSetDueProcesses.Any(candidate =>
                        string.Equals(ProcessIdentity(candidate), ProcessIdentity(process), StringComparison.OrdinalIgnoreCase)))
                {
                    processState.LastWorkingSetExecutionAt = completedAt;
                }
            }
        }
    }

    // Compatibility entry point for callers from the first implementation. It records cooldown/sample reset
    // without creating a permanent exclusion set.
    public void MarkExecuted(ApplicationOptimizationRule rule, IEnumerable<ProcessSnapshot> processes)
    {
        var successful = processes.Where(process => process.StartTimeFileTimeUtc is > 0).ToArray();
        var decisions = GetTargetDecisions(
            rule,
            Array.Empty<ProcessFamilySnapshot>(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        if (decisions.Count > 0)
        {
            RecordSuccessfulExecution(rule, decisions, successful, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return;
        }
        foreach (var target in rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
        {
            var state = GetTargetState(rule, target);
            foreach (var process in successful)
            {
                var processState = GetProcessState(state, process);
                processState.ConsecutiveReliableWorkingSetSamples = 0;
                processState.LastSuccessfulTrimAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void ResetExecutionForRule(string ruleId)
    {
        foreach (var key in _targetStates.Keys
                     .Where(key => key.StartsWith(ruleId + "|", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _targetStates.Remove(key);
        }
    }

    public void ResetExecutionForTarget(ApplicationOptimizationRule rule, ApplicationOptimizationRuleTarget target) =>
        _targetStates.Remove(StateKey(rule, ApplicationOptimizationRulePolicy.TargetIdentity(target)));

    public IReadOnlyList<ApplicationOptimizationRuleTargetProgress> CaptureProgress(
        IReadOnlyList<ApplicationOptimizationRule> rules,
        DateTimeOffset now)
    {
        var progress = new List<ApplicationOptimizationRuleTargetProgress>();
        foreach (var rule in rules)
        {
            foreach (var target in rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            {
                var targetIdentity = ApplicationOptimizationRulePolicy.TargetIdentity(target);
                if (!_targetStates.TryGetValue(StateKey(rule, targetIdentity), out var state)) continue;
                progress.Add(new ApplicationOptimizationRuleTargetProgress(
                    rule.Id,
                    targetIdentity,
                    state.ConfigurationRevision,
                    state.ConfigurationKey,
                    state.CurrentLaunchSignature ?? string.Empty,
                    Math.Max(0, state.DelayExecutionsCompleted),
                    ElapsedSeconds(state.LastDelayExecutionAt, now),
                    ElapsedSeconds(state.LastExecutionStartedAt, now),
                    Math.Max(0, state.LastReleasedBytes),
                    state.LastRetainedBytes,
                    state.LastSkippedReason,
                    state.ProcessStates.Select(pair => new ApplicationOptimizationRuleProcessProgress(
                        pair.Key,
                        ElapsedSeconds(pair.Value.LastSuccessfulTrimAt, now),
                        ElapsedSeconds(pair.Value.LastWorkingSetExecutionAt, now))).ToArray()));
            }
        }
        return progress;
    }

    public void RestoreProgress(
        IReadOnlyList<ApplicationOptimizationRuleTargetProgress>? progress,
        IReadOnlyList<ApplicationOptimizationRule> rules,
        IReadOnlyList<ProcessFamilySnapshot> families,
        DateTimeOffset now)
    {
        foreach (var item in progress ?? Array.Empty<ApplicationOptimizationRuleTargetProgress>())
        {
            var rule = rules.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, item.RuleId, StringComparison.OrdinalIgnoreCase));
            var target = rule?.Targets?.FirstOrDefault(candidate => string.Equals(
                ApplicationOptimizationRulePolicy.TargetIdentity(candidate),
                item.TargetIdentity,
                StringComparison.OrdinalIgnoreCase));
            if (rule is null || target is null ||
                item.ConfigurationRevision != Math.Max(1, rule.ConfigurationRevision) ||
                !string.Equals(item.ConfigurationKey, ConfigurationKey(rule, item.TargetIdentity), StringComparison.Ordinal))
                continue;

            var matches = ApplicationOptimizationRulePolicy.ResolveMatches(rule, families)
                .Where(match => string.Equals(
                    ApplicationOptimizationRulePolicy.TargetIdentity(match.Target),
                    item.TargetIdentity,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var launchSignature = LaunchSignature(target, matches);
            if (string.IsNullOrEmpty(launchSignature) ||
                !string.Equals(item.LaunchSignature, launchSignature, StringComparison.Ordinal))
                continue;

            var currentProcessIdentities = matches
                .SelectMany(match => match.Processes)
                .Where(process => process.StartTimeFileTimeUtc is > 0)
                .Select(ProcessIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var state = GetTargetState(rule, target);
            state.CurrentLaunchSignature = launchSignature;
            state.DelayExecutionsCompleted = Math.Clamp(
                item.DelayExecutionsCompleted,
                0,
                rule.RepeatIndefinitely ? int.MaxValue : NormalizeExecutionCount(rule.ExecutionCount));
            state.LastDelayExecutionAt = RestoreAnchor(item.LastDelayExecutionElapsedSeconds, now);
            state.LastExecutionStartedAt = RestoreAnchor(item.LastExecutionStartedElapsedSeconds, now);
            state.LastReleasedBytes = Math.Max(0, item.LastReleasedBytes);
            state.LastRetainedBytes = item.LastRetainedBytes is { } retained ? Math.Max(0, retained) : null;
            state.LastSkippedReason = item.LastSkippedReason;
            state.ProcessStates.Clear();
            foreach (var process in item.Processes ?? Array.Empty<ApplicationOptimizationRuleProcessProgress>())
            {
                if (!currentProcessIdentities.Contains(process.ProcessIdentity)) continue;
                state.ProcessStates[process.ProcessIdentity] = new ApplicationOptimizationRuleProcessRuntimeState
                {
                    ConsecutiveReliableWorkingSetSamples = 0,
                    LastSuccessfulTrimAt = RestoreAnchor(process.LastSuccessfulTrimElapsedSeconds, now),
                    LastWorkingSetExecutionAt = RestoreAnchor(process.LastWorkingSetExecutionElapsedSeconds, now)
                };
            }
        }
    }

    public void RecordRetainedOutcome(
        string ruleId,
        string targetIdentity,
        DateTimeOffset executionStartedAt,
        long retainedBytes)
    {
        if (!_targetStates.TryGetValue($"{ruleId}|{targetIdentity}", out var state) ||
            state.LastExecutionStartedAt != executionStartedAt)
            return;
        state.LastRetainedBytes = (state.LastRetainedBytes ?? 0) + Math.Max(0, retainedBytes);
    }

    private ApplicationOptimizationRuleTargetRuntimeState GetTargetState(
        ApplicationOptimizationRule rule,
        string targetIdentity)
    {
        var key = StateKey(rule, targetIdentity);
        var configurationKey = ConfigurationKey(rule, targetIdentity);
        if (!_targetStates.TryGetValue(key, out var state) ||
            !string.Equals(state.ConfigurationKey, configurationKey, StringComparison.Ordinal))
        {
            state = new ApplicationOptimizationRuleTargetRuntimeState
            {
                TargetIdentity = targetIdentity,
                ConfigurationRevision = Math.Max(1, rule.ConfigurationRevision),
                ConfigurationKey = configurationKey
            };
            _targetStates[key] = state;
        }
        return state;
    }

    private static ApplicationOptimizationRuleProcessRuntimeState GetProcessState(
        ApplicationOptimizationRuleTargetRuntimeState state,
        ProcessSnapshot process)
    {
        var identity = ProcessIdentity(process);
        if (!state.ProcessStates.TryGetValue(identity, out var processState))
        {
            processState = new ApplicationOptimizationRuleProcessRuntimeState();
            state.ProcessStates[identity] = processState;
        }
        return processState;
    }

    private static bool IsDelayDue(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTarget target,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches,
        DateTimeOffset museRamStartedAt,
        DateTimeOffset now,
        ApplicationOptimizationRuleTargetRuntimeState state)
    {
        if (!rule.Enabled ||
            rule.TriggerMode != ApplicationOptimizationRuleTriggerMode.Delayed ||
            !rule.DelayTriggerEnabled ||
            (!rule.RepeatIndefinitely &&
             state.DelayExecutionsCompleted >= NormalizeExecutionCount(rule.ExecutionCount)))
            return false;
        var anchor = rule.DelayAnchor == ApplicationOptimizationDelayAnchor.MuseRamStartup
            ? museRamStartedAt
            : TargetApplicationStartedAt(ApplicationOptimizationRulePolicy.ResolveLaunchProcesses(target, matches));
        if (!anchor.HasValue || now - anchor.Value < TimeSpan.FromMinutes(NormalizeMinutes(rule.DelayMinutes)))
            return false;
        return !state.LastDelayExecutionAt.HasValue ||
               now - state.LastDelayExecutionAt.Value >=
               TimeSpan.FromMinutes(NormalizeMinutes(rule.ExecutionIntervalMinutes));
    }

    private static string ConfigurationKey(ApplicationOptimizationRule rule, string targetIdentity)
    {
        var targetBypass = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .FirstOrDefault(target => string.Equals(
                ApplicationOptimizationRulePolicy.TargetIdentity(target),
                targetIdentity,
                StringComparison.OrdinalIgnoreCase))
            ?.BypassProtectionConfirmed == true;
        return string.Join(
            "|",
            Math.Max(0, rule.WorkingSetThresholdBytes),
            rule.TriggerMode,
            rule.DelayTriggerEnabled,
            rule.DelayAnchor,
            rule.DelayMinutes,
            rule.ExecutionCount,
            rule.ExecutionIntervalMinutes,
            rule.RepeatIndefinitely,
            rule.RestartWithApplication,
            rule.WorkingSetTriggerEnabled,
            rule.WorkingSetThresholdFollowsProfile,
            rule.CooldownMinutes,
            targetBypass,
            targetIdentity);
    }

    private static string StateKey(ApplicationOptimizationRule rule, string targetIdentity) =>
        $"{rule.Id}|{targetIdentity}";

    public static string ProcessIdentity(ProcessSnapshot process) =>
        $"{process.ProcessId}|{process.StartTimeFileTimeUtc}";

    private static string LaunchSignature(
        ApplicationOptimizationRuleTarget target,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches)
    {
        var anchor = ApplicationOptimizationRulePolicy.ResolveLaunchProcesses(target, matches)
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .OrderBy(process => process.StartTimeFileTimeUtc)
            .ThenBy(process => process.ProcessId)
            .FirstOrDefault();
        return anchor is null ? string.Empty : ProcessIdentity(anchor);
    }

    private static double? ElapsedSeconds(DateTimeOffset? anchor, DateTimeOffset now) =>
        anchor is { } value ? Math.Max(0, (now - value).TotalSeconds) : null;

    private static DateTimeOffset? RestoreAnchor(double? elapsedSeconds, DateTimeOffset now) =>
        elapsedSeconds is { } value && double.IsFinite(value) && value >= 0
            ? now - TimeSpan.FromSeconds(Math.Min(value, TimeSpan.FromDays(36500).TotalSeconds))
            : null;

    private static DateTimeOffset? TargetApplicationStartedAt(IReadOnlyList<ProcessSnapshot> processes)
    {
        var start = processes
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .Select(process => DateTimeOffset.FromFileTime(process.StartTimeFileTimeUtc!.Value))
            .OrderBy(value => value)
            .FirstOrDefault();
        return start == default ? null : start;
    }

    private static int NormalizeMinutes(int value) => Math.Clamp(value, 1, 1440);
    private static int NormalizeExecutionCount(int value) => Math.Clamp(value, 1, 10);
}

public static class ApplicationOptimizationRulePolicy
{
    public static IReadOnlyList<ProcessSnapshot> ResolveLaunchProcesses(
        ApplicationOptimizationRuleTarget target,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches)
    {
        var executableGroupPaths = target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
            ? TargetPaths(target)
            : null;
        var processes = matches
            .SelectMany(match => match.Processes)
            .Where(process => target.TargetType switch
            {
                ApplicationOptimizationTargetType.ApplicationFamily => PathsEqual(process.ExecutablePath, target.Path),
                ApplicationOptimizationTargetType.ExecutableGroup => executableGroupPaths!.Contains(
                    ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var path) ? path : string.Empty),
                _ => true
            })
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .GroupBy(ApplicationOptimizationRuleRuntime.ProcessIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return processes;
    }

    public static IReadOnlyList<ApplicationOptimizationRule> NormalizeRules(
        IEnumerable<ApplicationOptimizationRule>? rules)
    {
        var result = new List<ApplicationOptimizationRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules ?? Array.Empty<ApplicationOptimizationRule>())
        {
            if (rule is null) continue;
            var sourceTargets = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                .Where(target => target is not null &&
                                 ExecutablePathIdentity.TryNormalize(target.Path, out _) &&
                                 (target.TargetType != ApplicationOptimizationTargetType.ExecutableGroup ||
                                  TargetPaths(target).Count > 0))
                .ToArray();
            var isLegacyRuleWithOnlyRuleLevelBypass = rule.BypassProtection &&
                sourceTargets.Length > 0 &&
                sourceTargets.All(target => target.BypassProtectionConfirmed is null);
            var targets = sourceTargets
                .Select(target => new ApplicationOptimizationRuleTarget
                {
                    TargetType = target.TargetType,
                    Path = ExecutablePathIdentity.Normalize(target.Path),
                    ExecutablePaths = target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                        ? TargetPaths(target).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>(),
                    BypassProtectionConfirmed = target.BypassProtectionConfirmed ??
                        isLegacyRuleWithOnlyRuleLevelBypass
                })
                .GroupBy(TargetIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (targets.Count == 0) continue;

            var id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim();
            while (!ids.Add(id)) id = Guid.NewGuid().ToString("N");
            result.Add(Clone(rule, id, targets));
        }
        return result;
    }

    public static IReadOnlyList<AutomaticOptimizationThresholdOverride> CreateAutomaticThresholdOverrides(
        IEnumerable<ApplicationOptimizationRule>? rules,
        IReadOnlyList<ProcessFamilySnapshot> families,
        bool automaticOptimizationEnabled)
    {
        if (!automaticOptimizationEnabled) return Array.Empty<AutomaticOptimizationThresholdOverride>();
        return (rules ?? Array.Empty<ApplicationOptimizationRule>())
            .Where(rule => rule.Enabled &&
                           rule.TriggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic)
            .SelectMany(rule => CreateAutomaticThresholdOverrides(rule, families))
            .ToArray();
    }

    private static IReadOnlyList<AutomaticOptimizationThresholdOverride> CreateAutomaticThresholdOverrides(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ProcessFamilySnapshot> families)
    {
        var threshold = Math.Max(1L.MiB(), rule.WorkingSetThresholdBytes);
        return ResolveMatches(rule, families)
            .GroupBy(match => TargetIdentity(match.Target), StringComparer.OrdinalIgnoreCase)
            .SelectMany(targetGroup =>
            {
                var target = targetGroup.First().Target;
                var aggregateSatisfied = target.TargetType != ApplicationOptimizationTargetType.ExecutableGroup ||
                    targetGroup.SelectMany(match => match.Processes)
                        .Where(process => process.HasReliableActivitySample)
                        .GroupBy(ApplicationOptimizationRuleRuntime.ProcessIdentity, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .Sum(process => Math.Max(0, process.WorkingSetBytes)) >= threshold;
                return targetGroup
                    .GroupBy(match => match.Family.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(familyGroup => new AutomaticOptimizationThresholdOverride(
                        $"{rule.Id}|{targetGroup.Key}",
                        familyGroup.Key,
                        familyGroup.SelectMany(match => match.Processes)
                            .Select(process => process.ProcessId)
                            .ToHashSet(),
                        target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                            ? aggregateSatisfied ? 1 : long.MaxValue
                            : threshold)
                    {
                        BypassProtection = target.BypassProtectionConfirmed == true
                    });
            })
            .ToArray();
    }

    public static IReadOnlyList<ApplicationOptimizationRuleTargetMatch> ResolveMatches(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ProcessFamilySnapshot> families)
    {
        var matches = new List<ApplicationOptimizationRuleTargetMatch>();
        foreach (var target in rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
        {
            if (!ExecutablePathIdentity.TryNormalize(target.Path, out var path)) continue;
            var targetDirectory = Path.GetDirectoryName(path);
            var executableGroupPaths = target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                ? TargetPaths(target)
                : null;
            foreach (var family in families)
            {
                var processes = target.TargetType switch
                {
                    ApplicationOptimizationTargetType.Executable => family.Processes
                        .Where(process => PathsEqual(process.ExecutablePath, path))
                        .ToArray(),
                    ApplicationOptimizationTargetType.ApplicationFamily =>
                        family.Processes.Any(process => PathsEqual(process.ExecutablePath, path)) ||
                        string.Equals(
                            NormalizeDirectory(family.ExecutableDirectory),
                            NormalizeDirectory(targetDirectory),
                            StringComparison.OrdinalIgnoreCase)
                            ? family.Processes.ToArray()
                            : Array.Empty<ProcessSnapshot>(),
                    ApplicationOptimizationTargetType.ExecutableGroup => family.Processes
                        .Where(process => ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var processPath) &&
                                          executableGroupPaths!.Contains(processPath))
                        .ToArray(),
                    _ => Array.Empty<ProcessSnapshot>()
                };
                if (processes.Length == 0) continue;
                matches.Add(new ApplicationOptimizationRuleTargetMatch(
                    rule.Id,
                    target,
                    family,
                    processes));
            }
        }
        return matches
            .GroupBy(match => $"{match.Family.Key}|{match.Target.TargetType}|{match.Target.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static bool IsDelayDue(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleTargetMatch match,
        DateTimeOffset museRamStartedAt,
        DateTimeOffset now,
        DateTimeOffset? lastExecutionAt,
        int executionsCompleted)
    {
        if (!rule.Enabled ||
            rule.TriggerMode != ApplicationOptimizationRuleTriggerMode.Delayed ||
            !rule.DelayTriggerEnabled ||
            (!rule.RepeatIndefinitely && executionsCompleted >= NormalizeExecutionCount(rule.ExecutionCount)))
            return false;
        var anchor = rule.DelayAnchor == ApplicationOptimizationDelayAnchor.MuseRamStartup
            ? museRamStartedAt
            : TargetApplicationStartedAt(ResolveLaunchProcesses(match.Target, new[] { match }));
        if (!anchor.HasValue || now - anchor.Value < TimeSpan.FromMinutes(NormalizeMinutes(rule.DelayMinutes)))
            return false;
        return !lastExecutionAt.HasValue ||
               now - lastExecutionAt.Value >= TimeSpan.FromMinutes(NormalizeMinutes(rule.ExecutionIntervalMinutes));
    }

    public static IReadOnlyList<OptimizationCandidate> CreateCandidates(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ProcessFamilySnapshot> families,
        OptimizationSettings settings,
        ProtectionRules protection,
        ApplicationOptimizationRuleRuntime runtime,
        DateTimeOffset now,
        bool delayDue,
        IReadOnlySet<string>? coolingComponentKeys = null,
        IReadOnlySet<string>? delayDueTargetIdentities = null,
        IReadOnlySet<string>? workingSetDueTargetIdentities = null)
    {
        if (!rule.Enabled || !delayDue)
            return Array.Empty<OptimizationCandidate>();

        var allProcesses = families.SelectMany(family => family.Processes).ToArray();
        var activeProcessIds = allProcesses
            .Where(process => process.IsForeground ||
                              process.CpuPercent >= settings.ActiveCpuThresholdPercent ||
                              process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond)
            .Select(process => process.ProcessId)
            .ToHashSet();
        var parentById = allProcesses
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First().ParentProcessId);
        var candidates = new List<OptimizationCandidate>();
        foreach (var match in ResolveMatches(rule, families))
        {
            var targetIdentity = TargetIdentity(match.Target);
            var targetDelayDue = delayDueTargetIdentities?.Contains(targetIdentity) ?? delayDue;
            var targetWorkingSetDue = workingSetDueTargetIdentities?.Contains(targetIdentity) ??
                                      (!rule.WorkingSetTriggerEnabled || match.Processes.Any(process =>
                                          runtime.IsWorkingSetReady(rule, match.Target, process)));
            if (!targetDelayDue || (rule.WorkingSetTriggerEnabled && !targetWorkingSetDue)) continue;
            var protectedProcesses = match.Target.BypassProtectionConfirmed == true
                ? match.Processes
                : protection.FilterUnprotectedProcesses(
                    match.Family,
                    protection.CreateContext(allProcesses))?.Processes ?? Array.Empty<ProcessSnapshot>();
            // Protection filtering may return the whole family. An EXE target must be narrowed
            // back to its explicitly selected executable before any safety checks run.
            var executableGroupPaths = match.Target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                ? TargetPaths(match.Target)
                : null;
            var targets = protectedProcesses
                .Where(process => match.Target.TargetType switch
                {
                    ApplicationOptimizationTargetType.Executable => PathsEqual(process.ExecutablePath, match.Target.Path),
                    ApplicationOptimizationTargetType.ExecutableGroup =>
                        ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var processPath) &&
                        executableGroupPaths!.Contains(processPath),
                    _ => true
                })
                .Where(process => process.WorkingSetBytes > 0)
                .Where(process => process.StartTimeFileTimeUtc is > 0)
                .Where(process => process.ProcessId != Environment.ProcessId)
                .Where(process => !SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath))
                .Where(process => coolingComponentKeys is null ||
                                  !coolingComponentKeys.Contains(
                                      ApplicationComponentIdentity.ForProcess(match.Family.Key, process)))
                .Where(process => process.HasReliableActivitySample)
                .Where(process => process.CpuPercent < settings.ActiveCpuThresholdPercent)
                .Where(process => process.IoBytesPerSecond < settings.ActiveIoThresholdBytesPerSecond)
                .Where(process => !settings.EnhancedSafety || !process.IsForeground)
                .Where(process => (!process.IsForeground || !settings.EnhancedSafety) &&
                                  !HasActiveRelationship(process, activeProcessIds, parentById))
                .ToArray();
            if (targets.Length == 0) continue;
            candidates.Add(new OptimizationCandidate(
                match.Family,
                targets,
                new ProcessFamilySnapshot(
                    match.Family.Key,
                    match.Family.DisplayName,
                    match.Family.ExecutableDirectory,
                    targets).IdleConfidenceScore,
                targets.Sum(process => Math.Max(0, process.WorkingSetBytes)),
                "应用优化规则目标"));
        }

        return candidates
            .GroupBy(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .SelectMany(candidate => candidate.TargetProcesses)
                .GroupBy(process => $"{process.ProcessId}|{process.StartTimeFileTimeUtc}")
                .Select(processes => processes.First())
                .ToArray() is { Length: > 0 } targets
                    ? group.First() with
                    {
                        TargetProcesses = targets,
                        PotentialReleaseBytes = targets.Sum(process => Math.Max(0, process.WorkingSetBytes))
                    }
                    : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static bool HasActiveRelationship(
        ProcessSnapshot process,
        IReadOnlySet<int> activeProcessIds,
        IReadOnlyDictionary<int, int?> parentById)
    {
        var scope = ProcessRelationshipPolicy.BuildSafetyScope(process.ProcessId, parentById
            .Select(pair => new ProcessSnapshot(
                pair.Key,
                string.Empty,
                null,
                pair.Value,
                0,
                0,
                0,
                false,
                false,
                true,
                0))
            .ToArray());
        return scope.Any(activeId => activeId != process.ProcessId && activeProcessIds.Contains(activeId));
    }

    private static DateTimeOffset? TargetApplicationStartedAt(IReadOnlyList<ProcessSnapshot> processes)
    {
        var start = processes
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .Select(process => DateTimeOffset.FromFileTime(process.StartTimeFileTimeUtc!.Value))
            .OrderBy(value => value)
            .FirstOrDefault();
        return start == default ? null : start;
    }

    private static ApplicationOptimizationRule Clone(
        ApplicationOptimizationRule rule,
        string id,
        List<ApplicationOptimizationRuleTarget> targets)
    {
        var triggerMode = Enum.IsDefined(rule.TriggerMode)
            ? rule.TriggerMode
            : ApplicationOptimizationRuleTriggerMode.Delayed;
        return new ApplicationOptimizationRule
        {
            Id = id,
            Enabled = rule.Enabled,
            Targets = targets,
            TriggerMode = triggerMode,
            DelayTriggerEnabled = triggerMode == ApplicationOptimizationRuleTriggerMode.Delayed &&
                                  rule.DelayTriggerEnabled,
            DelayAnchor = rule.DelayAnchor,
            DelayMinutes = NormalizeMinutes(rule.DelayMinutes),
            ExecutionCount = NormalizeExecutionCount(rule.ExecutionCount),
            ExecutionIntervalMinutes = NormalizeMinutes(rule.ExecutionIntervalMinutes),
            RepeatIndefinitely = triggerMode == ApplicationOptimizationRuleTriggerMode.Delayed &&
                                 rule.RepeatIndefinitely,
            RestartWithApplication = triggerMode == ApplicationOptimizationRuleTriggerMode.Delayed &&
                                     rule.DelayAnchor == ApplicationOptimizationDelayAnchor.TargetApplicationStartup &&
                                     rule.RestartWithApplication,
            WorkingSetTriggerEnabled = triggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic ||
                                       rule.WorkingSetTriggerEnabled,
            WorkingSetThresholdFollowsProfile = triggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic
                ? false
                : rule.WorkingSetThresholdFollowsProfile,
            WorkingSetThresholdBytes = Math.Max(1L.MiB(), rule.WorkingSetThresholdBytes),
            CooldownMinutes = NormalizeMinutes(rule.CooldownMinutes),
            ConfigurationRevision = Math.Max(1, rule.ConfigurationRevision),
            BypassProtection = rule.BypassProtection
        };
    }

    public static string TargetIdentity(ApplicationOptimizationRuleTarget target)
    {
        if (target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup)
            return $"{target.TargetType}|{string.Join(";", TargetPaths(target).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}";
        var path = ExecutablePathIdentity.TryNormalize(target.Path, out var normalized)
            ? normalized
            : target.Path.Trim();
        return $"{target.TargetType}|{path}";
    }

    public static bool TargetsOverlap(
        ApplicationOptimizationRuleTarget left,
        ApplicationOptimizationRuleTarget right)
    {
        if (string.Equals(TargetIdentity(left), TargetIdentity(right), StringComparison.OrdinalIgnoreCase))
            return true;
        if (left.TargetType != ApplicationOptimizationTargetType.ExecutableGroup &&
            right.TargetType != ApplicationOptimizationTargetType.ExecutableGroup)
            return false;
        if (left.TargetType == ApplicationOptimizationTargetType.ApplicationFamily ||
            right.TargetType == ApplicationOptimizationTargetType.ApplicationFamily)
            return false;
        return TargetPaths(left).Overlaps(TargetPaths(right));
    }

    public static IReadOnlySet<string> TargetPaths(ApplicationOptimizationRuleTarget target)
    {
        if (target.TargetType != ApplicationOptimizationTargetType.ExecutableGroup)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExecutablePathIdentity.Normalize(target.Path) };
        return (target.ExecutablePaths ?? new List<string>())
            .Where(path => ExecutablePathIdentity.TryNormalize(path, out _))
            .Select(ExecutablePathIdentity.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string right) =>
        ExecutablePathIdentity.TryNormalize(left, out var normalized) &&
        string.Equals(normalized, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static int NormalizeMinutes(int value) => Math.Clamp(value, 1, 1440);
    private static int NormalizeExecutionCount(int value) => Math.Clamp(value, 1, 10);
}
