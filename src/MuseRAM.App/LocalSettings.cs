using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using MuseRAM.Core;

namespace MuseRAM.App;

public enum CloseButtonBehavior
{
    Ask,
    Exit,
    MinimizeToTray
}

public static class SettingsSchema
{
    public const int CurrentVersion = 7;
}

public static class CandidateDisplayLimitPolicy
{
    public const int Unlimited = 0;
    public const int Default = 20;

    public static int Normalize(int value) => value is 10 or 20 or 40 or Unlimited
        ? value
        : Default;
}

public sealed record LocalSettingsLoadResult(
    LocalSettings Settings,
    bool Migrated,
    string? ErrorMessage);

public sealed class LocalSettings
{
    public int SettingsVersion { get; set; } = SettingsSchema.CurrentVersion;
    public OptimizationProfile Profile { get; set; } = OptimizationProfile.Turbo;
    public string? ActiveCustomProfileId { get; set; }
    public bool ShowBuiltInProfiles { get; set; } = true;
    public List<CustomOptimizationProfile> CustomProfiles { get; set; } = new();
    public bool AutoOptimization { get; set; }
    public bool ScheduledOptimizationEnabled { get; set; }
    public int ScheduledOptimizationIntervalMinutes { get; set; } = ScheduledOptimizationPolicy.DefaultIntervalMinutes;
    public bool GlobalReclaimIntervalEnabled { get; set; }
    public int GlobalReclaimIntervalMinutes { get; set; } = GlobalReclaimSchedulePolicy.DefaultIntervalMinutes;
    public bool GlobalReclaimStartupDelayEnabled { get; set; }
    public int GlobalReclaimStartupDelayMinutes { get; set; } = GlobalReclaimSchedulePolicy.DefaultStartupDelayMinutes;
    public bool LongIdleOptimizationEnabled { get; set; }
    public int LongIdleOptimizationMinutes { get; set; } = LongIdleOptimizationPolicy.DefaultMinutes;
    public bool StartWithWindows { get; set; }
    public bool LightTheme { get; set; } = true;
    public bool FollowSystemTheme { get; set; }
    public bool ShowMemoryUsageInTrayIcon { get; set; }
    public bool EnhancedSafety { get; set; }
    public bool IgnoreMemoryPressureThreshold { get; set; }
    public bool IntelligentCandidateSelection { get; set; }
    public StableStateSuppressionMode StableStateSuppressionMode { get; set; } =
        StableStateSuppressionMode.FollowBaseProfile;
    public string? ActiveCustomStableStateSuppressionProfileId { get; set; }
    public bool ShowBuiltInStableStateSuppressionProfiles { get; set; } = true;
    public List<CustomStableStateSuppressionProfile> CustomStableStateSuppressionProfiles { get; set; } = new();
    public List<ApplicationStableAnchorSetting> StableAnchorSettings { get; set; } = new();
    // Retained so schema 1-2 settings and downgrade builds can still read the last custom values.
    public StableStateSuppressionSettings CustomStableStateSuppression { get; set; } =
        StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
    public bool DiagnosticDataCollectionEnabled { get; set; }
    public bool RuntimeProgressPersistenceEnabled { get; set; }
    public bool QuickCandidateSelection { get; set; }
    public int CandidateDisplayLimit { get; set; } = CandidateDisplayLimitPolicy.Default;
    public bool ProtectRelatedProcesses { get; set; } = true;
    public bool UltimateRiskPromptSuppressed { get; set; }
    public bool SelectedApplicationOptimizationPromptSuppressed { get; set; }
    public bool ReboundProtectionWarningSuppressed { get; set; }
    public CloseButtonBehavior CloseButtonBehavior { get; set; } = CloseButtonBehavior.Ask;
    public string LanguageCode { get; set; } = "zh-CN";
    public string UpdateFeedUrl { get; set; } = UpdateConfiguration.FeedUrl;
    public string UpdateDirectory { get; set; } = string.Empty;
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.EveryStartup;
    public DateTimeOffset? LastAutomaticUpdateCheckUtc { get; set; }
    public string SuppressedUpdateVersion { get; set; } = string.Empty;
    // Null keeps legacy fields authoritative; an empty list explicitly means no protection rules.
    public List<ApplicationProtectionRule>? ApplicationProtectionRules { get; set; }
    public List<string> ProtectedPaths { get; set; } = new();
    public List<ApplicationOptimizationRule>? ApplicationOptimizationRules { get; set; }
    // Identifies this Windows boot and prevents a MuseRAM restart from treating the same app instance as new.
    public long FirstBootStableReviewBootUtcTicks { get; set; }
    public List<string> FirstBootStableReviewLaunches { get; set; } = new();

    public CustomOptimizationProfile? ActiveCustomProfile => CustomProfiles.FirstOrDefault(profile =>
        string.Equals(profile.Id, ActiveCustomProfileId, StringComparison.OrdinalIgnoreCase));

    public CustomStableStateSuppressionProfile? ActiveCustomStableStateSuppressionProfile =>
        CustomStableStateSuppressionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, ActiveCustomStableStateSuppressionProfileId, StringComparison.OrdinalIgnoreCase));

    public OptimizationSettings ResolveOptimizationSettings(bool manual)
    {
        var settings = ResolveProfileOptimizationSettings(manual);
        return settings with
        {
            IgnoreMemoryPressureThreshold = settings.IgnoreMemoryPressureThreshold ||
                                            IgnoreMemoryPressureThreshold,
            QuickCandidateSelection = !manual && QuickCandidateSelection
        };
    }

    public bool ActiveProfileIgnoresMemoryPressureThreshold =>
        ResolveProfileOptimizationSettings(manual: false).IgnoreMemoryPressureThreshold;

    private OptimizationSettings ResolveProfileOptimizationSettings(bool manual)
    {
        var custom = ActiveCustomProfile;
        return custom is not null
            ? custom.Settings
            : manual
                ? OptimizationSettings.ForManual(Profile)
                : OptimizationSettings.For(Profile);
    }

    public ReboundBackoffSettings ResolveReboundSettings() =>
        ActiveCustomProfile?.Rebound ?? ReboundBackoffSettings.For(Profile);

    public StableStateSuppressionMode ResolveStableStateSuppressionMode() =>
        StableStateSuppressionPolicy.ResolveMode(
            ActiveCustomProfile?.BaseProfile ?? Profile,
            StableStateSuppressionMode);

    public StableStateSuppressionSettings? ResolveStableStateSuppressionSettings()
    {
        var configuredMode = StableStateSuppressionMode;
        if (configuredMode == MuseRAM.Core.StableStateSuppressionMode.Disabled) return null;
        if (configuredMode == MuseRAM.Core.StableStateSuppressionMode.Custom)
            return (ActiveCustomStableStateSuppressionProfile?.Settings ?? CustomStableStateSuppression).Normalize();
        if (configuredMode != MuseRAM.Core.StableStateSuppressionMode.FollowBaseProfile)
            return MuseRAM.Core.StableStateSuppressionSettings.For(configuredMode);
        return MuseRAM.Core.StableStateSuppressionSettings.For(
            ActiveCustomProfile?.BaseProfile ?? Profile);
    }

    public ProtectionRules CreateProtectionRules() =>
        new(ApplicationProtectionSettings.Resolve(this));

    public LocalSettings DeepClone() => new()
    {
        SettingsVersion = SettingsVersion,
        Profile = Profile,
        ActiveCustomProfileId = ActiveCustomProfileId,
        ShowBuiltInProfiles = ShowBuiltInProfiles,
        CustomProfiles = CustomProfiles.Select(CloneCustomProfile).ToList(),
        AutoOptimization = AutoOptimization,
        ScheduledOptimizationEnabled = ScheduledOptimizationEnabled,
        ScheduledOptimizationIntervalMinutes = ScheduledOptimizationIntervalMinutes,
        GlobalReclaimIntervalEnabled = GlobalReclaimIntervalEnabled,
        GlobalReclaimIntervalMinutes = GlobalReclaimIntervalMinutes,
        GlobalReclaimStartupDelayEnabled = GlobalReclaimStartupDelayEnabled,
        GlobalReclaimStartupDelayMinutes = GlobalReclaimStartupDelayMinutes,
        LongIdleOptimizationEnabled = LongIdleOptimizationEnabled,
        LongIdleOptimizationMinutes = LongIdleOptimizationMinutes,
        StartWithWindows = StartWithWindows,
        LightTheme = LightTheme,
        FollowSystemTheme = FollowSystemTheme,
        ShowMemoryUsageInTrayIcon = ShowMemoryUsageInTrayIcon,
        EnhancedSafety = EnhancedSafety,
        IgnoreMemoryPressureThreshold = IgnoreMemoryPressureThreshold,
        IntelligentCandidateSelection = IntelligentCandidateSelection,
        StableStateSuppressionMode = StableStateSuppressionMode,
        ActiveCustomStableStateSuppressionProfileId = ActiveCustomStableStateSuppressionProfileId,
        ShowBuiltInStableStateSuppressionProfiles = ShowBuiltInStableStateSuppressionProfiles,
        CustomStableStateSuppressionProfiles = CustomStableStateSuppressionProfiles
            .Select(profile => profile.Clone())
            .ToList(),
        StableAnchorSettings = StableAnchorSettings.Select(anchor => anchor with { }).ToList(),
        CustomStableStateSuppression = CustomStableStateSuppression with { },
        DiagnosticDataCollectionEnabled = DiagnosticDataCollectionEnabled,
        RuntimeProgressPersistenceEnabled = RuntimeProgressPersistenceEnabled,
        QuickCandidateSelection = QuickCandidateSelection,
        CandidateDisplayLimit = CandidateDisplayLimit,
        ProtectRelatedProcesses = ProtectRelatedProcesses,
        UltimateRiskPromptSuppressed = UltimateRiskPromptSuppressed,
        SelectedApplicationOptimizationPromptSuppressed = SelectedApplicationOptimizationPromptSuppressed,
        ReboundProtectionWarningSuppressed = ReboundProtectionWarningSuppressed,
        CloseButtonBehavior = CloseButtonBehavior,
        LanguageCode = LanguageCode,
        UpdateFeedUrl = UpdateFeedUrl,
        UpdateDirectory = UpdateDirectory,
        UpdateCheckFrequency = UpdateCheckFrequency,
        LastAutomaticUpdateCheckUtc = LastAutomaticUpdateCheckUtc,
        SuppressedUpdateVersion = SuppressedUpdateVersion,
        ApplicationProtectionRules = ApplicationProtectionRules?.Select(CloneProtectionRule).ToList(),
        ProtectedPaths = ProtectedPaths.ToList(),
        ApplicationOptimizationRules = ApplicationOptimizationRules?.Select(CloneOptimizationRule).ToList(),
        FirstBootStableReviewBootUtcTicks = FirstBootStableReviewBootUtcTicks,
        FirstBootStableReviewLaunches = FirstBootStableReviewLaunches.ToList()
    };

    private static ApplicationProtectionRule CloneProtectionRule(ApplicationProtectionRule rule) => new()
    {
        ApplicationExecutablePath = rule.ApplicationExecutablePath,
        ProtectEntireFamily = rule.ProtectEntireFamily,
        ProtectedExecutablePaths = rule.ProtectedExecutablePaths?.ToList() ?? new List<string>()
    };

    private static ApplicationOptimizationRule CloneOptimizationRule(ApplicationOptimizationRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Targets = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Select(target => new ApplicationOptimizationRuleTarget
            {
                TargetType = target.TargetType,
                Path = target.Path,
                ExecutablePaths = target.ExecutablePaths?.ToList() ?? new List<string>(),
                BypassProtectionConfirmed = target.BypassProtectionConfirmed
            })
            .ToList(),
        TriggerMode = rule.TriggerMode,
        DelayTriggerEnabled = rule.DelayTriggerEnabled,
        DelayAnchor = rule.DelayAnchor,
        DelayMinutes = rule.DelayMinutes,
        ExecutionCount = rule.ExecutionCount,
        ExecutionIntervalMinutes = rule.ExecutionIntervalMinutes,
        RepeatIndefinitely = rule.RepeatIndefinitely,
        RestartWithApplication = rule.RestartWithApplication,
        WorkingSetTriggerEnabled = rule.WorkingSetTriggerEnabled,
        WorkingSetThresholdFollowsProfile = rule.WorkingSetThresholdFollowsProfile,
        WorkingSetThresholdBytes = rule.WorkingSetThresholdBytes,
        CooldownMinutes = rule.CooldownMinutes,
        ConfigurationRevision = rule.ConfigurationRevision,
        BypassProtection = rule.BypassProtection
    };

    private static CustomOptimizationProfile CloneCustomProfile(CustomOptimizationProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        BaseProfile = profile.BaseProfile,
        SortOrder = profile.SortOrder,
        Settings = profile.Settings with { },
        Rebound = profile.Rebound with { },
        StableStateSuppression = (profile.StableStateSuppression ??
                                  MuseRAM.Core.StableStateSuppressionSettings.For(profile.BaseProfile)) with { },
        StableStateSuppressionMode = profile.StableStateSuppressionMode
    };
}

public static class ApplicationProtectionSettings
{
    public static IReadOnlyList<ApplicationProtectionRule> Resolve(LocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ApplicationProtectionRules is null
            ? FromLegacy(settings.ProtectedPaths, settings.ProtectRelatedProcesses)
            : NormalizeRules(settings.ApplicationProtectionRules);
    }

    public static void ProtectEntireFamily(LocalSettings settings, string applicationExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var applicationPath = ExecutablePathIdentity.Normalize(applicationExecutablePath);
        var rules = Resolve(settings)
            .Where(rule => !string.Equals(
                rule.ApplicationExecutablePath,
                applicationPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        rules.Add(new ApplicationProtectionRule
        {
            ApplicationExecutablePath = applicationPath,
            ProtectEntireFamily = true
        });
        Replace(settings, rules);
    }

    public static void ProtectSelectedExecutables(
        LocalSettings settings,
        string applicationExecutablePath,
        IEnumerable<string> executablePaths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(executablePaths);
        var applicationPath = ExecutablePathIdentity.Normalize(applicationExecutablePath);
        var protectedPaths = NormalizePaths(executablePaths);
        var rules = Resolve(settings)
            .Where(rule => !string.Equals(
                rule.ApplicationExecutablePath,
                applicationPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (protectedPaths.Count > 0)
        {
            rules.Add(new ApplicationProtectionRule
            {
                ApplicationExecutablePath = applicationPath,
                ProtectEntireFamily = false,
                ProtectedExecutablePaths = protectedPaths
            });
        }
        Replace(settings, rules);
    }

    public static void Remove(LocalSettings settings, string applicationExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var applicationPath = ExecutablePathIdentity.Normalize(applicationExecutablePath);
        Replace(settings, Resolve(settings).Where(rule => !string.Equals(
            rule.ApplicationExecutablePath,
            applicationPath,
            StringComparison.OrdinalIgnoreCase)));
    }

    public static void Replace(
        LocalSettings settings,
        IEnumerable<ApplicationProtectionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rules);
        settings.ApplicationProtectionRules = NormalizeRules(rules);
        SynchronizeLegacyFields(settings);
    }

    public static void SynchronizeLegacyFields(LocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ApplicationProtectionRules is null) return;

        var rules = NormalizeRules(settings.ApplicationProtectionRules);
        settings.ApplicationProtectionRules = rules;
        settings.ProtectedPaths = NormalizePaths(rules.SelectMany(rule =>
            rule.ProtectEntireFamily
                ? (IEnumerable<string>)new[] { rule.ApplicationExecutablePath }
                : rule.ProtectedExecutablePaths));
        // Legacy builds have one global scope, so mixed rules roll back toward extra protection.
        settings.ProtectRelatedProcesses = rules.Count == 0 ||
                                           rules.Any(rule => rule.ProtectEntireFamily);
    }

    internal static void Normalize(LocalSettings settings)
    {
        settings.ProtectedPaths = NormalizePaths(settings.ProtectedPaths ?? new List<string>());
        if (settings.ApplicationProtectionRules is not null)
            SynchronizeLegacyFields(settings);
    }

    private static List<ApplicationProtectionRule> FromLegacy(
        IEnumerable<string>? protectedPaths,
        bool protectRelatedProcesses) =>
        NormalizePaths(protectedPaths ?? Array.Empty<string>())
            .Select(path => new ApplicationProtectionRule
            {
                ApplicationExecutablePath = path,
                ProtectEntireFamily = protectRelatedProcesses,
                ProtectedExecutablePaths = protectRelatedProcesses
                    ? new List<string>()
                    : new List<string> { path }
            })
            .ToList();

    private static List<ApplicationProtectionRule> NormalizeRules(
        IEnumerable<ApplicationProtectionRule>? rules)
    {
        var result = new List<ApplicationProtectionRule>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in (rules ?? Array.Empty<ApplicationProtectionRule>())
                     .Where(rule => rule is not null))
        {
            if (!ExecutablePathIdentity.TryNormalize(
                    candidate.ApplicationExecutablePath,
                    out var applicationPath))
            {
                continue;
            }

            var protectedPaths = NormalizePaths(candidate.ProtectedExecutablePaths ?? new List<string>());
            if (!candidate.ProtectEntireFamily && protectedPaths.Count == 0) continue;

            if (!indexes.TryGetValue(applicationPath, out var index))
            {
                indexes[applicationPath] = result.Count;
                result.Add(new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = applicationPath,
                    ProtectEntireFamily = candidate.ProtectEntireFamily,
                    ProtectedExecutablePaths = candidate.ProtectEntireFamily
                        ? new List<string>()
                        : protectedPaths
                });
                continue;
            }

            var existing = result[index];
            if (existing.ProtectEntireFamily || candidate.ProtectEntireFamily)
            {
                existing.ProtectEntireFamily = true;
                existing.ProtectedExecutablePaths.Clear();
                continue;
            }

            existing.ProtectedExecutablePaths = NormalizePaths(
                existing.ProtectedExecutablePaths.Concat(protectedPaths));
        }
        return result;
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (ExecutablePathIdentity.TryNormalize(path, out var normalized) && seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }
}

public static class ApplicationOptimizationRuleSettings
{
    public static IReadOnlyList<ApplicationOptimizationRule> Resolve(LocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ApplicationOptimizationRulePolicy.NormalizeRules(settings.ApplicationOptimizationRules);
    }

    public static void Replace(
        LocalSettings settings,
        IEnumerable<ApplicationOptimizationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rules);
        settings.ApplicationOptimizationRules = ApplicationOptimizationRulePolicy
            .NormalizeRules(rules)
            .Select(Clone)
            .ToList();
    }

    public static void Remove(LocalSettings settings, string ruleId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(ruleId)) return;
        Replace(settings, Resolve(settings).Where(rule =>
            !string.Equals(rule.Id, ruleId, StringComparison.OrdinalIgnoreCase)));
    }

    internal static void Normalize(LocalSettings settings)
    {
        if (settings.ApplicationOptimizationRules is null) return;
        settings.ApplicationOptimizationRules = ApplicationOptimizationRulePolicy
            .NormalizeRules(settings.ApplicationOptimizationRules)
            .Select(Clone)
            .ToList();
    }

    private static ApplicationOptimizationRule Clone(ApplicationOptimizationRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Targets = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Select(target => new ApplicationOptimizationRuleTarget
            {
                TargetType = target.TargetType,
                Path = target.Path,
                ExecutablePaths = target.ExecutablePaths?.ToList() ?? new List<string>(),
                BypassProtectionConfirmed = target.BypassProtectionConfirmed
            })
            .ToList(),
        TriggerMode = rule.TriggerMode,
        DelayTriggerEnabled = rule.DelayTriggerEnabled,
        DelayAnchor = rule.DelayAnchor,
        DelayMinutes = rule.DelayMinutes,
        ExecutionCount = rule.ExecutionCount,
        ExecutionIntervalMinutes = rule.ExecutionIntervalMinutes,
        RepeatIndefinitely = rule.RepeatIndefinitely,
        RestartWithApplication = rule.RestartWithApplication,
        WorkingSetTriggerEnabled = rule.WorkingSetTriggerEnabled,
        WorkingSetThresholdFollowsProfile = rule.WorkingSetThresholdFollowsProfile,
        WorkingSetThresholdBytes = rule.WorkingSetThresholdBytes,
        CooldownMinutes = rule.CooldownMinutes,
        ConfigurationRevision = rule.ConfigurationRevision,
        BypassProtection = rule.BypassProtection
    };
}

public sealed record LocalSettingsTransactionResult(
    LocalSettings Settings,
    Exception? Error)
{
    public bool Succeeded => Error is null;
}

public static class LocalSettingsTransaction
{
    public static LocalSettingsTransactionResult TryCommit(
        LocalSettings current,
        Action<LocalSettings> mutate,
        Action<LocalSettings> persist)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(persist);

        try
        {
            var candidate = current.DeepClone();
            mutate(candidate);
            persist(candidate);
            return new LocalSettingsTransactionResult(candidate, null);
        }
        catch (Exception exception)
        {
            return new LocalSettingsTransactionResult(current, exception);
        }
    }
}

public static class CustomProfileSettingsOperations
{
    public static CustomOptimizationProfile AddCopy(
        LocalSettings settings,
        OptimizationProfile baseProfile,
        string name)
    {
        return AddCopy(
            settings,
            CustomProfilePolicy.Create(baseProfile, name, settings.CustomProfiles.Count),
            name);
    }

    public static CustomOptimizationProfile AddCopy(
        LocalSettings settings,
        CustomOptimizationProfile source,
        string name)
    {
        if (settings.CustomProfiles.Count >= CustomProfilePolicy.MaximumCustomProfiles)
            throw new InvalidOperationException("The custom profile limit has been reached.");

        var profile = CustomProfilePolicy.Copy(source, name, settings.CustomProfiles.Count);
        settings.CustomProfiles.Add(profile);
        return profile;
    }

    public static bool Remove(LocalSettings settings, string profileId)
    {
        var removed = settings.CustomProfiles.RemoveAll(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return false;

        for (var index = 0; index < settings.CustomProfiles.Count; index++)
            settings.CustomProfiles[index].SortOrder = index;
        if (string.Equals(settings.ActiveCustomProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            settings.ActiveCustomProfileId = null;
        if (settings.CustomProfiles.Count == 0)
        {
            settings.ShowBuiltInProfiles = true;
        }
        else if (!settings.ShowBuiltInProfiles && settings.ActiveCustomProfile is null)
        {
            settings.ActiveCustomProfileId = settings.CustomProfiles
                .OrderBy(profile => profile.SortOrder)
                .First()
                .Id;
        }
        return true;
    }
}

public static class CustomStableStateSuppressionProfileSettingsOperations
{
    public static CustomStableStateSuppressionProfile AddCopy(
        LocalSettings settings,
        OptimizationProfile baseProfile,
        string name)
    {
        return AddCopy(
            settings,
            CustomStableStateSuppressionProfilePolicy.Create(
                baseProfile,
                name,
                settings.CustomStableStateSuppressionProfiles.Count),
            name);
    }

    public static CustomStableStateSuppressionProfile AddCopy(
        LocalSettings settings,
        CustomStableStateSuppressionProfile source,
        string name)
    {
        if (settings.CustomStableStateSuppressionProfiles.Count >=
            CustomStableStateSuppressionProfilePolicy.MaximumCustomProfiles)
        {
            throw new InvalidOperationException("The custom steady-state profile limit has been reached.");
        }

        var profile = CustomStableStateSuppressionProfilePolicy.Copy(
            source,
            name,
            settings.CustomStableStateSuppressionProfiles.Count);
        settings.CustomStableStateSuppressionProfiles.Add(profile);
        return profile;
    }

    public static bool Remove(LocalSettings settings, string profileId)
    {
        var removed = settings.CustomStableStateSuppressionProfiles.RemoveAll(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return false;

        for (var index = 0; index < settings.CustomStableStateSuppressionProfiles.Count; index++)
            settings.CustomStableStateSuppressionProfiles[index].SortOrder = index;
        if (string.Equals(
                settings.ActiveCustomStableStateSuppressionProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveCustomStableStateSuppressionProfileId = null;
        }
        if (settings.CustomStableStateSuppressionProfiles.Count == 0)
        {
            settings.ShowBuiltInStableStateSuppressionProfiles = true;
            if (settings.StableStateSuppressionMode == StableStateSuppressionMode.Custom)
                settings.StableStateSuppressionMode = StableStateSuppressionMode.FollowBaseProfile;
        }
        else if (settings.StableStateSuppressionMode == StableStateSuppressionMode.Custom &&
                 settings.ActiveCustomStableStateSuppressionProfile is null)
        {
            settings.ActiveCustomStableStateSuppressionProfileId = settings
                .CustomStableStateSuppressionProfiles
                .OrderBy(profile => profile.SortOrder)
                .First()
                .Id;
        }
        else if (!settings.ShowBuiltInStableStateSuppressionProfiles &&
                 settings.ActiveCustomStableStateSuppressionProfile is null)
        {
            settings.ActiveCustomStableStateSuppressionProfileId = settings
                .CustomStableStateSuppressionProfiles
                .OrderBy(profile => profile.SortOrder)
                .First()
                .Id;
            settings.StableStateSuppressionMode = StableStateSuppressionMode.Custom;
        }
        return true;
    }
}

public static class MemoryTriggerPresentation
{
    public static int ToUsagePercent(int availablePercent) =>
        100 - Math.Clamp(availablePercent, 1, 95);

    public static int ToAvailablePercent(double usagePercent) =>
        100 - (int)Math.Round(Math.Clamp(usagePercent, 5, 99));

    public static (double Minimum, double Maximum) UsageBounds(
        double minimumAvailablePercent,
        double maximumAvailablePercent) =>
        (100 - maximumAvailablePercent, 100 - minimumAvailablePercent);
}

public static class ScheduledOptimizationPolicy
{
    public const int DefaultIntervalMinutes = 60;
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 1440;
    public static readonly IReadOnlyList<int> PresetIntervals = new[] { 1, 2, 5, 10, 15, 30, 60 };

    public static int NormalizeInterval(int minutes) =>
        minutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes
            ? minutes
            : DefaultIntervalMinutes;

    public static bool IsDue(DateTimeOffset anchor, DateTimeOffset now, int intervalMinutes) =>
        now - anchor >= TimeSpan.FromMinutes(NormalizeInterval(intervalMinutes));

    public static bool IsUnavailable(bool autoOptimizationEnabled, bool ignoresMemoryPressure) =>
        autoOptimizationEnabled && ignoresMemoryPressure;
}

public static class GlobalReclaimSchedulePolicy
{
    public const int DefaultIntervalMinutes = 60;
    public const int DefaultStartupDelayMinutes = 5;
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 1440;
    public const int MinimumStartupDelayMinutes = 0;
    public const int MaximumStartupDelayMinutes = 1440;

    public static int NormalizeInterval(int minutes) =>
        minutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes
            ? minutes
            : DefaultIntervalMinutes;

    public static int NormalizeStartupDelay(int minutes) =>
        minutes is >= MinimumStartupDelayMinutes and <= MaximumStartupDelayMinutes
            ? minutes
            : DefaultStartupDelayMinutes;
}

public static class LongIdleOptimizationPolicy
{
    public const int DefaultMinutes = 60;
    public const int MinimumMinutes = 30;
    public const int MaximumMinutes = 360;
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(30);

    public static int NormalizeMinutes(int minutes) =>
        Math.Clamp(minutes, MinimumMinutes, MaximumMinutes);

    public static bool IsDue(
        DateTimeOffset lastSuccessfulOptimizationAt,
        DateTimeOffset now,
        int minutes) =>
        now - lastSuccessfulOptimizationAt >= TimeSpan.FromMinutes(NormalizeMinutes(minutes));

    public static bool CanEvaluate(DateTimeOffset? lastEvaluationAt, DateTimeOffset now) =>
        !lastEvaluationAt.HasValue || now - lastEvaluationAt.Value >= EvaluationInterval;
}

public static class AutomaticOptimizationSafetyWindow
{
    public static bool CanRun(
        DateTimeOffset? anchor,
        DateTimeOffset now,
        TimeSpan cooldown) =>
        !anchor.HasValue || now - anchor.Value >= cooldown;

    public static bool ShouldStart(bool manual, bool scheduled) =>
        !manual || scheduled;
}

public static class SystemThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}

public sealed class LocalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private bool _writesBlocked;

    public LocalSettingsStore(string? path = null)
    {
        _path = path ?? AppDataPaths.SettingsFile;
    }

    public string SettingsFile => _path;

    public LocalSettings Load() => LoadWithStatus().Settings;

    public LocalSettingsLoadResult LoadWithStatus()
    {
        if (!File.Exists(_path))
            return new LocalSettingsLoadResult(Normalize(new LocalSettings()), false, null);

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ??
                throw new InvalidDataException("Settings must be a JSON object.");
            var sourceVersion = ReadVersion(root, "SettingsVersion");
            if (sourceVersion > SettingsSchema.CurrentVersion)
                throw new InvalidDataException($"Settings version {sourceVersion} is newer than supported version {SettingsSchema.CurrentVersion}.");

            var migrated = sourceVersion < SettingsSchema.CurrentVersion;
            if (migrated)
            {
                File.Copy(_path, _path + ".bak", true);
                Migrate(root, sourceVersion);
            }

            var settings = Normalize(root.Deserialize<LocalSettings>(JsonOptions) ?? new LocalSettings());
            if (migrated) Save(settings);
            DeleteMigrationBackup();
            return new LocalSettingsLoadResult(settings, migrated, null);
        }
        catch (Exception exception)
        {
            _writesBlocked = true;
            return new LocalSettingsLoadResult(Normalize(new LocalSettings()), false, exception.Message);
        }
    }

    public string LoadLanguageCode()
    {
        try
        {
            if (!File.Exists(_path)) return "zh-CN";
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            var code = root?["LanguageCode"]?.GetValue<string>();
            return UiLanguageCatalog.ToCode(UiLanguageCatalog.FromCode(code));
        }
        catch
        {
            return "zh-CN";
        }
    }

    public void Save(LocalSettings settings)
    {
        if (_writesBlocked)
            throw new InvalidOperationException("Settings cannot be saved because the existing file could not be loaded safely.");
        ApplicationProtectionSettings.Normalize(settings);
        ApplicationOptimizationRuleSettings.Normalize(settings);
        settings.SettingsVersion = SettingsSchema.CurrentVersion;
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
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
            // A stale migration backup must not prevent valid settings from loading.
        }
    }

    private static int ReadVersion(JsonObject root, string propertyName) =>
        root[propertyName] is null ? 0 : root[propertyName]!.GetValue<int>();

    private static void Migrate(JsonObject root, int sourceVersion)
    {
        for (var version = sourceVersion; version < SettingsSchema.CurrentVersion; version++)
        {
            switch (version)
            {
                case 0:
                    root["SettingsVersion"] = 1;
                    break;
                case 1:
                    MigrateStableStateSuppression(root);
                    root["SettingsVersion"] = 2;
                    break;
                case 2:
                    MigrateCustomStableStateSuppressionProfiles(root);
                    root["SettingsVersion"] = 3;
                    break;
                case 3:
                    root[nameof(LocalSettings.ApplicationOptimizationRules)] ??=
                        new JsonArray();
                    root["SettingsVersion"] = 4;
                    break;
                case 4:
                    root[nameof(LocalSettings.StableAnchorSettings)] ??= new JsonArray();
                    root["SettingsVersion"] = 5;
                    break;
                case 5:
                    MigrateStableValidationSettings(root);
                    root["SettingsVersion"] = 6;
                    break;
                case 6:
                    root[nameof(LocalSettings.FirstBootStableReviewLaunches)] ??= new JsonArray();
                    root["SettingsVersion"] = 7;
                    break;
                default:
                    throw new InvalidDataException($"No settings migration is available from version {version}.");
            }
        }
    }

    private static void MigrateStableStateSuppression(JsonObject root)
    {
        var mode = root[nameof(LocalSettings.StableStateSuppressionMode)]?.GetValue<int>() ??
                   (int)StableStateSuppressionMode.FollowBaseProfile;
        StableStateSuppressionSettings? migrated = mode switch
        {
            (int)StableStateSuppressionMode.ReduceRepeatedOptimization =>
                StableStateSuppressionSettings.For(OptimizationProfile.Lite),
            (int)StableStateSuppressionMode.Balanced =>
                StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            (int)StableStateSuppressionMode.FasterReevaluation =>
                StableStateSuppressionSettings.For(OptimizationProfile.Ultimate),
            _ => null
        };

        if (mode == (int)StableStateSuppressionMode.FollowBaseProfile &&
            root[nameof(LocalSettings.ActiveCustomProfileId)]?.GetValue<string>() is { } activeId &&
            root[nameof(LocalSettings.CustomProfiles)] is JsonArray profiles)
        {
            migrated = profiles
                .OfType<JsonObject>()
                .FirstOrDefault(profile => string.Equals(
                    profile[nameof(CustomOptimizationProfile.Id)]?.GetValue<string>(),
                    activeId,
                    StringComparison.OrdinalIgnoreCase))?[nameof(CustomOptimizationProfile.StableStateSuppression)]
                ?.Deserialize<StableStateSuppressionSettings>(JsonOptions);
        }

        root[nameof(LocalSettings.CustomStableStateSuppression)] = JsonSerializer.SerializeToNode(
            (migrated ?? StableStateSuppressionSettings.For(OptimizationProfile.Turbo)).Normalize(),
            JsonOptions);
        if (migrated is not null)
            root[nameof(LocalSettings.StableStateSuppressionMode)] = (int)StableStateSuppressionMode.Custom;
    }

    private static void MigrateCustomStableStateSuppressionProfiles(JsonObject root)
    {
        var mode = root[nameof(LocalSettings.StableStateSuppressionMode)]?.GetValue<int>() ??
                   (int)StableStateSuppressionMode.FollowBaseProfile;
        if (mode != (int)StableStateSuppressionMode.Custom) return;

        var settings = root[nameof(LocalSettings.CustomStableStateSuppression)]
            ?.Deserialize<StableStateSuppressionSettings>(JsonOptions) ??
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var languageCode = root[nameof(LocalSettings.LanguageCode)]?.GetValue<string>() ?? "zh-CN";
        var profile = CustomStableStateSuppressionProfilePolicy.Create(
            OptimizationProfile.Turbo,
            languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "自定义稳态"
                : "Custom steady state",
            0);
        profile.Settings = settings.Normalize();
        root[nameof(LocalSettings.CustomStableStateSuppressionProfiles)] =
            JsonSerializer.SerializeToNode(new[] { profile }, JsonOptions);
        root[nameof(LocalSettings.ActiveCustomStableStateSuppressionProfileId)] = profile.Id;
    }

    private static void MigrateStableValidationSettings(JsonObject root)
    {
        MigrateStableValidationSettingsObject(
            root[nameof(LocalSettings.CustomStableStateSuppression)] as JsonObject,
            OptimizationProfile.Turbo);
        if (root[nameof(LocalSettings.CustomStableStateSuppressionProfiles)] is JsonArray stableProfiles)
        {
            foreach (var profile in stableProfiles.OfType<JsonObject>())
            {
                var baseProfile = ReadBaseProfile(profile);
                MigrateStableValidationSettingsObject(
                    profile[nameof(CustomStableStateSuppressionProfile.Settings)] as JsonObject,
                    baseProfile);
            }
        }
        if (root[nameof(LocalSettings.CustomProfiles)] is JsonArray optimizationProfiles)
        {
            foreach (var profile in optimizationProfiles.OfType<JsonObject>())
            {
                var baseProfile = ReadBaseProfile(profile);
                MigrateStableValidationSettingsObject(
                    profile[nameof(CustomOptimizationProfile.StableStateSuppression)] as JsonObject,
                    baseProfile);
            }
        }
    }

    private static OptimizationProfile ReadBaseProfile(JsonObject profile)
    {
        var value = profile[nameof(CustomStableStateSuppressionProfile.BaseProfile)]?.GetValue<int>() ??
                    (int)OptimizationProfile.Turbo;
        return Enum.IsDefined(typeof(OptimizationProfile), value)
            ? (OptimizationProfile)value
            : OptimizationProfile.Turbo;
    }

    private static void MigrateStableValidationSettingsObject(
        JsonObject? settings,
        OptimizationProfile baseProfile)
    {
        if (settings is null) return;
        const string legacyObservationWindow = "NaturalStableObservationWindow";
        var maximumValidationProperty = nameof(StableStateSuppressionSettings.MaximumStableValidationDuration);
        settings[maximumValidationProperty] ??= settings[legacyObservationWindow]?.DeepClone() ??
            JsonSerializer.SerializeToNode(
                StableStateSuppressionSettings.For(baseProfile).MaximumStableValidationDuration,
                JsonOptions);
        settings.Remove(legacyObservationWindow);
        settings[nameof(StableStateSuppressionSettings.IgnoreRegularObservationUnderSeverePressure)] ??=
            JsonValue.Create(baseProfile == OptimizationProfile.Ultimate);
    }

    private static LocalSettings Normalize(LocalSettings settings)
    {
        settings.SettingsVersion = SettingsSchema.CurrentVersion;
        if (!Enum.IsDefined(settings.Profile)) settings.Profile = OptimizationProfile.Turbo;
        if (!Enum.IsDefined(settings.CloseButtonBehavior)) settings.CloseButtonBehavior = CloseButtonBehavior.Ask;
        if (!Enum.IsDefined(settings.StableStateSuppressionMode))
            settings.StableStateSuppressionMode = StableStateSuppressionMode.FollowBaseProfile;
        settings.CustomStableStateSuppression = (settings.CustomStableStateSuppression ??
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo)).Normalize();
        settings.CustomStableStateSuppressionProfiles = NormalizeCustomStableStateSuppressionProfiles(
            settings.CustomStableStateSuppressionProfiles);
        settings.StableAnchorSettings = NormalizeStableAnchorSettings(settings.StableAnchorSettings);
        if (settings.CustomStableStateSuppressionProfiles.Count == 0)
        {
            settings.ActiveCustomStableStateSuppressionProfileId = null;
            settings.ShowBuiltInStableStateSuppressionProfiles = true;
            if (settings.StableStateSuppressionMode == StableStateSuppressionMode.Custom)
                settings.StableStateSuppressionMode = StableStateSuppressionMode.FollowBaseProfile;
        }
        else if (settings.ActiveCustomStableStateSuppressionProfileId is not null &&
                 settings.ActiveCustomStableStateSuppressionProfile is null)
        {
            settings.ActiveCustomStableStateSuppressionProfileId = null;
        }
        if (settings.StableStateSuppressionMode == StableStateSuppressionMode.Custom &&
            settings.ActiveCustomStableStateSuppressionProfile is null)
        {
            settings.ActiveCustomStableStateSuppressionProfileId = settings
                .CustomStableStateSuppressionProfiles[0]
                .Id;
        }
        if (!settings.ShowBuiltInStableStateSuppressionProfiles &&
            settings.StableStateSuppressionMode is
                StableStateSuppressionMode.ReduceRepeatedOptimization or
                StableStateSuppressionMode.Balanced or
                StableStateSuppressionMode.FasterReevaluation)
        {
            settings.ActiveCustomStableStateSuppressionProfileId ??=
                settings.CustomStableStateSuppressionProfiles[0].Id;
            settings.StableStateSuppressionMode = StableStateSuppressionMode.Custom;
        }
        settings.ScheduledOptimizationIntervalMinutes = ScheduledOptimizationPolicy.NormalizeInterval(
            settings.ScheduledOptimizationIntervalMinutes);
        settings.GlobalReclaimIntervalMinutes = GlobalReclaimSchedulePolicy.NormalizeInterval(
            settings.GlobalReclaimIntervalMinutes);
        settings.GlobalReclaimStartupDelayMinutes = GlobalReclaimSchedulePolicy.NormalizeStartupDelay(
            settings.GlobalReclaimStartupDelayMinutes);
        settings.LongIdleOptimizationMinutes = LongIdleOptimizationPolicy.NormalizeMinutes(
            settings.LongIdleOptimizationMinutes);
        settings.CandidateDisplayLimit = CandidateDisplayLimitPolicy.Normalize(settings.CandidateDisplayLimit);
        settings.LanguageCode = UiLanguageCatalog.ToCode(UiLanguageCatalog.FromCode(settings.LanguageCode));
        if (!Enum.IsDefined(settings.UpdateCheckFrequency))
            settings.UpdateCheckFrequency = UpdateCheckFrequency.EveryStartup;
        if (string.IsNullOrWhiteSpace(settings.UpdateFeedUrl))
            settings.UpdateFeedUrl = UpdateConfiguration.FeedUrl;
        settings.SuppressedUpdateVersion = settings.SuppressedUpdateVersion?.Trim() ?? string.Empty;
        settings.FirstBootStableReviewBootUtcTicks = Math.Max(0, settings.FirstBootStableReviewBootUtcTicks);
        settings.FirstBootStableReviewLaunches = (settings.FirstBootStableReviewLaunches ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(512)
            .ToList();
        settings.CustomProfiles = NormalizeCustomProfiles(settings.CustomProfiles);
        if (settings.CustomProfiles.Count == 0)
        {
            settings.ActiveCustomProfileId = null;
            settings.ShowBuiltInProfiles = true;
        }
        else if (settings.ActiveCustomProfileId is not null && settings.ActiveCustomProfile is null)
        {
            settings.ActiveCustomProfileId = null;
        }
        if (!settings.ShowBuiltInProfiles && settings.ActiveCustomProfile is null)
        {
            settings.ActiveCustomProfileId = settings.CustomProfiles[0].Id;
        }
        ApplicationProtectionSettings.Normalize(settings);
        ApplicationOptimizationRuleSettings.Normalize(settings);
        return settings;
    }

    private static List<ApplicationStableAnchorSetting> NormalizeStableAnchorSettings(
        IEnumerable<ApplicationStableAnchorSetting>? anchors) =>
        (anchors ?? Array.Empty<ApplicationStableAnchorSetting>())
        .Where(anchor => !string.IsNullOrWhiteSpace(anchor.FamilyKey) &&
                         !string.IsNullOrWhiteSpace(anchor.ScopeKey) &&
                         Enum.IsDefined(anchor.Mode) &&
                         anchor.FixedAnchorBytes >= 0)
        .GroupBy(anchor => anchor.ScopeKey.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Last() with
        {
            FamilyKey = group.Last().FamilyKey.Trim(),
            ScopeKey = group.Key
        })
        .Take(250)
        .ToList();

    private static List<CustomOptimizationProfile> NormalizeCustomProfiles(
        IEnumerable<CustomOptimizationProfile>? profiles)
    {
        var result = new List<CustomOptimizationProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in (profiles ?? Array.Empty<CustomOptimizationProfile>())
                     .Where(profile => profile is not null)
                     .OrderBy(profile => profile.SortOrder))
        {
            if (result.Count >= CustomProfilePolicy.MaximumCustomProfiles) break;
            var normalized = CustomProfilePolicy.Normalize(candidate);
            if (!ids.Add(normalized.Id) ||
                !CustomProfilePolicy.IsUniqueName(result, normalized.Name))
            {
                continue;
            }
            normalized.SortOrder = result.Count;
            result.Add(normalized);
        }
        return result;
    }

    private static List<CustomStableStateSuppressionProfile> NormalizeCustomStableStateSuppressionProfiles(
        IEnumerable<CustomStableStateSuppressionProfile>? profiles)
    {
        var result = new List<CustomStableStateSuppressionProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in (profiles ?? Array.Empty<CustomStableStateSuppressionProfile>())
                     .Where(profile => profile is not null)
                     .OrderBy(profile => profile.SortOrder))
        {
            if (result.Count >= CustomStableStateSuppressionProfilePolicy.MaximumCustomProfiles) break;
            var normalized = CustomStableStateSuppressionProfilePolicy.Normalize(candidate);
            if (!ids.Add(normalized.Id) ||
                !CustomStableStateSuppressionProfilePolicy.IsUniqueName(result, normalized.Name))
            {
                continue;
            }
            normalized.SortOrder = result.Count;
            result.Add(normalized);
        }
        return result;
    }
}

public static class AppDataPaths
{
    public static string RootDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);
    public static string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");

    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(RootDirectory, "history.json");
    public static string BenefitLearningFile => Path.Combine(RootDirectory, "benefit-learning.json");
    public static string RuntimeProgressFile => Path.Combine(RootDirectory, "runtime-state.json");
    public static string ReboundHistoryFile => Path.Combine(RootDirectory, "rebound-history.json");
    public static string CalibrationMetricsFile => Path.Combine(DiagnosticsDirectory, "calibration-metrics.jsonl");
    public static string DiagnosticLogFile => Path.Combine(DiagnosticsDirectory, "museram.log");

    public static void MigrateLegacyAuxiliaryFiles(string? rootDirectory = null)
    {
        var root = Path.GetFullPath(rootDirectory ?? RootDirectory);
        var diagnostics = Path.Combine(root, "diagnostics");
        var legacyLogs = Path.Combine(root, "logs");
        MoveIfDestinationMissing(
            Path.Combine(root, "calibration-metrics.jsonl"),
            Path.Combine(diagnostics, "calibration-metrics.jsonl"));
        MoveIfDestinationMissing(
            Path.Combine(root, "calibration-metrics.jsonl.previous"),
            Path.Combine(diagnostics, "calibration-metrics.jsonl.previous"));
        MoveIfDestinationMissing(
            Path.Combine(legacyLogs, "museram.log"),
            Path.Combine(diagnostics, "museram.log"));
        MoveIfDestinationMissing(
            Path.Combine(legacyLogs, "museram.log.previous"),
            Path.Combine(diagnostics, "museram.log.previous"));
        try
        {
            if (Directory.Exists(legacyLogs) && !Directory.EnumerateFileSystemEntries(legacyLogs).Any())
                Directory.Delete(legacyLogs);
        }
        catch
        {
            // Auxiliary migration is best-effort and must not block startup.
        }
    }

    private static void MoveIfDestinationMissing(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(destination)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        catch
        {
            // The old file remains usable for manual recovery if migration cannot complete.
        }
    }
}

public sealed record StartupPreferenceTransactionResult(
    bool Succeeded,
    Exception? RegistrationError,
    Exception? PersistenceError,
    Exception? CompensationError);

public static class StartupPreferenceTransaction
{
    public static StartupPreferenceTransactionResult TryCommit(
        bool previous,
        bool requested,
        Action<bool> apply,
        Func<bool> readEnabled,
        Action<bool> persist)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(readEnabled);
        ArgumentNullException.ThrowIfNull(persist);

        try
        {
            ApplyAndVerify(requested, apply, readEnabled);
        }
        catch (Exception exception)
        {
            return Compensate(previous, apply, readEnabled, exception, persistenceError: null);
        }

        try
        {
            persist(requested);
            return new StartupPreferenceTransactionResult(true, null, null, null);
        }
        catch (Exception exception)
        {
            return Compensate(previous, apply, readEnabled, registrationError: null, exception);
        }
    }

    private static StartupPreferenceTransactionResult Compensate(
        bool previous,
        Action<bool> apply,
        Func<bool> readEnabled,
        Exception? registrationError,
        Exception? persistenceError)
    {
        try
        {
            ApplyAndVerify(previous, apply, readEnabled);
            return new StartupPreferenceTransactionResult(
                false,
                registrationError,
                persistenceError,
                null);
        }
        catch (Exception exception)
        {
            return new StartupPreferenceTransactionResult(
                false,
                registrationError,
                persistenceError,
                exception);
        }
    }

    private static void ApplyAndVerify(
        bool enabled,
        Action<bool> apply,
        Func<bool> readEnabled)
    {
        apply(enabled);
        if (readEnabled() != enabled)
        {
            throw new InvalidOperationException(enabled
                ? "Windows did not retain the MuseRAM logon task."
                : "Windows did not remove the MuseRAM logon task.");
        }
    }
}

public static class StartupRegistration
{
    private const string TaskName = "MuseRAM";
    private const string LegacyValueName = "MuseRAM";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = StartupTaskValidationPolicy.LogonTriggerType;
    private const int TaskActionExecute = 0;

    public static void SetEnabled(bool enabled)
    {
        RemoveLegacyRunEntry();
        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException("MuseRAM executable path could not be resolved.");
            }

            RegisterTask(StartupLaunchPolicy.CreateTaskSpec(executablePath, CurrentUserId()));
        }
        else
        {
            DeleteTask();
        }
    }

    public static bool RepairEnabledPath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("MuseRAM executable path could not be resolved.");

        RemoveLegacyRunEntry();
        var spec = StartupLaunchPolicy.CreateTaskSpec(executablePath, CurrentUserId());
        if (IsTaskCurrent(spec)) return false;
        RegisterTask(spec);
        return true;
    }

    public static bool IsEnabled()
    {
        var executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executablePath) && IsTaskCurrent(
            StartupLaunchPolicy.CreateTaskSpec(executablePath, CurrentUserId()));
    }

    private static void RegisterTask(StartupTaskSpec spec)
    {
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? definition = null;
        try
        {
            service = CreateTaskService();
            service.Connect();
            folder = service.GetFolder("\\");
            definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "Start MuseRAM in the notification area when this user signs in.";
            definition.Principal.UserId = spec.UserId;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunLevelHighest;
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;

            dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = spec.UserId;
            dynamic action = definition.Actions.Create(TaskActionExecute);
            action.Path = spec.ExecutablePath;
            action.Arguments = spec.Arguments;
            action.WorkingDirectory = spec.WorkingDirectory;

            folder.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreateOrUpdate,
                spec.UserId,
                null,
                TaskLogonInteractiveToken,
                null);
        }
        finally
        {
            ReleaseComObject(definition);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static bool IsTaskCurrent(StartupTaskSpec expected)
    {
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            service = CreateTaskService();
            service.Connect();
            folder = service.GetFolder("\\");
            task = folder.GetTask(TaskName);
            dynamic definition = task.Definition;
            dynamic action = definition.Actions.Item(1);
            dynamic trigger = definition.Triggers.Item(1);
            return task.Enabled &&
                string.Equals((string?)action.Path, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(((string?)action.Arguments)?.Trim(), expected.Arguments, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)action.WorkingDirectory, expected.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
                StartupLaunchPolicy.UserIdsReferToSameAccount(
                    (string?)definition.Principal.UserId,
                    expected.UserId) &&
                (int)definition.Principal.LogonType == TaskLogonInteractiveToken &&
                (int)definition.Principal.RunLevel == TaskRunLevelHighest &&
                StartupTaskValidationPolicy.IsExpectedLogonTrigger(
                    (int)trigger.Type,
                    (string?)trigger.UserId,
                    expected.UserId);
        }
        catch (COMException exception) when ((uint)exception.HResult == 0x80070002)
        {
            return false;
        }
        catch (FileNotFoundException exception) when ((uint)exception.HResult == 0x80070002)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void DeleteTask()
    {
        dynamic? service = null;
        dynamic? folder = null;
        try
        {
            service = CreateTaskService();
            service.Connect();
            folder = service.GetFolder("\\");
            folder.DeleteTask(TaskName, 0);
        }
        catch (COMException exception) when ((uint)exception.HResult == 0x80070002)
        {
        }
        catch (FileNotFoundException exception) when ((uint)exception.HResult == 0x80070002)
        {
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static dynamic CreateTaskService() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ??
            throw new PlatformNotSupportedException("Windows Task Scheduler is unavailable."))!;

    private static string CurrentUserId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? identity.Name;
    }

    private static void RemoveLegacyRunEntry()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(LegacyValueName, false);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

public sealed record StartupTaskSpec(
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string UserId,
    bool RunWithHighestPrivileges,
    bool InteractiveLogonOnly);

public static class StartupTaskValidationPolicy
{
    public const int LogonTriggerType = 9;

    public static bool IsExpectedLogonTrigger(
        int triggerType,
        string? triggerUserId,
        string expectedUserId) =>
        triggerType == LogonTriggerType &&
        StartupLaunchPolicy.UserIdsReferToSameAccount(triggerUserId, expectedUserId);
}

public static class StartupLaunchPolicy
{
    public const string BackgroundArgument = "--background";

    public static bool ShouldStartHidden(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(
            argument,
            BackgroundArgument,
            StringComparison.OrdinalIgnoreCase));

    public static bool UserIdsReferToSameAccount(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        var leftSid = TryResolveSid(left);
        var rightSid = TryResolveSid(right);
        return leftSid is not null && rightSid is not null && leftSid.Equals(rightSid);
    }

    private static SecurityIdentifier? TryResolveSid(string userId)
    {
        try
        {
            var value = userId.Trim();
            if (value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                return new SecurityIdentifier(value);
            return (SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static StartupTaskSpec CreateTaskSpec(string executablePath, string userId)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User identity is required.", nameof(userId));
        }

        var fullPath = Path.GetFullPath(executablePath.Trim());
        return new StartupTaskSpec(
            fullPath,
            BackgroundArgument,
            Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            userId.Trim(),
            RunWithHighestPrivileges: true,
            InteractiveLogonOnly: true);
    }
}
