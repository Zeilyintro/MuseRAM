using MuseRAM.App;
using MuseRAM.Core;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MuseRAM.App.Tests;

public sealed class WindowThemeServiceTests
{
    [Fact]
    public void NativeAnimationStyleKeepsCaptionAndSystemCapabilities()
    {
        const long caption = 0x00C00000L;
        const long systemButtons = 0x00080000L | 0x00020000L | 0x00010000L;

        var style = WindowThemeService.NativeAnimationStyle(systemButtons);

        Assert.Equal(caption, style & caption);
        Assert.Equal(systemButtons, style & systemButtons);
    }

    [Fact]
    public void CalibrationMetricsStoreAnonymizesStableStateObservationIdentities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"museram-stable-{Guid.NewGuid():N}.jsonl");
        try
        {
            var context = new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo,
                OptimizationTriggerKind.Automatic, "test") { RunId = "stable-run" };
            var store = new CalibrationMetricsStore(path);
            store.AppendStableStateObservation(new ApplicationStableObservation(
                context,
                "private-family-key",
                "private-scope-key",
                "private-launch-signature",
                DateTimeOffset.UtcNow,
                ComponentCount: 2,
                CurrentWorkingSetBytes: 400,
                PreviousWorkingSetBytes: 420,
                ConvergenceToleranceBytes: 32,
                QualityEligible: true,
                StateBefore: ApplicationStableCandidateState.Provisional,
                StateAfter: ApplicationStableCandidateState.Converged,
                Decision: ApplicationStableObservationDecision.Converged)
            {
                ReboundPercent = 12.5
            });

            var line = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("\"Kind\":\"stable-state-observation\"", line);
            Assert.Contains("\"Decision\":1", line);
            Assert.Contains("\"ComponentCount\":2", line);
            Assert.Contains("\"ReboundPercent\":12.5", line);
            Assert.DoesNotContain("private-family-key", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-scope-key", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-launch-signature", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".previous")) File.Delete(path + ".previous");
        }
    }

}

public sealed class LocalSettingsLearningPolicyTests
{
    [Fact]
    public void FollowBaseProfileUsesTheActiveCustomProfilesBaseProfile()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        profile.StableStateSuppressionMode = StableStateSuppressionMode.FasterReevaluation;
        profile.StableStateSuppression = new StableStateSuppressionSettings(
            7,
            TimeSpan.FromDays(12),
            0.42,
            77L * 1024 * 1024);
        var settings = new LocalSettings
        {
            Profile = OptimizationProfile.Turbo,
            ActiveCustomProfileId = profile.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { profile }
        };

        Assert.Equal(
            StableStateSuppressionMode.ReduceRepeatedOptimization,
            settings.ResolveStableStateSuppressionMode());
        Assert.Equal(
            StableStateSuppressionMode.FollowBaseProfile,
            settings.DeepClone().StableStateSuppressionMode);
        Assert.Equal(
            StableStateSuppressionMode.FasterReevaluation,
            settings.DeepClone().ActiveCustomProfile!.StableStateSuppressionMode);
        Assert.Equal(3, settings.ResolveStableStateSuppressionSettings()!.MinimumSamples);
        Assert.Equal(0.50, settings.ResolveStableStateSuppressionSettings()!.RelativeGrowthMargin);
        Assert.Equal(0.42, settings.DeepClone().ActiveCustomProfile!.StableStateSuppression.RelativeGrowthMargin);
    }

    [Fact]
    public void ExplicitGlobalModeOverridesTheActiveCustomProfilesLegacyMode()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        profile.StableStateSuppressionMode = StableStateSuppressionMode.ReduceRepeatedOptimization;
        var settings = new LocalSettings
        {
            ActiveCustomProfileId = profile.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { profile },
            StableStateSuppressionMode = StableStateSuppressionMode.Disabled
        };

        Assert.Equal(StableStateSuppressionMode.Disabled, settings.ResolveStableStateSuppressionMode());
        Assert.Null(settings.ResolveStableStateSuppressionSettings());
        Assert.Equal(StableStateSuppressionMode.Disabled, settings.DeepClone().StableStateSuppressionMode);
    }

    [Fact]
    public void ExplicitGlobalPresetOverridesCustomSuppressionParameters()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        profile.StableStateSuppression = new StableStateSuppressionSettings(
            7,
            TimeSpan.FromDays(12),
            0.42,
            77L * 1024 * 1024);
        var settings = new LocalSettings
        {
            ActiveCustomProfileId = profile.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { profile },
            StableStateSuppressionMode = StableStateSuppressionMode.Balanced
        };

        var resolved = settings.ResolveStableStateSuppressionSettings()!;

        Assert.Equal(3, resolved.MinimumSamples);
        Assert.Equal(0.35, resolved.RelativeGrowthMargin);
        Assert.Equal(96L * 1024 * 1024, resolved.AbsoluteGrowthMarginBytes);
    }

    [Fact]
    public void IndependentCustomModeUsesOnlyTheGlobalCustomSettings()
    {
        var settings = new LocalSettings
        {
            Profile = OptimizationProfile.Ultimate,
            StableStateSuppressionMode = StableStateSuppressionMode.Custom,
            CustomStableStateSuppression = new StableStateSuppressionSettings(
                8,
                TimeSpan.FromDays(12),
                0.42,
                77L * 1024 * 1024)
        };

        var resolved = settings.ResolveStableStateSuppressionSettings()!;

        Assert.Equal(StableStateSuppressionMode.Custom, settings.ResolveStableStateSuppressionMode());
        Assert.Equal(8, resolved.MinimumSamples);
        Assert.Equal(0.42, resolved.RelativeGrowthMargin);
    }
}

public sealed class EnhancedSafetyBehaviorTests
{
    [Fact]
    public void DefaultBehaviorUsesBaselineTimingAndDoesNotRequireSecondConfirmation()
    {
        Assert.Equal(TimeSpan.Zero, EnhancedSafetyBehavior.PostTrimSamplingDelay(enabled: false));
        Assert.Equal(TimeSpan.FromMilliseconds(900), EnhancedSafetyBehavior.DeepReleaseGracePeriod(enabled: false));
        Assert.False(EnhancedSafetyBehavior.RequiresForceTerminationConfirmation(enabled: false));
    }

    [Fact]
    public void EnhancedBehaviorRestoresMuseSafetyTimingAndConfirmation()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), EnhancedSafetyBehavior.PostTrimSamplingDelay(enabled: true));
        Assert.Equal(TimeSpan.FromSeconds(5), EnhancedSafetyBehavior.DeepReleaseGracePeriod(enabled: true));
        Assert.True(EnhancedSafetyBehavior.RequiresForceTerminationConfirmation(enabled: true));
    }
}

public sealed class MemoryTriggerPresentationTests
{
    [Fact]
    public void ConvertsAvailableMemoryThresholdToUsageThresholdWithoutChangingBehavior()
    {
        Assert.Equal(52, MemoryTriggerPresentation.ToUsagePercent(48));
        Assert.Equal(48, MemoryTriggerPresentation.ToAvailablePercent(52));
        Assert.Equal((30d, 90d), MemoryTriggerPresentation.UsageBounds(10, 70));
    }
}

public sealed class ScheduledOptimizationPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(1440)]
    public void ValidPresetAndCustomIntervalsArePreserved(int minutes)
    {
        Assert.Equal(minutes, ScheduledOptimizationPolicy.NormalizeInterval(minutes));
    }

    [Fact]
    public void InvalidIntervalFallsBackToSixtyMinutes()
    {
        Assert.Equal(60, ScheduledOptimizationPolicy.NormalizeInterval(0));
        Assert.Equal(60, ScheduledOptimizationPolicy.NormalizeInterval(1441));
    }

    [Fact]
    public void IntervalBecomesDueAtTheConfiguredBoundary()
    {
        var anchor = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

        Assert.False(ScheduledOptimizationPolicy.IsDue(anchor, anchor.AddMinutes(29), 30));
        Assert.True(ScheduledOptimizationPolicy.IsDue(anchor, anchor.AddMinutes(30), 30));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void SchedulingIsUnavailableOnlyForPressureIndependentAutomaticOptimization(
        bool autoOptimization,
        bool ignoresMemoryPressure,
        bool expected)
    {
        Assert.Equal(expected, ScheduledOptimizationPolicy.IsUnavailable(
            autoOptimization,
            ignoresMemoryPressure));
    }
}

public sealed class LongIdleOptimizationPolicyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(360, 360)]
    [InlineData(999, 360)]
    public void MinutesAreClampedToTheTestRange(int value, int expected) =>
        Assert.Equal(expected, LongIdleOptimizationPolicy.NormalizeMinutes(value));

    [Fact]
    public void BecomesDueOnlyAtTheConfiguredBoundary()
    {
        var lastSuccess = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

        Assert.False(LongIdleOptimizationPolicy.IsDue(lastSuccess, lastSuccess.AddMinutes(59), 60));
        Assert.True(LongIdleOptimizationPolicy.IsDue(lastSuccess, lastSuccess.AddMinutes(60), 60));
    }

    [Fact]
    public void ReEvaluationIsLimitedToOnceEveryThirtySeconds()
    {
        var lastEvaluation = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

        Assert.False(LongIdleOptimizationPolicy.CanEvaluate(
            lastEvaluation,
            lastEvaluation.AddSeconds(29)));
        Assert.True(LongIdleOptimizationPolicy.CanEvaluate(
            lastEvaluation,
            lastEvaluation.AddSeconds(30)));
        Assert.True(LongIdleOptimizationPolicy.CanEvaluate(null, lastEvaluation));
    }
}

public sealed class SelectedApplicationOptimizationPolicyTests
{
    [Fact]
    public void ExplicitSelectionBypassesProfileScopeButKeepsActivitySafetyThresholds()
    {
        var original = OptimizationSettings.For(OptimizationProfile.Lite) with
        {
            EnhancedSafety = true,
            QuickCandidateSelection = true
        };

        var selected = SelectedApplicationOptimizationPolicy.Apply(original);

        Assert.Equal(0, selected.MaxApplications);
        Assert.Equal(0, selected.MinimumFamilyWorkingSetBytes);
        Assert.Equal(0, selected.MinimumProcessWorkingSetBytes);
        Assert.Equal(0, selected.MinimumIdleScore);
        Assert.Equal(TimeSpan.Zero, selected.ProcessCooldown);
        Assert.Equal(TimeSpan.Zero, selected.VisibleWindowIdleDelay);
        Assert.False(selected.QuickCandidateSelection);
        Assert.Equal(original.ActiveCpuThresholdPercent, selected.ActiveCpuThresholdPercent);
        Assert.Equal(original.ActiveIoThresholdBytesPerSecond, selected.ActiveIoThresholdBytesPerSecond);
        Assert.True(selected.EnhancedSafety);
    }
}

public sealed class OptimizationResourceSamplerTests
{
    [Fact]
    public async Task CapturesBoundedCpuAndSystemPagingCounters()
    {
        Assert.NotNull(SystemPagingCounter.TryCapture());
        var sampler = OptimizationResourceSampler.Start();
        await Task.Delay(220);

        var sample = await sampler.StopAsync();

        Assert.InRange(sample.AppAverageCpuPercent, 0, 100);
        Assert.InRange(sample.AppPeakCpuPercent, 0, 100);
        Assert.NotNull(sample.SystemPageFaultCountDelta);
        Assert.NotNull(sample.SystemPageReadCountDelta);
        Assert.NotNull(sample.SystemPageReadIoCountDelta);
    }
}

public sealed class AutomaticOptimizationSafetyWindowTests
{
    [Fact]
    public void AutomaticAndScheduledRunsStartTheSafetyWindowButManualRunsDoNot()
    {
        Assert.True(AutomaticOptimizationSafetyWindow.ShouldStart(manual: false, scheduled: false));
        Assert.True(AutomaticOptimizationSafetyWindow.ShouldStart(manual: true, scheduled: true));
        Assert.False(AutomaticOptimizationSafetyWindow.ShouldStart(manual: true, scheduled: false));
    }

    [Fact]
    public void UnattendedRunWaitsForTheWholeSafetyWindow()
    {
        var anchor = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var cooldown = TimeSpan.FromSeconds(90);

        Assert.False(AutomaticOptimizationSafetyWindow.CanRun(anchor, anchor.AddSeconds(89), cooldown));
        Assert.True(AutomaticOptimizationSafetyWindow.CanRun(anchor, anchor.AddSeconds(90), cooldown));
        Assert.True(AutomaticOptimizationSafetyWindow.CanRun(null, anchor, cooldown));
    }
}

public sealed class OptimizationResultAttributionPolicyTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void SystemMemoryChangeRequiresAtLeastOneSuccessfulTrimRequest(int succeeded, bool expected)
    {
        Assert.Equal(expected, OptimizationResultAttributionPolicy.CanAttributeSystemMemoryChange(succeeded));
    }
}

public sealed class RunningProtectionCandidateCatalogTests
{
    [Fact]
    public void WholeFamilyProtectionKeepsProtectedApplicationVisibleAndFirst()
    {
        var protectedPath = @"F:\Apps\EditorSuite\Editor\EditorStudio.exe";
        var protectedFamily = Family(
            "editor",
            Process(10, "EditorStudio", protectedPath),
            Process(11, "EditorStudioHelper", @"F:\Apps\EditorSuite\Editor\EditorStudioHelper.exe", parentProcessId: 10));
        var otherFamily = Family(
            "browser",
            Process(20, "Browser", @"F:\Apps\Browser\Browser.exe"));

        var candidates = RunningProtectionCandidateCatalog.Create(
            new[] { protectedFamily, otherFamily },
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = protectedPath,
                    ProtectEntireFamily = true
                }
            });

        Assert.Equal(2, candidates.Count);
        Assert.Equal("editor", candidates[0].FamilyKey);
        Assert.Equal(ApplicationProtectionState.EntireFamily, candidates[0].ProtectionState);
        Assert.All(candidates[0].Executables, executable => Assert.True(executable.IsProtected));
        Assert.Equal(ApplicationProtectionState.None, candidates[1].ProtectionState);
    }

    [Fact]
    public void ExactProtectionProducesPartialApplicationState()
    {
        var protectedPath = @"F:\Apps\EditorSuite\Editor\EditorStudio.exe";
        var helperPath = @"F:\Apps\EditorSuite\Editor\EditorStudioHelper.exe";
        var family = Family(
            "editor",
            Process(10, "EditorStudio", protectedPath),
            Process(11, "EditorStudioHelper", helperPath, parentProcessId: 10));

        var candidates = RunningProtectionCandidateCatalog.Create(
            new[] { family },
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = protectedPath,
                    ProtectEntireFamily = false,
                    ProtectedExecutablePaths = new List<string> { protectedPath }
                }
            });

        var candidate = Assert.Single(candidates);
        Assert.Equal(ApplicationProtectionState.Partial, candidate.ProtectionState);
        Assert.True(Assert.Single(candidate.Executables, executable => executable.ExecutablePath == protectedPath).IsProtected);
        Assert.False(Assert.Single(candidate.Executables, executable => executable.ExecutablePath == helperPath).IsProtected);
    }

    [Fact]
    public void MultipleProcessesUsingTheSameExecutableAreCollapsedIntoOneEntry()
    {
        var path = @"F:\Apps\Browser\browser.exe";
        var family = Family(
            "browser",
            Process(20, "Browser", path),
            Process(21, "Browser", path, parentProcessId: 20));

        var candidate = Assert.Single(RunningProtectionCandidateCatalog.Create(
            new[] { family },
            Array.Empty<ApplicationProtectionRule>()));

        var executable = Assert.Single(candidate.Executables);
        Assert.Equal(2, executable.InstanceCount);
        Assert.Equal(400L * 1024 * 1024, executable.WorkingSetBytes);
        Assert.Equal(new[] { 20, 21 }, executable.Processes.Select(process => process.ProcessId).Order());
        Assert.All(executable.Processes, process => Assert.Equal(200L * 1024 * 1024, process.WorkingSetBytes));
    }

    [Fact]
    public void LauncherAndGameFromUnrelatedDirectoriesRemainSeparateRunningApplications()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(30, "steam", @"D:\Steam\steam.exe"),
            Process(31, "bootstrapper", @"F:\SteamLibrary\steamapps\common\Call of Duty HQ\bootstrapper.exe", parentProcessId: 30),
            Process(32, "cod", @"F:\SteamLibrary\steamapps\common\Call of Duty HQ\cod.exe", parentProcessId: 31)
        });

        var candidates = RunningProtectionCandidateCatalog.Create(
            families,
            Array.Empty<ApplicationProtectionRule>());

        Assert.Equal(2, candidates.Count);
        var steam = Assert.Single(candidates, candidate => candidate.DisplayName == "steam");
        var cod = Assert.Single(candidates, candidate => candidate.DisplayName == "bootstrapper");
        Assert.Single(steam.Executables);
        Assert.Equal(2, cod.Executables.Count);
        Assert.DoesNotContain(cod.Executables, executable =>
            executable.ExecutablePath.Equals(@"D:\Steam\steam.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunningApplicationsKeepProtectedItemsFirstThenSortByMemoryUsage()
    {
        var protectedPath = @"F:\Apps\Protected\protected.exe";
        var candidates = RunningProtectionCandidateCatalog.Create(
            new[]
            {
                Family("small", Process(50, "Small", @"F:\Apps\Small\small.exe")),
                Family("large", Process(51, "Large", @"F:\Apps\Large\large.exe"),
                    Process(52, "Large", @"F:\Apps\Large\large.exe", parentProcessId: 51)),
                Family("protected", Process(53, "Protected", protectedPath))
            },
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = protectedPath,
                    ProtectEntireFamily = true
                }
            });

        Assert.Equal(new[] { "protected", "large", "small" },
            candidates.Select(candidate => candidate.FamilyKey));
    }

    [Fact]
    public void WholeFamilyRuleProtectsNestedApplicationDirectory()
    {
        var applicationPath = @"F:\Apps\Suite\app.exe";
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(30, "Suite", applicationPath),
            Process(31, "SuiteHelper", @"F:\Apps\Suite\Helpers\helper.exe", parentProcessId: 30)
        });

        var candidate = Assert.Single(RunningProtectionCandidateCatalog.Create(
            families,
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = applicationPath,
                    ProtectEntireFamily = true
                }
            }));

        Assert.Equal(ApplicationProtectionState.EntireFamily, candidate.ProtectionState);
        Assert.Equal(applicationPath, candidate.ApplicationExecutablePath);
        Assert.Equal(2, candidate.Executables.Count);
    }

    [Fact]
    public void WholeFamilyProtectionDoesNotAbsorbUnrelatedChildApplication()
    {
        var myDockPath = @"D:\Steam\steamapps\common\MyDockFinder\MyDock.exe";
        var myDock = Family(
            "mydock",
            Process(30, "MyDock", myDockPath),
            Process(31, "Dock_64", @"D:\Steam\steamapps\common\MyDockFinder\Dock_64.exe", parentProcessId: 30));
        var edge = Family(
            "edge",
            Process(32, "msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", parentProcessId: 31));

        var candidates = RunningProtectionCandidateCatalog.Create(
            new[] { myDock, edge },
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = myDockPath,
                    ProtectEntireFamily = true
                }
            });

        Assert.Equal(2, candidates.Count);
        Assert.Equal(ApplicationProtectionState.EntireFamily,
            Assert.Single(candidates, candidate => candidate.FamilyKey == "mydock").ProtectionState);
        Assert.Equal(ApplicationProtectionState.None,
            Assert.Single(candidates, candidate => candidate.FamilyKey == "edge").ProtectionState);
    }

    [Fact]
    public void MergeSelectionsRemovesDisplayedRulesButPreservesNonRunningRules()
    {
        var runningPath = @"F:\Apps\Editor\editor.exe";
        var dormantPath = @"F:\Apps\Archive\archive.exe";
        var current = new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = runningPath,
                ProtectEntireFamily = true
            },
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = dormantPath,
                ProtectEntireFamily = true
            }
        };

        var merged = RunningProtectionCandidateCatalog.MergeSelections(
            current,
            new[]
            {
                new RunningProtectionSelection(
                    runningPath,
                    ApplicationProtectionState.None,
                    Array.Empty<string>(),
                    new[] { runningPath })
            });

        Assert.Equal(dormantPath, Assert.Single(merged).ApplicationExecutablePath);
    }

    [Fact]
    public void MergeSelectionsCanReplaceAWholeFamilyWithExactExecutables()
    {
        var applicationPath = @"F:\Apps\Editor\editor.exe";
        var helperPath = @"F:\Apps\Editor\helper.exe";
        var merged = RunningProtectionCandidateCatalog.MergeSelections(
            new[]
            {
                new ApplicationProtectionRule
                {
                    ApplicationExecutablePath = applicationPath,
                    ProtectEntireFamily = true
                }
            },
            new[]
            {
                new RunningProtectionSelection(
                    applicationPath,
                    ApplicationProtectionState.Partial,
                    new[] { helperPath },
                    new[] { applicationPath })
            });

        var rule = Assert.Single(merged);
        Assert.False(rule.ProtectEntireFamily);
        Assert.Equal(helperPath, Assert.Single(rule.ProtectedExecutablePaths));
    }

    private static ProcessFamilySnapshot Family(string key, params ProcessSnapshot[] processes) =>
        new(key, key, Path.GetDirectoryName(processes[0].ExecutablePath), processes);

    private static ProcessSnapshot Process(int id, string name, string path, int? parentProcessId = null) =>
        new(id, name, path, parentProcessId, 200L * 1024 * 1024, 0, 0, false, false, true, 90);
}

public sealed class InfrastructureTests
{
    [Fact]
    public void ProcessRetentionIndicatorExplainsStableSuppressionWithoutMaskingProtection()
    {
        var stableReasons = new[] { CandidateExclusionReason.StableStateSuppression };

        Assert.Equal(
            ProcessRetentionIndicator.SessionStableState,
            ProcessRetentionPresentation.Resolve(false, false, stableReasons));
        Assert.Equal(
            ProcessRetentionIndicator.LongTermStableState,
            ProcessRetentionPresentation.Resolve(
                false, false, stableReasons, hasLongTermStableReference: true));
        Assert.Equal(
            ProcessRetentionIndicator.LongTermStableState,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                stableReasons,
                hasLongTermStableReference: true,
                naturalStableProvisionalValidation: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableReview,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                stableReasons,
                hasLongTermStableReference: true,
                naturalStableReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableObservation,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                stableReasons,
                naturalStableObservation: true,
                hasLongTermStableReference: true));
        Assert.Equal(
            ProcessRetentionIndicator.LongTermStableState,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[] { CandidateExclusionReason.BelowFamilyWorkingSet },
                hasLongTermStableReference: true,
                naturalStableProvisionalValidation: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableGrowthReview,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                stableReasons,
                hasLongTermStableReference: true,
                naturalStableReview: true,
                naturalStableGrowthReview: true,
                naturalStableProvisionalValidation: true));
        Assert.Equal(
            ProcessRetentionIndicator.LongTermStableState,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[]
                {
                    CandidateExclusionReason.StableStateSuppression,
                    CandidateExclusionReason.BelowFamilyWorkingSet
                },
                hasLongTermStableReference: true));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservationWithHistoricalStable,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[]
                {
                    CandidateExclusionReason.ReboundObservationPending,
                    CandidateExclusionReason.StableStateSuppression
                },
                hasLongTermStableReference: true));
        Assert.Equal(
            ProcessRetentionIndicator.LongTermStableState,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[]
                {
                    CandidateExclusionReason.ReboundObservationPending,
                    CandidateExclusionReason.StableStateSuppression
                },
                hasLongTermStableReference: true,
                reboundObservationPending: false));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservationWithHistoricalStable,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[] { CandidateExclusionReason.StableStateSuppression },
                hasLongTermStableReference: true,
                reboundObservationPending: true));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservation,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[]
                {
                    CandidateExclusionReason.ReboundObservationPending,
                    CandidateExclusionReason.StableStateSuppression
                }));
        Assert.Equal(
            ProcessRetentionIndicator.EntireFamilyProtection,
            ProcessRetentionPresentation.Resolve(true, false, stableReasons));
        Assert.Equal(
            ProcessRetentionIndicator.SessionStableState,
            ProcessRetentionPresentation.Resolve(false, true, stableReasons));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableGrowthReview,
            ProcessRetentionPresentation.Resolve(
                false,
                true,
                Array.Empty<CandidateExclusionReason>(),
                naturalStableGrowthReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.None,
            ProcessRetentionPresentation.Resolve(false, false, Array.Empty<CandidateExclusionReason>()));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservation,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.ReboundObservationPending },
                naturalStableObservation: true));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservation,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.ReboundObservationPending },
                naturalStableObservation: true,
                naturalStableReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableGrowthReview,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.ReboundObservationPending },
                naturalStableObservation: true,
                naturalStableReview: true,
                naturalStableGrowthReview: true,
                naturalStableProvisionalValidation: true));
        Assert.Equal(
            ProcessRetentionIndicator.BenefitObservation,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.ReboundObservationPending }));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableObservation,
            ProcessRetentionPresentation.Resolve(
                false,
                false,
                new[] { CandidateExclusionReason.AutomaticBackoff },
                naturalStableObservation: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableGrowthReview,
            ProcessRetentionPresentation.ResolveLifecycle(
                new[]
                {
                    CandidateExclusionReason.AutomaticBackoff,
                    CandidateExclusionReason.CurrentIoActivity
                }, naturalStableGrowthReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableGrowthReview,
            ProcessRetentionPresentation.ResolveLifecycle(
                new[] { CandidateExclusionReason.CurrentIoActivity },
                naturalStableGrowthReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.NaturalStableReview,
            ProcessRetentionPresentation.ResolveLifecycle(
                new[] { CandidateExclusionReason.UnreliableActivitySample },
                naturalStableReview: true));
        Assert.Equal(
            ProcessRetentionIndicator.AutomaticBackoff,
            ProcessRetentionPresentation.Resolve(
                false,
                true,
                new[] { CandidateExclusionReason.AutomaticBackoff }));
        Assert.Equal(
            ProcessRetentionIndicator.Sampling,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.UnreliableActivitySample }));
        Assert.Equal(
            ProcessRetentionIndicator.CandidateReady,
            ProcessRetentionPresentation.Resolve(
                false, false, Array.Empty<CandidateExclusionReason>(), isEligible: true));
        Assert.Equal(
            ProcessRetentionIndicator.CandidateReady,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.BelowProcessWorkingSet },
                isEligible: true,
                hasProcessableTargets: true));
        Assert.Equal(
            ProcessRetentionIndicator.BelowWorkingSetThreshold,
            ProcessRetentionPresentation.Resolve(
                false, false,
                new[] { CandidateExclusionReason.BelowProcessWorkingSet }));
    }

    [Fact]
    public void WindowModeResizePreservesTheCurrentCenter()
    {
        var result = WindowBoundsPolicy.CenterAndClamp(
            new WindowBounds(100, 80, 1240, 800),
            540,
            266,
            new WindowBounds(0, 0, 1920, 1040));

        Assert.Equal(new WindowBounds(450, 347, 540, 266), result);
    }

    [Fact]
    public void WindowModeResizeStaysInsideTheCurrentWorkingArea()
    {
        var result = WindowBoundsPolicy.CenterAndClamp(
            new WindowBounds(1700, 900, 540, 266),
            1240,
            800,
            new WindowBounds(0, 0, 1920, 1040));

        Assert.Equal(new WindowBounds(680, 240, 1240, 800), result);
    }

    [Fact]
    public void SettingsRoundTripPreservesFunctionalChoices()
    {
        var path = TestPath("settings.json");
        try
        {
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings
            {
                Profile = OptimizationProfile.Ultimate,
                AutoOptimization = true,
                StartWithWindows = true,
                LightTheme = true,
                ShowMemoryUsageInTrayIcon = true,
                ApplicationOptimizationRules = new List<ApplicationOptimizationRule>
                {
                    new()
                    {
                        Targets = new()
                        {
                            new()
                            {
                                TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
                                Path = @"F:\Apps\One\one.exe",
                                ExecutablePaths = new() { @"F:\Apps\One\one.exe", @"F:\Apps\One\helper.exe" }
                            }
                        }
                    }
                },
                ProtectedPaths = new List<string> { @"F:\Apps\One\one.exe" }
            });

            var loaded = store.Load();

            Assert.Equal(OptimizationProfile.Ultimate, loaded.Profile);
            Assert.True(loaded.AutoOptimization);
            Assert.True(loaded.StartWithWindows);
            Assert.True(loaded.LightTheme);
            Assert.True(loaded.ShowMemoryUsageInTrayIcon);
            var fixedGroup = Assert.Single(Assert.Single(loaded.ApplicationOptimizationRules!).Targets);
            Assert.Equal(ApplicationOptimizationTargetType.ExecutableGroup, fixedGroup.TargetType);
            Assert.Equal(2, fixedGroup.ExecutablePaths.Count);
            Assert.Equal(@"F:\Apps\One\one.exe", Assert.Single(loaded.ProtectedPaths));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(56, 56, 201.6)]
    [InlineData(100, 100, 360)]
    [InlineData(101, 100, 360)]
    public void TrayMemoryIconUsesBoundedPercentAsClockwiseRingProgress(
        int percent,
        int expectedPercent,
        float expectedSweep)
    {
        Assert.Equal(expectedPercent, TrayMemoryIconPolicy.Normalize(percent));
        Assert.Equal(expectedSweep, TrayMemoryIconPolicy.ProgressSweepAngle(percent), 3);
    }

    [Fact]
    public void TrayMemoryIconOnlyRegeneratesWhenTheDisplayedPercentChanges()
    {
        Assert.True(TrayMemoryIconPolicy.ShouldRegenerate(null, 47));
        Assert.False(TrayMemoryIconPolicy.ShouldRegenerate(47, 47));
        Assert.True(TrayMemoryIconPolicy.ShouldRegenerate(47, 48));
        Assert.Equal(0, TrayMemoryIconPolicy.Normalize(-1));
        Assert.Equal(100, TrayMemoryIconPolicy.Normalize(101));
    }

    [Fact]
    public void TrayMemoryIconUsesNeutralSurfacesAndTheBrandBlueAccent()
    {
        Assert.Equal(System.Drawing.Color.FromArgb(255, 17, 17, 19), TrayMemoryIconPolicy.BackgroundColor);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 63, 63, 70), TrayMemoryIconPolicy.TrackColor);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 124, 156, 235), TrayMemoryIconPolicy.ProgressColor);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 250, 250, 250), TrayMemoryIconPolicy.NumberColor);
    }

    [Fact]
    public void TrayMemoryIconCentersTextWithoutRendererPadding()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "TrayMemoryIcon.cs"));

        Assert.Contains("Forms.TextFormatFlags.NoPadding", source);
        Assert.Contains("Forms.TextFormatFlags.HorizontalCenter", source);
        Assert.Contains("Forms.TextFormatFlags.VerticalCenter", source);
        Assert.DoesNotContain("graphics.DrawString", source);
        Assert.Contains("graphics.DrawArc(progress, ringBounds, -90", source);
        Assert.Contains("graphics.FillEllipse(background", source);
        Assert.Contains("graphics.FillEllipse(background, new RectangleF(1, 1, 29, 29))", source);
        Assert.Contains("new RectangleF(2, 2, 27, 27)", source);
        Assert.Contains("TrayMemoryIconPolicy.TrackColor, 2.5f", source);
        Assert.Contains("TrayMemoryIconPolicy.ProgressColor, 2.5f", source);
        Assert.Contains("percent == 100 ? 11f : 17f", source);
    }

    [Fact]
    public void TrayMemoryRingRendersBrandPixelsAndOpticallyCenteredNumber()
    {
        using var bitmap = TrayMemoryIconController.RenderBitmap(56);
        var numberPixels = new List<System.Drawing.Point>();
        var opaquePixels = new List<System.Drawing.Point>();
        var progressPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A > 0) opaquePixels.Add(new System.Drawing.Point(x, y));
            if (pixel.ToArgb() == TrayMemoryIconPolicy.NumberColor.ToArgb())
                numberPixels.Add(new System.Drawing.Point(x, y));
            if (pixel.ToArgb() == TrayMemoryIconPolicy.ProgressColor.ToArgb())
                progressPixels++;
        }

        Assert.NotEmpty(numberPixels);
        Assert.True(progressPixels > 0);
        Assert.Equal(1, opaquePixels.Min(point => point.X));
        Assert.Equal(30, opaquePixels.Max(point => point.X));
        Assert.Equal(1, opaquePixels.Min(point => point.Y));
        Assert.Equal(30, opaquePixels.Max(point => point.Y));
        Assert.True(numberPixels.Max(point => point.X) - numberPixels.Min(point => point.X) + 1 >= 16);
        Assert.True(numberPixels.Max(point => point.Y) - numberPixels.Min(point => point.Y) + 1 >= 11);
        var horizontalCenter = (numberPixels.Min(point => point.X) + numberPixels.Max(point => point.X)) / 2d;
        var verticalCenter = (numberPixels.Min(point => point.Y) + numberPixels.Max(point => point.Y)) / 2d;
        Assert.InRange(horizontalCenter, 14.5, 16.5);
        Assert.InRange(verticalCenter, 14.5, 16.5);
    }

    [Fact]
    public void StartupTaskUsesInteractiveLogonWithHighestPrivileges()
    {
        var task = StartupLaunchPolicy.CreateTaskSpec(
            @"F:\Muse RAM\MuseRAM.exe",
            @"DESKTOP\MuseUser");

        Assert.Equal(@"F:\Muse RAM\MuseRAM.exe", task.ExecutablePath);
        Assert.Equal("--background", task.Arguments);
        Assert.Equal(@"F:\Muse RAM", task.WorkingDirectory);
        Assert.Equal(@"DESKTOP\MuseUser", task.UserId);
        Assert.True(task.RunWithHighestPrivileges);
        Assert.True(task.InteractiveLogonOnly);
        Assert.True(StartupLaunchPolicy.ShouldStartHidden(new[] { "--BACKGROUND" }));
    }

    [Fact]
    public void StartupTaskAcceptsSidAsCanonicalUserIdentity()
    {
        const string sid = "S-1-5-21-1-2-3-1001";

        var task = StartupLaunchPolicy.CreateTaskSpec(@"F:\MuseRAM\MuseRAM.exe", sid);

        Assert.Equal(sid, task.UserId);
    }

    [Fact]
    public void StartupIdentityComparisonAcceptsCurrentNameAndSid()
    {
        using var identity = WindowsIdentity.GetCurrent();

        Assert.True(StartupLaunchPolicy.UserIdsReferToSameAccount(
            identity.Name,
            identity.User?.Value));
    }

    [Fact]
    public void StartupTaskValidationRequiresTheExpectedLogonUser()
    {
        Assert.True(StartupTaskValidationPolicy.IsExpectedLogonTrigger(
            StartupTaskValidationPolicy.LogonTriggerType,
            "S-1-5-18",
            "S-1-5-18"));
        Assert.False(StartupTaskValidationPolicy.IsExpectedLogonTrigger(
            StartupTaskValidationPolicy.LogonTriggerType,
            "S-1-5-19",
            "S-1-5-18"));
        Assert.False(StartupTaskValidationPolicy.IsExpectedLogonTrigger(
            triggerType: 8,
            "S-1-5-18",
            "S-1-5-18"));
    }

    [Fact]
    public void StartupPreferenceDoesNotEnableAutoOptimization()
    {
        var path = TestPath("startup-settings.json");
        try
        {
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings { StartWithWindows = true, AutoOptimization = false });

            var loaded = store.Load();

            Assert.True(loaded.StartWithWindows);
            Assert.False(loaded.AutoOptimization);
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
        }
    }

    [Fact]
    public void HistoryRoundTripKeepsNewestThirtyEntries()
    {
        var path = TestPath("history.json");
        try
        {
            var store = new ActivityHistoryStore(path);
            store.Save(Enumerable.Range(1, 120).Select(index =>
                ActivityHistoryEntry.Create(
                    "HistoryProfileFormat",
                    new object?[] { $"event-{index}" },
                    occurredAt: DateTimeOffset.UnixEpoch.AddSeconds(index))));

            var loaded = store.Load();

            Assert.Equal(30, loaded.Count);
            Assert.Equal("event-1", loaded[0].Arguments[0]);
            Assert.Equal("event-30", loaded[^1].Arguments[0]);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(30, document.RootElement.GetProperty("Records").GetArrayLength());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
        }
    }

    [Fact]
    public void HistoryLoadsLegacyStringArrayWithoutChangingItsText()
    {
        var path = TestPath("history-legacy.json");
        try
        {
            File.WriteAllText(path, "[\"12:34:56  legacy entry\"]");

            var loaded = new ActivityHistoryStore(path).Load();

            var entry = Assert.Single(loaded);
            Assert.Equal("12:34:56  legacy entry", entry.Format(UiLanguage.English));
            Assert.Equal("12:34:56  legacy entry", entry.Format(UiLanguage.ChineseSimplified));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
        }
    }

    [Fact]
    public void StructuredHistoryRendersOuterAndNestedMessagesInSelectedLanguage()
    {
        var entry = ActivityHistoryEntry.Create(
            "HistoryLongIdleOptimizationFormat",
            new object?[] { string.Empty },
            "OptimizationResultFormat",
            new object?[] { "782 MB", "+158 MB" },
            occurredAt: new DateTimeOffset(2026, 8, 3, 12, 34, 56, TimeSpan.Zero));

        Assert.Contains("长期闲置优化：工作集 -782 MB，可用内存 +158 MB", entry.Format(UiLanguage.ChineseSimplified));
        Assert.Contains("Long-idle optimization: Working set -782 MB, available memory +158 MB", entry.Format(UiLanguage.English));
    }

    [Fact]
    public void BenefitLearningRoundTripKeepsNewestTwoHundredFiftyEntries()
    {
        var path = TestPath("benefit-learning.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new BenefitLearningStore(path);
            store.Save(Enumerable.Range(1, 300).Select(index =>
                new ApplicationBenefitLearningRecord(
                    $"app-{index}",
                    0.5,
                    4,
                    1,
                    now - TimeSpan.FromMinutes(index))));

            var loaded = store.Load();

            Assert.Equal(250, loaded.Count);
            Assert.Equal("app-1", loaded[0].FamilyKey);
            Assert.Equal("app-250", loaded[^1].FamilyKey);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(250, document.RootElement.GetProperty("Records").GetArrayLength());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningRoundTripKeepsLaunchContributionWeightAndFamilyStableData()
    {
        var path = TestPath("benefit-learning-weight.json");
        try
        {
            var store = new BenefitLearningStore(path);
            store.Save(new[]
            {
                new ApplicationBenefitLearningRecord(
                    "weighted-app",
                    0.8,
                    21,
                    4,
                    DateTimeOffset.UtcNow)
                {
                    ComponentKey = "weighted-app|component:main",
                    ValidSampleCount = 21,
                    DistinctLaunchCount = 21,
                    LastLaunchContributionWeight = 0.05,
                    RecentQuickReturnRate = 0.2
                }
            }, familyStableRecords: new[]
            {
                new ApplicationStableLearningRecord(
                    "weighted-app", new long[] { 200, 220, 210 }, DateTimeOffset.UtcNow, "launch-21")
                {
                    ComponentKeys = new[] { "weighted-app|component:main" }
                },
                new ApplicationStableLearningRecord(
                    "weighted-app", new long[] { 300, 320, 310 }, DateTimeOffset.UtcNow, "launch-partial")
                {
                    ComponentKeys = new[] { "weighted-app|component:main", "weighted-app|component:helper" }
                }
            });

            var result = store.LoadWithStatus();
            var loaded = Assert.Single(result.Records);

            Assert.Equal(0.05, loaded.LastLaunchContributionWeight);
            Assert.Equal(0.2, loaded.RecentQuickReturnRate);
            Assert.Empty(loaded.StableWorkingSetSamplesBytes);
            Assert.Equal(2, result.FamilyStableRecords.Count);
            var stable = Assert.Single(result.FamilyStableRecords.Where(record => record.ComponentKeys.Count == 1));
            Assert.Equal(new long[] { 200, 220, 210 }, stable.StableWorkingSetSamplesBytes);
            Assert.Equal("launch-21", stable.LastStableLaunchSignature);
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningSchemaThreeMigrationDropsOnlyComponentStableSamples()
    {
        var path = TestPath("benefit-learning-stable-merge.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var records = new[]
            {
                new ApplicationBenefitLearningRecord("suite", 0.5, 2, 0, now - TimeSpan.FromHours(1))
                {
                    ComponentKey = "suite|component:main",
                    ValidSampleCount = 2,
                    StableWorkingSetSamplesBytes = new long[] { 100, 200 },
                    StableLastObservedAt = now - TimeSpan.FromHours(1),
                    LastStableLaunchSignature = "launch-2"
                },
                new ApplicationBenefitLearningRecord("suite", 0.7, 3, 0, now)
                {
                    ComponentKey = "suite|component:main",
                    ValidSampleCount = 3,
                    StableWorkingSetSamplesBytes = new long[] { 300, 400, 500 },
                    StableLastObservedAt = now,
                    LastStableLaunchSignature = "launch-5"
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 3, Records = records }));
            var result = new BenefitLearningStore(path).LoadWithStatus();
            var merged = Assert.Single(result.Records);

            Assert.True(result.Migrated);
            Assert.Equal(5, merged.ValidSampleCount);
            Assert.Equal(0.62, merged.AverageOutcomeMultiplier, precision: 2);
            Assert.Empty(merged.StableWorkingSetSamplesBytes);
            Assert.Null(merged.LastStableLaunchSignature);
            Assert.Null(merged.StableLastObservedAt);
            Assert.Empty(result.FamilyStableRecords);
            Assert.False(File.Exists(path + ".bak"));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningSchemaFourMigratesStableSamplesAsOneLegacyRecoveryCycle()
    {
        var path = TestPath("benefit-learning-anchor-generation.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var stable = new ApplicationStableLearningRecord(
                "suite",
                new long[] { 200, 220, 210 },
                now,
                "launch-legacy")
            {
                ComponentKeys = new[] { "suite|component:main" },
                ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
                LastStableLaunchSampleCount = 3
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                SchemaVersion = 4,
                Records = Array.Empty<ApplicationBenefitLearningRecord>(),
                FamilyStableRecords = new[] { stable }
            }));

            var result = new BenefitLearningStore(path).LoadWithStatus();
            var migrated = Assert.Single(result.FamilyStableRecords);

            Assert.True(result.Migrated);
            Assert.Equal(1, migrated.AnchorGeneration);
            Assert.Equal(210, migrated.AnchorGenerationBaselineBytes);
            Assert.Equal(3, migrated.StableSamples.Count);
            Assert.All(migrated.StableSamples, sample =>
            {
                Assert.Equal("legacy", sample.RecoveryCycleId);
                Assert.False(sample.PendingHigh);
            });
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.False(File.Exists(path + ".bak"));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningRoundTripKeepsDismissedProtectionSuggestions()
    {
        var path = TestPath("benefit-learning-suggestions.json");
        try
        {
            var store = new BenefitLearningStore(path);
            var record = new ApplicationBenefitLearningRecord(
                "suite", 0.2, 8, 0, DateTimeOffset.UtcNow)
            {
                ComponentKey = "suite|component:main"
            };

            store.Save(new[] { record }, new[] { "PROTECT|SUITE|HIGH-REBOUND-V1" });
            var loaded = store.LoadWithStatus();

            Assert.Equal("suite", Assert.Single(loaded.Records).FamilyKey);
            Assert.Equal(
                "protect|suite|high-rebound-v1",
                Assert.Single(loaded.DismissedSuggestionIds));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void LegacyBenefitLearningArrayIsBackedUpAndMigrated()
    {
        var path = TestPath("legacy-benefit-learning.json");
        try
        {
            var record = new ApplicationBenefitLearningRecord("editor", 0.7, 4, 1, DateTimeOffset.UtcNow);
            var legacy = JsonSerializer.Serialize(new[] { record });
            File.WriteAllText(path, legacy);

            var store = new BenefitLearningStore(path);
            var result = store.LoadWithStatus();

            Assert.True(result.Migrated);
            Assert.Null(result.ErrorMessage);
            Assert.Equal("editor", Assert.Single(result.Records).FamilyKey);
            Assert.False(File.Exists(path + ".bak"));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("Records").GetArrayLength());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningMigratesAndMergesWindowsPackageVersions()
    {
        var path = TestPath("package-benefit-learning.json");
        try
        {
            const string oldPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.40816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            const string newPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.4816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            var oldFamily = "directory:" + Path.GetDirectoryName(oldPath)!.ToLowerInvariant();
            var newFamily = "directory:" + Path.GetDirectoryName(newPath)!.ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var records = new[]
            {
                new ApplicationBenefitLearningRecord(oldFamily, 0.4, 2, 0, now - TimeSpan.FromDays(1))
                {
                    ComponentKey = oldFamily + "|component:" + oldPath.ToLowerInvariant(),
                    ExecutablePath = oldPath,
                    ValidSampleCount = 2,
                    DistinctLaunchCount = 1,
                    AverageRetainedBytes = 400,
                    LateWorkingSetSamplesBytes = new long[] { 100, 200 }
                },
                new ApplicationBenefitLearningRecord(newFamily, 0.8, 3, 1, now)
                {
                    ComponentKey = newFamily + "|component:" + newPath.ToLowerInvariant(),
                    ExecutablePath = newPath,
                    ValidSampleCount = 3,
                    DistinctLaunchCount = 2,
                    AverageRetainedBytes = 800,
                    LateWorkingSetSamplesBytes = new long[] { 300, 400, 500 }
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 1, Records = records }));

            var result = new BenefitLearningStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            var merged = Assert.Single(result.Records);
            Assert.Equal("package:openai.codex_2p2nqsd0c76g0", merged.FamilyKey);
            Assert.Equal(5, merged.ValidSampleCount);
            Assert.Equal(3, merged.DistinctLaunchCount);
            Assert.Equal(640, merged.AverageRetainedBytes);
            Assert.Equal(new long[] { 100, 200, 300, 400, 500 }, merged.LateWorkingSetSamplesBytes);
            Assert.False(File.Exists(path + ".bak"));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Single(document.RootElement.GetProperty("Records").EnumerateArray());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningPackageMergeDoesNotWeightValidMetricsWithLegacyOnlyRecords()
    {
        var path = TestPath("package-benefit-learning-legacy-isolation.json");
        try
        {
            const string oldPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.40816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            const string newPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.4816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
            var oldFamily = "directory:" + Path.GetDirectoryName(oldPath)!.ToLowerInvariant();
            var newFamily = "directory:" + Path.GetDirectoryName(newPath)!.ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var records = new[]
            {
                new ApplicationBenefitLearningRecord(oldFamily, 0.2, 10, 5, now - TimeSpan.FromDays(1))
                {
                    ComponentKey = oldFamily + "|component:" + oldPath.ToLowerInvariant(),
                    ExecutablePath = oldPath,
                    AverageRetainedBytes = 100,
                    AverageLateWorkingSetBytes = 200,
                    AverageReboundPercent = 80,
                    LegacySampleCount = 10,
                    ValidSampleCount = 0
                },
                new ApplicationBenefitLearningRecord(newFamily, 0.8, 3, 0, now)
                {
                    ComponentKey = newFamily + "|component:" + newPath.ToLowerInvariant(),
                    ExecutablePath = newPath,
                    AverageRetainedBytes = 800,
                    AverageLateWorkingSetBytes = 900,
                    AverageReboundPercent = 20,
                    DistinctLaunchCount = 3,
                    ValidSampleCount = 3,
                    LateWorkingSetSamplesBytes = new long[] { 700, 900, 1100 }
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 2, Records = records }));

            var merged = Assert.Single(new BenefitLearningStore(path).Load());

            Assert.Equal(3, merged.SampleCount);
            Assert.Equal(3, merged.ValidSampleCount);
            Assert.Equal(0, merged.LegacySampleCount);
            Assert.Equal(0.8, merged.AverageOutcomeMultiplier, 10);
            Assert.Equal(800, merged.AverageRetainedBytes);
            Assert.Equal(900, merged.AverageLateWorkingSetBytes);
            Assert.Equal(20, merged.AverageReboundPercent);
            Assert.Empty(merged.StableWorkingSetSamplesBytes);
            Assert.False(File.Exists(path + ".bak"));
            using var migrated = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, migrated.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void BenefitLearningMigratesAndMergesVersionedAppDirectories()
    {
        var path = TestPath("versioned-directory-benefit-learning.json");
        try
        {
            const string oldPath = @"C:\Users\User\AppData\Local\KOOK\app-0.109.0\KOOK.exe";
            const string newPath = @"C:\Users\User\AppData\Local\KOOK\app-0.109.1\KOOK.exe";
            var oldFamily = "directory:" + Path.GetDirectoryName(oldPath)!.ToLowerInvariant();
            var newFamily = "directory:" + Path.GetDirectoryName(newPath)!.ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var records = new[]
            {
                new ApplicationBenefitLearningRecord(oldFamily, 0.4, 2, 0, now - TimeSpan.FromDays(1))
                {
                    ComponentKey = oldFamily + "|component:" + oldPath.ToLowerInvariant(),
                    ExecutablePath = oldPath,
                    ValidSampleCount = 2,
                    DistinctLaunchCount = 1
                },
                new ApplicationBenefitLearningRecord(newFamily, 0.8, 3, 0, now)
                {
                    ComponentKey = newFamily + "|component:" + newPath.ToLowerInvariant(),
                    ExecutablePath = newPath,
                    ValidSampleCount = 3,
                    DistinctLaunchCount = 2
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                SchemaVersion = 5,
                Records = records
            }));

            var result = new BenefitLearningStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            var merged = Assert.Single(result.Records);
            Assert.Equal(@"directory:c:\users\user\appdata\local\kook", merged.FamilyKey);
            Assert.Equal(@"directory:c:\users\user\appdata\local\kook|component:versioned:kook.exe",
                merged.ComponentKey);
            Assert.Equal(5, merged.ValidSampleCount);
            Assert.Equal(3, merged.DistinctLaunchCount);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Single(document.RootElement.GetProperty("Records").EnumerateArray());
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void FutureBenefitLearningVersionIsNotOverwritten()
    {
        var path = TestPath("future-benefit-learning.json");
        try
        {
            const string future = """{"SchemaVersion":7,"Records":[]}""";
            File.WriteAllText(path, future);

            var store = new BenefitLearningStore(path);
            var result = store.LoadWithStatus();

            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Records);
            Assert.Equal(future, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.Throws<InvalidOperationException>(() => store.Save(Array.Empty<ApplicationBenefitLearningRecord>()));
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".tmp");
            DeleteFile(path + ".bak");
        }
    }

    [Fact]
    public void InMemoryHistoryKeepsTheThirtyEntryLimit()
    {
        var state = new AppState();

        for (var index = 1; index <= 35; index++) state.AddHistory($"event-{index}");

        Assert.Equal(30, state.History.Count);
        Assert.Contains("event-35", state.History[0]);
        Assert.Contains("event-6", state.History[^1]);
    }

    [Theory]
    [InlineData(false, false, 15)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    public void MonitoringIntervalMatchesActiveWork(bool automaticOptimization, bool reboundTracking, int seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            MonitoringIntervalPolicy.Resolve(automaticOptimization, reboundTracking));
    }

    [Fact]
    public void DiagnosticLogWritesSingleLineEntries()
    {
        var path = TestPath("museram.log");
        try
        {
            var log = new DiagnosticLog(path);
            log.Warning("first line\nsecond line", new InvalidOperationException("test failure"));

            var entry = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("[WARN] first line second line", entry);
            Assert.Contains("InvalidOperationException: test failure", entry);
        }
        finally
        {
            DeleteFile(path);
            DeleteFile(path + ".previous");
        }
    }

    [Fact]
    public void DisabledDiagnosticLogDoesNotCreateAFileOrDirectory()
    {
        var directory = TestPath("diagnostics");
        var path = Path.Combine(directory, "museram.log");

        var log = new DiagnosticLog(path, isEnabled: () => false);
        log.Warning("must stay disabled");

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(directory));
    }

    private static string TestPath(string suffix) =>
        Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}-{suffix}");

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

public sealed class RelatedServiceAdvisorTests
{
    [Fact]
    public void FindRecommendsRunningServiceFromSelectedApplicationDirectory()
    {
        var family = Family(@"F:\Apps\Editor\editor.exe");
        var services = new[]
        {
            new WindowsServiceDescriptor("EditorAgent", "Editor Agent", @"F:\Apps\Editor\agent.exe", true, false),
            new WindowsServiceDescriptor("OtherAgent", "Other Agent", @"F:\Apps\Other\agent.exe", true, false)
        };

        var suggestion = Assert.Single(RelatedServiceAdvisor.Find(new[] { family }, services));

        Assert.Equal("EditorAgent", suggestion.Service.Name);
        Assert.False(suggestion.IsRecommended);
    }

    [Fact]
    public void FindDoesNotPreselectRelatedSystemService()
    {
        var family = Family(@"C:\Windows\Vendor\tool.exe");
        var services = new[]
        {
            new WindowsServiceDescriptor("VendorSystem", "Vendor System", @"C:\Windows\Vendor\agent.exe", true, true)
        };

        var suggestion = Assert.Single(RelatedServiceAdvisor.Find(new[] { family }, services));

        Assert.False(suggestion.IsRecommended);
        Assert.Contains("系统服务", suggestion.Impact);
    }

    [Theory]
    [InlineData("\"F:\\Apps\\Editor\\agent.exe\" --service", @"F:\Apps\Editor\agent.exe")]
    [InlineData("F:\\Apps\\Editor\\agent.exe --service", @"F:\Apps\Editor\agent.exe")]
    public void ExtractExecutablePathHandlesServiceCommandLines(string commandLine, string expected)
    {
        Assert.Equal(expected, WindowsServiceCommandLine.ExtractExecutablePath(commandLine));
    }

    [Fact]
    public void FindIncludesDynamicKnownServiceAndKeepsItUnchecked()
    {
        var services = new[]
        {
            new WindowsServiceDescriptor("CDPUserSvc_4f92a", "Connected Devices", null, true, true)
        };

        var suggestion = Assert.Single(RelatedServiceAdvisor.Find(Array.Empty<ProcessFamilySnapshot>(), services));

        Assert.Equal("CDPUserSvc_4f92a", suggestion.Service.Name);
        Assert.False(suggestion.IsRecommended);
    }

    [Fact]
    public void DedicatedServiceProcessIsRemovedFromApplicationCandidates()
    {
        var serviceProcess = new ProcessSnapshot(20, "MarvisSvr", null, null, 100L * 1024 * 1024, 0, 0, false, false, true, 90);
        var appProcess = new ProcessSnapshot(10, "Marvis", null, null, 100L * 1024 * 1024, 0, 0, false, false, true, 90);
        var applications = new[]
        {
            Candidate("Marvis", appProcess),
            Candidate("MarvisSvr", serviceProcess)
        };
        var services = new[]
        {
            new ServiceSuggestion(
                new WindowsServiceDescriptor("MarvisSvr", "Marvis Service", null, true, false, 20),
                "Marvis",
                false,
                "应用后台服务")
        };

        var remaining = DeepReleaseCandidateDeduplicator.RemoveServiceDuplicates(applications, services);

        Assert.Equal("Marvis", Assert.Single(remaining).Family.DisplayName);
    }

    private static ProcessFamilySnapshot Family(string executablePath)
    {
        var process = new ProcessSnapshot(1, "editor", executablePath, null, 200L * 1024 * 1024, 0, 0, false, false, true, 90);
        return new ProcessFamilySnapshot("editor", "Editor", Path.GetDirectoryName(executablePath), new[] { process });
    }

    private static DeepReleaseCandidate Candidate(string displayName, ProcessSnapshot process)
    {
        var family = new ProcessFamilySnapshot(displayName, displayName, null, new[] { process });
        return new DeepReleaseCandidate(
            family,
            new BackgroundActivity(displayName, BackgroundActivityState.Observing, TimeSpan.Zero, TimeSpan.Zero, 1),
            false);
    }
}

public sealed class ServiceStopVerificationPolicyTests
{
    [Fact]
    public void StopIsSuccessfulOnlyAfterStoppedStateIsObserved()
    {
        var states = new Queue<WindowsServiceStatusQuery>(new[]
        {
            new WindowsServiceStatusQuery(true, WindowsServiceRuntimeState.StopPending),
            new WindowsServiceStatusQuery(true, WindowsServiceRuntimeState.Running),
            new WindowsServiceStatusQuery(true, WindowsServiceRuntimeState.Stopped)
        });
        var waits = 0;

        var result = ServiceStopVerificationPolicy.Verify(
            "EditorService",
            states.Dequeue,
            _ => waits++,
            maximumChecks: 3);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void AcceptedStopThatNeverReachesStoppedStateReportsFailure()
    {
        var waits = 0;

        var result = ServiceStopVerificationPolicy.Verify(
            "EditorService",
            () => new WindowsServiceStatusQuery(true, WindowsServiceRuntimeState.StopPending),
            _ => waits++,
            maximumChecks: 3);

        Assert.False(result.Success);
        Assert.Contains("verification timeout", result.Error);
        Assert.Contains(nameof(WindowsServiceRuntimeState.StopPending), result.Error);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void StatusQueryFailureCannotBeReportedAsStopped()
    {
        var result = ServiceStopVerificationPolicy.Verify(
            "EditorService",
            () => new WindowsServiceStatusQuery(false, default, 5),
            _ => throw new InvalidOperationException("No wait is expected."));

        Assert.False(result.Success);
        Assert.Equal("QueryServiceStatusEx failed (5).", result.Error);
    }

    [Fact]
    public void AlreadyStoppedServiceSucceedsWithoutWaiting()
    {
        var result = ServiceStopVerificationPolicy.Verify(
            "EditorService",
            () => new WindowsServiceStatusQuery(true, WindowsServiceRuntimeState.Stopped),
            _ => throw new InvalidOperationException("No wait is expected."));

        Assert.True(result.Success);
    }
}

public sealed class UiLanguageCatalogTests
{
    [Theory]
    [InlineData("zh-CN", UiLanguage.ChineseSimplified)]
    [InlineData("en", UiLanguage.English)]
    public void LanguageCodesRoundTrip(string code, UiLanguage expected)
    {
        var language = UiLanguageCatalog.FromCode(code);
        Assert.Equal(expected, language);
        Assert.Equal(code, UiLanguageCatalog.ToCode(language));
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void RemovedLanguageCodesFallBackToSimplifiedChinese(string code)
    {
        Assert.Equal(UiLanguage.ChineseSimplified, UiLanguageCatalog.FromCode(code));
    }

    [Fact]
    public void EveryLanguageContainsEveryUiResource()
    {
        var expectedKeys = UiTextCatalog.For(UiLanguage.ChineseSimplified).Keys.OrderBy(key => key).ToArray();
        foreach (var option in UiLanguageCatalog.Options)
        {
            Assert.Equal(expectedKeys, UiTextCatalog.For(option.Language).Keys.OrderBy(key => key));
        }
    }
}

public sealed class UpdateServicesTests
{
    [Theory]
    [InlineData(UpdateCheckFrequency.EveryStartup, null, true)]
    [InlineData(UpdateCheckFrequency.Daily, 23, false)]
    [InlineData(UpdateCheckFrequency.Daily, 24, true)]
    [InlineData(UpdateCheckFrequency.Weekly, 167, false)]
    [InlineData(UpdateCheckFrequency.Weekly, 168, true)]
    [InlineData(UpdateCheckFrequency.ManualOnly, null, false)]
    public void AutomaticCheckPolicyHonorsConfiguredFrequency(
        UpdateCheckFrequency frequency,
        int? elapsedHours,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? lastCheck = elapsedHours.HasValue ? now.AddHours(-elapsedHours.Value) : null;

        Assert.Equal(expected, UpdateCheckPolicy.IsDue(frequency, lastCheck, now));
    }

    [Fact]
    public void ExpiredUpdateCleanupOnlyRemovesRecognizedTopLevelFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"cleanup-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var expiredPackage = Path.Combine(root, "MuseRAM-1.2.0.exe");
        var expiredDownload = Path.Combine(root, "MuseRAM-1.2.0.exe.download");
        var unrelated = Path.Combine(root, "notes.txt");
        var nestedPackage = Path.Combine(nested, "MuseRAM-1.1.0.exe");
        File.WriteAllText(expiredPackage, "package");
        File.WriteAllText(expiredDownload, "download");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(nestedPackage, "keep");
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(expiredPackage, now.AddDays(-8).UtcDateTime);
        File.SetLastWriteTimeUtc(expiredDownload, now.AddDays(-8).UtcDateTime);
        try
        {
            Assert.Equal(2, UpdateStorage.CleanupExpired(root, now, TimeSpan.FromDays(7)));
            Assert.False(File.Exists(expiredPackage));
            Assert.False(File.Exists(expiredDownload));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(nestedPackage));
        }
        finally
        {
            if (File.Exists(expiredPackage)) File.Delete(expiredPackage);
            if (File.Exists(expiredDownload)) File.Delete(expiredDownload);
            if (File.Exists(unrelated)) File.Delete(unrelated);
            if (File.Exists(nestedPackage)) File.Delete(nestedPackage);
            if (Directory.Exists(nested)) Directory.Delete(nested);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("--apply-update", true)]
    [InlineData("--APPLY-UPDATE", true)]
    [InlineData("--check-update", false)]
    public void CompletionRequestDetectionIsExplicitAndCaseInsensitive(string argument, bool expected)
    {
        Assert.Equal(expected, UpdateCompletionService.IsRequested(new[] { argument }));
    }

    [Fact]
    public void EmptyArgumentsAreNotAnUpdateCompletionRequest()
    {
        Assert.False(UpdateCompletionService.IsRequested(Array.Empty<string>()));
    }

    [Fact]
    public async Task CheckAsyncRejectsNonHttpsFeed()
    {
        using var httpClient = new HttpClient(new StaticHandler(Array.Empty<byte>()));

        await Assert.ThrowsAsync<ArgumentException>(() => new UpdateFeedClient(httpClient).CheckAsync(
            new Uri("http://updates.example/manifest.json"),
            new Version(1, 1, 0)));
    }

    [Fact]
    public async Task CheckAsyncRejectsRedirectsToNonHttpsFeed()
    {
        var json = Encoding.UTF8.GetBytes(
            """{"version":"1.2.0","downloadUrl":"https://updates.example/MuseRAM.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""");
        using var httpClient = new HttpClient(new StaticHandler(
            json,
            new Uri("http://updates.example/manifest.json")));

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateFeedClient(httpClient).CheckAsync(
            new Uri("https://updates.example/manifest.json"),
            new Version(1, 1, 0)));
    }

    [Fact]
    public async Task CheckAsyncReturnsNewerValidatedAsset()
    {
        var json = """{"version":"1.2.0","downloadUrl":"https://updates.example/MuseRAM.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""";
        using var httpClient = new HttpClient(new StaticHandler(Encoding.UTF8.GetBytes(json)));

        var result = await new UpdateFeedClient(httpClient).CheckAsync(
            new Uri("https://updates.example/manifest.json"),
            new Version(1, 1, 0));

        Assert.True(result.IsAvailable);
        Assert.Equal(new Version(1, 2, 0), result.Asset!.Version);
    }

    [Fact]
    public async Task DownloadAsyncStreamsAndValidatesSha256()
    {
        var content = Encoding.UTF8.GetBytes("verified-update-package");
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var directory = Path.Combine(AppContext.BaseDirectory, $"update-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "MuseRAM-1.2.0.exe");
        try
        {
            using var httpClient = new HttpClient(new StaticHandler(content));
            var asset = new UpdateAsset(new Version(1, 2, 0), new Uri("https://updates.example/MuseRAM.exe"), hash, "MuseRAM.exe");

            var path = await new UpdatePackageDownloader(httpClient).DownloadAsync(asset, directory);

            Assert.Equal(destination, path);
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            var temporary = destination + ".download";
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DownloadAsyncRemovesTemporaryFileWhenSha256DoesNotMatch()
    {
        var content = Encoding.UTF8.GetBytes("changed-update-package");
        var directory = Path.Combine(AppContext.BaseDirectory, $"update-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "MuseRAM-1.2.0.exe");
        var temporary = destination + ".download";
        try
        {
            using var httpClient = new HttpClient(new StaticHandler(content));
            var asset = new UpdateAsset(
                new Version(1, 2, 0),
                new Uri("https://updates.example/MuseRAM.exe"),
                new string('A', 64),
                "MuseRAM.exe");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdatePackageDownloader(httpClient).DownloadAsync(asset, directory));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DownloadAsyncRejectsRedirectsToNonHttpsPackage()
    {
        var content = Encoding.UTF8.GetBytes("redirected-update-package");
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var directory = Path.Combine(AppContext.BaseDirectory, $"update-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(new StaticHandler(
                content,
                new Uri("http://updates.example/MuseRAM.exe")));
            var asset = new UpdateAsset(
                new Version(1, 2, 0),
                new Uri("https://updates.example/MuseRAM.exe"),
                hash,
                "MuseRAM.exe");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdatePackageDownloader(httpClient).DownloadAsync(asset, directory));
            Assert.False(File.Exists(Path.Combine(directory, "MuseRAM-1.2.0.exe")));
        }
        finally
        {
            var temporary = Path.Combine(directory, "MuseRAM-1.2.0.exe.download");
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DownloadAsyncTimesOutWaitingForTheServerWithoutLeavingATemporaryFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"update-timeout-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "MuseRAM-1.2.0.exe");
        try
        {
            using var httpClient = new HttpClient(new WaitingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            var asset = new UpdateAsset(
                new Version(1, 2, 0),
                new Uri("https://updates.example/MuseRAM.exe"),
                new string('A', 64),
                "MuseRAM.exe");

            await Assert.ThrowsAsync<TimeoutException>(() =>
                new UpdatePackageDownloader(httpClient).DownloadAsync(
                    asset,
                    directory,
                    responseTimeout: TimeSpan.FromMilliseconds(20)));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".download"));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(destination + ".download")) File.Delete(destination + ".download");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void SingleExecutableDetectionRejectsFrameworkDependentLayout()
    {
        Assert.False(UpdateLauncher.IsSingleExecutableDistribution(
            @"F:\MuseRAM\MuseRAM.exe",
            @"F:\MuseRAM\MuseRAM.dll"));
        Assert.True(UpdateLauncher.IsSingleExecutableDistribution(
            @"F:\MuseRAM\MuseRAM.exe",
            entryAssemblyLocation: string.Empty));
    }

    [Fact]
    public void CompletionArgumentsRequireTrustedPathRelationships()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"completion-{Guid.NewGuid():N}");
        var updates = Path.Combine(root, "updates");
        var target = Path.Combine(root, "MuseRAM.exe");
        var staged = Path.Combine(updates, "MuseRAM-1.2.0.exe");
        Directory.CreateDirectory(updates);
        File.WriteAllText(target, "current");
        File.WriteAllText(staged, "staged");
        try
        {
            var validArguments = new[]
            {
                "--apply-update", "123", target, target + ".rollback", updates
            };

            Assert.True(UpdateCompletionService.TryParseArguments(validArguments, staged, out var request));
            Assert.Equal(target, request!.TargetExecutable);

            var wrongBackup = validArguments.ToArray();
            wrongBackup[3] = Path.Combine(root, "unrelated.rollback");
            Assert.False(UpdateCompletionService.TryParseArguments(wrongBackup, staged, out _));

            var outsideStaged = Path.Combine(root, "outside.exe");
            File.WriteAllText(outsideStaged, "outside");
            try
            {
                Assert.False(UpdateCompletionService.TryParseArguments(validArguments, outsideStaged, out _));
            }
            finally
            {
                File.Delete(outsideStaged);
            }
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
            if (File.Exists(target)) File.Delete(target);
            if (Directory.Exists(updates)) Directory.Delete(updates);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void ReplacementBacksUpAndRestartsUpdatedExecutable()
    {
        var paths = CreateReplacementFiles();
        try
        {
            string? restartedPath = null;

            UpdateCompletionService.ReplaceAndRestart(
                paths.Request,
                path => restartedPath = path);

            Assert.Equal("staged", File.ReadAllText(paths.Target));
            Assert.Equal("current", File.ReadAllText(paths.Backup));
            Assert.Equal(paths.Target, restartedPath);
        }
        finally
        {
            DeleteReplacementFiles(paths);
        }
    }

    [Fact]
    public void ReplacementRestoresBackupWhenRestartFails()
    {
        var paths = CreateReplacementFiles();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                UpdateCompletionService.ReplaceAndRestart(
                    paths.Request,
                    _ => throw new InvalidOperationException("restart failed")));

            Assert.Equal("current", File.ReadAllText(paths.Target));
            Assert.Equal("current", File.ReadAllText(paths.Backup));
        }
        finally
        {
            DeleteReplacementFiles(paths);
        }
    }

    private static ReplacementPaths CreateReplacementFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"replacement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "MuseRAM.exe");
        var staged = Path.Combine(root, "MuseRAM-staged.exe");
        var backup = target + ".rollback";
        File.WriteAllText(target, "current");
        File.WriteAllText(staged, "staged");
        return new ReplacementPaths(
            root,
            target,
            staged,
            backup,
            new UpdateCompletionRequest(123, target, backup, root, staged));
    }

    private static void DeleteReplacementFiles(ReplacementPaths paths)
    {
        var replacement = paths.Target + ".new";
        if (File.Exists(replacement)) File.Delete(replacement);
        if (File.Exists(paths.Staged)) File.Delete(paths.Staged);
        if (File.Exists(paths.Target)) File.Delete(paths.Target);
        if (File.Exists(paths.Backup)) File.Delete(paths.Backup);
        if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root);
    }

    private sealed record ReplacementPaths(
        string Root,
        string Target,
        string Staged,
        string Backup,
        UpdateCompletionRequest Request);

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly Uri? _responseUri;

        public StaticHandler(byte[] content, Uri? responseUri = null)
        {
            _content = content;
            _responseUri = responseUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    _responseUri ?? request.RequestUri)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}

public sealed class DeepReleaseSelectionPolicyTests
{
    [Fact]
    public void SuggestedIdleApplicationIsCheckedByDefault()
    {
        var process = new ProcessSnapshot(10, "editor", @"F:\Apps\Editor\editor.exe", null, 500L * 1024 * 1024, 0, 0, false, false, true, 100);
        var family = new ProcessFamilySnapshot("editor", "Editor", @"F:\Apps\Editor", new[] { process });
        var candidate = new DeepReleaseCandidate(
            family,
            new BackgroundActivity("editor", BackgroundActivityState.Idle, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10),
            IsSuggested: true);

        Assert.True(DeepReleaseSelectionPolicy.IsCheckedByDefault(candidate));
    }
}

public sealed class CalibrationMetricsStoreTests
{
    [Fact]
    public void ExplicitDiagnosticClearDeletesOnlyCurrentAndPreviousDiagnosticFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"museram-diagnostic-clear-{Guid.NewGuid():N}");
        var metricsPath = Path.Combine(directory, "calibration-metrics.jsonl");
        var logPath = Path.Combine(directory, "logs", "museram.log");
        try
        {
            var metrics = new CalibrationMetricsStore(metricsPath);
            var log = new DiagnosticLog(logPath);
            metrics.AppendResponsivenessStall(new ResponsivenessStallCalibrationMetric(
                DateTimeOffset.UtcNow, "build", "ui", 300, false, null));
            log.Info("sample");
            File.WriteAllText(metricsPath + ".previous", "old metrics");
            File.WriteAllText(logPath + ".previous", "old log");
            var unrelatedPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(unrelatedPath, "{}");

            metrics.Delete();
            log.Delete();

            Assert.False(File.Exists(metricsPath));
            Assert.False(File.Exists(metricsPath + ".previous"));
            Assert.False(File.Exists(logPath));
            Assert.False(File.Exists(logPath + ".previous"));
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            if (File.Exists(metricsPath)) File.Delete(metricsPath);
            if (File.Exists(metricsPath + ".previous")) File.Delete(metricsPath + ".previous");
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(logPath + ".previous")) File.Delete(logPath + ".previous");
            var settingsPath = Path.Combine(directory, "settings.json");
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            var logDirectory = Path.GetDirectoryName(logPath)!;
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void ProcessIoTrackerCompressesHighActivityIntoTwoMinuteEpisodes()
    {
        var tracker = new ProcessIoCalibrationTracker();
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            ActiveIoThresholdBytesPerSecond = 100
        };
        var plan = new OptimizationPlan(
            false,
            "none",
            Array.Empty<OptimizationCandidate>(),
            OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = new[]
            {
                Evaluation(
                    "family",
                    isEligible: false,
                    targetCount: 0,
                    confidence: 90,
                    CandidateExclusionReason.CurrentIoActivity)
            }
        };
        var now = DateTimeOffset.UtcNow;

        var entered = tracker.Observe(context, now, plan, settings, IoFamily(150, 1_000, 2_000, 300, 150));
        IReadOnlyList<ProcessIoCalibrationObservation> continued = Array.Empty<ProcessIoCalibrationObservation>();
        for (var sample = 1; sample <= 39; sample++)
        {
            continued = tracker.Observe(
                context,
                now.AddSeconds(sample * 3),
                plan,
                settings,
                IoFamily(200, 1_400, 2_200, 400, 200));
            if (sample < 39) Assert.Empty(continued);
        }
        var exited = tracker.Observe(context, now.AddSeconds(123), plan, settings, IoFamily(50, 1_500, 2_250, 100, 50));
        var remainedLow = tracker.Observe(context, now.AddSeconds(126), plan, settings, IoFamily(40, 1_580, 2_290, 80, 40));

        Assert.Equal("threshold-entered", Assert.Single(entered).Metric.EventKind);
        var highSample = Assert.Single(continued).Metric;
        Assert.Equal("threshold-summary", highSample.EventKind);
        Assert.Equal(1_400UL, highSample.ReadTransferCount);
        Assert.Equal(2_200UL, highSample.WriteTransferCount);
        Assert.Equal(400UL, highSample.ReadDeltaBytes);
        Assert.Equal(200UL, highSample.WriteDeltaBytes);
        Assert.Equal(200, highSample.TotalBytesPerSecond);
        Assert.Equal(3, highSample.SampleIntervalSeconds);
        Assert.False(highSample.FamilyIsCandidate);
        Assert.Contains(nameof(CandidateExclusionReason.CurrentIoActivity), highSample.ExclusionReasons);
        Assert.Equal(120, highSample.EpisodeDurationSeconds);
        Assert.Equal(40, highSample.EpisodeSampleCount);
        Assert.Equal(198.75, highSample.EpisodeAverageBytesPerSecond);
        Assert.Equal(200, highSample.EpisodePeakBytesPerSecond);
        Assert.Equal("threshold-exited", Assert.Single(exited).Metric.EventKind);
        Assert.Empty(remainedLow);
    }

    [Fact]
    public void ProcessCpuTrackerUsesTheSameEpisodeCadence()
    {
        var tracker = new ProcessCpuCalibrationTracker();
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with { ActiveCpuThresholdPercent = 1 };
        var plan = new OptimizationPlan(false, "none", Array.Empty<OptimizationCandidate>(), OptimizationPlanOutcome.NoCandidates);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("threshold-entered", Assert.Single(tracker.Observe(
            context, now, plan, settings, IoFamily(0, 0, 0, 0, 0, cpuPercent: 2))).Metric.EventKind);
        IReadOnlyList<ProcessCpuCalibrationObservation> summary = Array.Empty<ProcessCpuCalibrationObservation>();
        for (var sample = 1; sample <= 39; sample++)
        {
            summary = tracker.Observe(
                context,
                now.AddSeconds(sample * 3),
                plan,
                settings,
                IoFamily(0, 0, 0, 0, 0, cpuPercent: 3));
            if (sample < 39) Assert.Empty(summary);
        }

        var metric = Assert.Single(summary).Metric;
        Assert.Equal("threshold-summary", metric.EventKind);
        Assert.Equal(120, metric.EpisodeDurationSeconds);
        Assert.Equal(40, metric.EpisodeSampleCount);
        Assert.Equal(2.975, metric.EpisodeAverageCpuPercent, precision: 6);
        Assert.Equal(3, metric.EpisodePeakCpuPercent);
        Assert.Equal("threshold-exited", Assert.Single(tracker.Observe(
            context, now.AddSeconds(123), plan, settings, IoFamily(0, 0, 0, 0, 0, cpuPercent: 0.2))).Metric.EventKind);
    }

    [Fact]
    public void LargeMemoryOpportunityUsesUsableThirtyGibThresholdAndTenMinuteCadence()
    {
        var now = DateTimeOffset.UtcNow;
        var eligible = new MemorySnapshot(
            30UL * 1024 * 1024 * 1024,
            18UL * 1024 * 1024 * 1024,
            40);

        Assert.True(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible,
            OptimizationPlanOutcome.LowMemoryPressure,
            lastObservedAt: null,
            now));
        Assert.False(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible,
            OptimizationPlanOutcome.LowMemoryPressure,
            now - TimeSpan.FromMinutes(9),
            now));
        Assert.True(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible,
            OptimizationPlanOutcome.LowMemoryPressure,
            now - TimeSpan.FromMinutes(10),
            now));
        Assert.False(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible with { TotalPhysicalBytes = 29UL * 1024 * 1024 * 1024 },
            OptimizationPlanOutcome.LowMemoryPressure,
            null,
            now));
        Assert.False(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible with { LoadPercent = 50 },
            OptimizationPlanOutcome.LowMemoryPressure,
            null,
            now));
        Assert.False(LargeMemoryOpportunityPolicy.ShouldObserve(
            eligible,
            OptimizationPlanOutcome.CandidatesFound,
            null,
            now));
    }

    [Fact]
    public void CandidateMetricSeparatesLegacyAndShadowIdleEligibility()
    {
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        var plan = new OptimizationPlan(
            false,
            "none",
            Array.Empty<OptimizationCandidate>(),
            OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = new[]
            {
                Evaluation("legacy", isEligible: true, targetCount: 1, confidence: 40),
                Evaluation(
                    "shadow",
                    isEligible: false,
                    targetCount: 1,
                    confidence: 80,
                    CandidateExclusionReason.BelowIdleScore),
                Evaluation(
                    "foreground",
                    isEligible: false,
                    targetCount: 0,
                    confidence: 90,
                    CandidateExclusionReason.Foreground)
            }
        };
        var families = new[]
        {
            new ProcessFamilySnapshot(
                "sample",
                "sample",
                null,
                new[]
                {
                    new ProcessSnapshot(
                        10, "sample", null, null, 100L * 1024 * 1024, 6, 2d * 1024 * 1024,
                        false, false, true, 70, HasMinimizedWindow: true),
                    new ProcessSnapshot(
                        11, "sample-helper", null, 10, 3L * 1024 * 1024, 0, 0,
                        false, false, false, 10)
                })
        };

        var metric = CandidatePlanCalibrationPolicy.Create(
            context,
            DateTimeOffset.UtcNow,
            plan,
            OptimizationSettings.For(OptimizationProfile.Turbo) with { MinimumIdleScore = 50 },
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            families,
            observedFamilyCount: 4);

        Assert.Equal(4, metric.ObservedFamilyCount);
        Assert.Equal(3, metric.EvaluatedFamilyCount);
        Assert.Equal(1, metric.EligibleFamilyCount);
        Assert.Equal(100d / 3d, metric.CandidateRatePercent, precision: 6);
        Assert.Equal(1, metric.LegacyOnlyEligibleCount);
        Assert.Equal(1, metric.ShadowOnlyEligibleCount);
        Assert.Equal(4UL * 1024 * 1024 * 1024, metric.AvailablePhysicalBytes);
        Assert.Equal(16UL * 1024 * 1024 * 1024 * 48 / 100, metric.EffectiveTriggerAvailableBytes);
        Assert.Equal(8, metric.ActiveCpuThresholdPercent);
        Assert.Equal(4d * 1024 * 1024, metric.ActiveIoThresholdBytesPerSecond);
        Assert.Equal(2, metric.Population.ProcessCount);
        Assert.Equal(1, metric.Population.ReliableActivityProcessCount);
        Assert.Equal(1, metric.Population.UnreliableActivityProcessCount);
        Assert.Equal(1, metric.Population.CpuPercentBucketCounts["4-8"]);
        Assert.Equal(1, metric.Population.IoRateBucketCounts["1-4-mib"]);
        Assert.Equal(1, metric.Population.WindowStateCounts["minimized"]);
        Assert.Equal(1, metric.LegacyOnlyExperimentalEligibleCount);
        Assert.Equal(0, metric.ExperimentalOnlyEligibleCount);
        Assert.Equal(1, metric.Population.ExperimentalIdleScoreBucketCounts["lt-20"]);
        Assert.Equal(1, metric.ExclusionReasonCounts[nameof(CandidateExclusionReason.BelowIdleScore)]);
        Assert.Equal(1, metric.ExclusionReasonCounts[nameof(CandidateExclusionReason.Foreground)]);
    }

    [Fact]
    public void CandidateMetricComparesExperimentalIdleScoreWithoutChangingThePlan()
    {
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        var plan = new OptimizationPlan(false, "none", Array.Empty<OptimizationCandidate>(), OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = new[]
            {
                Evaluation("legacy", isEligible: true, targetCount: 1, confidence: 80),
                Evaluation("experimental", isEligible: false, targetCount: 1, confidence: 20,
                    CandidateExclusionReason.BelowIdleScore)
            }
        };
        var families = new[]
        {
            FamilyWithActivity("legacy", cpuPercent: 20, ioBytesPerSecond: 4d * 1024 * 1024),
            FamilyWithActivity("experimental", cpuPercent: 0.1, ioBytesPerSecond: 0)
        };
        var activity = new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase)
        {
            ["legacy"] = new("legacy", BackgroundActivityState.Idle, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), 10),
            ["experimental"] = new("experimental", BackgroundActivityState.Idle, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), 10)
        };

        var metric = CandidatePlanCalibrationPolicy.Create(
            context,
            DateTimeOffset.UtcNow,
            plan,
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            families,
            observedFamilyCount: families.Length,
            activity: activity);

        Assert.Equal(0, metric.LegacyOnlyExperimentalEligibleCount);
        Assert.Equal(1, metric.ExperimentalOnlyEligibleCount);
        Assert.Single(metric.IdleScoreShadows.Where(shadow => shadow.SamplingReason == "Disagreement"));
        Assert.All(metric.IdleScoreShadows, shadow => Assert.Equal(1800, shadow.IdleForSeconds));
        Assert.All(metric.IdleScoreShadows, shadow => Assert.Equal(1, shadow.ReliableActivityProcessCount));
        Assert.DoesNotContain(metric.IdleScoreShadows, shadow => shadow.FamilyId is "legacy" or "experimental");
        Assert.Empty(metric.ActivityThresholdShadows);
        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void CandidateMetricControlSampleDoesNotFallBackToWholeFamilyWhenThereAreNoTargets()
    {
        const string key = "protected-family";
        var family = FamilyWithActivity(key, cpuPercent: 0.1, ioBytesPerSecond: 0);
        var plan = new OptimizationPlan(false, "none", Array.Empty<OptimizationCandidate>(), OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = new[]
            {
                new CandidateEvaluation(
                    key,
                    key,
                    false,
                    ProcessCount: 1,
                    TargetProcessCount: 0,
                    new[] { CandidateExclusionReason.Protected })
                {
                    LegacyIdleScore = 0,
                    IdleConfidenceScore = 0,
                    TargetProcessIds = Array.Empty<int>(),
                    TotalWorkingSetBytes = family.WorkingSetBytes
                }
            }
        };
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        CandidatePlanCalibrationMetric? metric = null;
        for (var bucket = 0; bucket < 100 && metric?.IdleScoreShadows.Count != 1; bucket++)
        {
            metric = CandidatePlanCalibrationPolicy.Create(
                context,
                DateTimeOffset.UnixEpoch.AddMinutes(bucket * 15),
                plan,
                OptimizationSettings.For(OptimizationProfile.Turbo),
                new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
                new[] { family },
                observedFamilyCount: 1);
        }

        var shadow = Assert.Single(Assert.IsType<CandidatePlanCalibrationMetric>(metric).IdleScoreShadows);
        Assert.Equal("ControlSample", shadow.SamplingReason);
        Assert.Equal(0, shadow.LegacyIdleScore);
        Assert.Equal(0, shadow.ExperimentalIdleScore);
        Assert.Equal(0, shadow.TargetProcessCount);
        Assert.Equal(0, shadow.TargetWorkingSetBytes);
        Assert.Equal(family.WorkingSetBytes, shadow.TotalWorkingSetBytes);
    }

    [Fact]
    public void CandidateMetricDoesNotTreatZeroTargetScoresAsFormulaDisagreements()
    {
        const string key = "zero-target";
        var family = FamilyWithActivity(key, cpuPercent: 0.1, ioBytesPerSecond: 0);
        var plan = new OptimizationPlan(false, "none", Array.Empty<OptimizationCandidate>(), OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = new[]
            {
                new CandidateEvaluation(
                    key,
                    key,
                    false,
                    ProcessCount: 1,
                    TargetProcessCount: 0,
                    new[] { CandidateExclusionReason.Protected })
                {
                    LegacyIdleScore = 80,
                    IdleConfidenceScore = 80,
                    TargetProcessIds = Array.Empty<int>(),
                    TotalWorkingSetBytes = family.WorkingSetBytes
                }
            }
        };
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        CandidatePlanCalibrationMetric? metric = null;
        for (var bucket = 0; bucket < 100 && metric?.IdleScoreShadows.Count != 1; bucket++)
        {
            metric = CandidatePlanCalibrationPolicy.Create(
                context,
                DateTimeOffset.UnixEpoch.AddMinutes(bucket * 15),
                plan,
                OptimizationSettings.For(OptimizationProfile.Turbo) with { MinimumIdleScore = 50 },
                new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
                new[] { family },
                observedFamilyCount: 1);
        }

        var shadow = Assert.Single(Assert.IsType<CandidatePlanCalibrationMetric>(metric).IdleScoreShadows);
        Assert.Equal("ControlSample", shadow.SamplingReason);
        Assert.Empty(shadow.ProcessInputs);
    }

    [Fact]
    public void LongIdleFilterUsesTheSameIdleWindowAndCandidateLimitForEveryPlan()
    {
        var older = FamilyWithActivity("older", cpuPercent: 0.1, ioBytesPerSecond: 0);
        var newer = FamilyWithActivity("newer", cpuPercent: 0.1, ioBytesPerSecond: 0);
        var plan = new OptimizationPlan(
            true,
            "candidates",
            new[]
            {
                new OptimizationCandidate(older, older.Processes, 80, older.WorkingSetBytes, "older"),
                new OptimizationCandidate(newer, newer.Processes, 80, newer.WorkingSetBytes, "newer")
            },
            OptimizationPlanOutcome.CandidatesFound);
        var activity = new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase)
        {
            [older.Key] = new(older.Key, BackgroundActivityState.Idle, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), 20),
            [newer.Key] = new(newer.Key, BackgroundActivityState.Idle, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), 4)
        };

        var filtered = CandidatePlanCalibrationPolicy.ApplyLongIdleFilter(
            plan,
            activity,
            TimeSpan.FromMinutes(30),
            maxApplications: 1);

        Assert.True(filtered.ShouldRun);
        Assert.Equal(OptimizationPlanOutcome.CandidatesFound, filtered.Outcome);
        Assert.Equal("older", Assert.Single(filtered.Candidates).Family.Key);
    }

    [Fact]
    public void CandidateMetricRecordsOnlyIdleScoreDisagreementsOrNearThresholdFamilies()
    {
        const string disagreementKey = @"directory:c:\private\disagreement";
        const string nearKey = @"directory:c:\private\near";
        const string farKey = @"directory:c:\private\far";
        var disagreementFamily = FamilyWithActivity(
            disagreementKey,
            cpuPercent: 0.1,
            ioBytesPerSecond: 0,
            idleScore: 100);
        var nearFamily = new ProcessFamilySnapshot(
            nearKey,
            "near-display-name",
            @"c:\private\near",
            new[]
            {
                new ProcessSnapshot(
                    21001, "near.exe", @"c:\private\near\near.exe", null,
                    100L * 1024 * 1024, 1, 128d * 1024,
                    false, false, true, 52, StartTimeFileTimeUtc: 123456)
            });
        var farFamily = FamilyWithActivity(farKey, cpuPercent: 0.1, ioBytesPerSecond: 0);
        var selected = new OptimizationCandidate(
            nearFamily,
            nearFamily.Processes,
            IdleConfidenceScore: 52,
            PotentialReleaseBytes: nearFamily.WorkingSetBytes,
            Reason: "selected");
        var plan = new OptimizationPlan(
            true,
            "candidate",
            new[] { selected },
            OptimizationPlanOutcome.CandidatesFound)
        {
            CandidateEvaluations = new[]
            {
                new CandidateEvaluation(
                    disagreementKey, "private-disagreement", false, 1, 1,
                    new[] { CandidateExclusionReason.BelowIdleScore })
                {
                    LegacyIdleScore = 20,
                    IdleConfidenceScore = 20,
                    TargetProcessIds = new[] { 10544 }
                },
                new CandidateEvaluation(nearKey, "private-near", true, 1, 1, Array.Empty<CandidateExclusionReason>())
                {
                    LegacyIdleScore = 52,
                    IdleConfidenceScore = 52,
                    TargetProcessIds = new[] { 21001 }
                },
                new CandidateEvaluation(farKey, "private-far", true, 1, 1, Array.Empty<CandidateExclusionReason>())
                {
                    LegacyIdleScore = 80,
                    IdleConfidenceScore = 80,
                    TargetProcessIds = new[] { 10544 }
                }
            }
        };
        var activity = new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase)
        {
            [disagreementKey] = new(disagreementKey, BackgroundActivityState.Idle, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), 10),
            [nearKey] = new(nearKey, BackgroundActivityState.Idle, TimeSpan.Zero, TimeSpan.Zero, 1),
            [farKey] = new(farKey, BackgroundActivityState.Idle, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), 10)
        };

        var metric = CandidatePlanCalibrationPolicy.Create(
            new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3"),
            DateTimeOffset.UnixEpoch,
            plan,
            OptimizationSettings.For(OptimizationProfile.Turbo) with { MinimumIdleScore = 50 },
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { disagreementFamily, nearFamily, farFamily },
            observedFamilyCount: 3,
            activity: activity);

        Assert.Equal(2, metric.IdleScoreShadows.Count);
        var disagreement = Assert.Single(metric.IdleScoreShadows.Where(shadow => shadow.LegacyIdleScore == 20));
        Assert.Equal(
            LocalIdleScoreShadowPolicy.Calculate(disagreementFamily, TimeSpan.FromMinutes(30)),
            disagreement.ExperimentalIdleScore);
        Assert.False(disagreement.LegacyMeetsThreshold);
        Assert.False(disagreement.IdleConfidenceMeetsThreshold);
        Assert.True(disagreement.ExperimentalMeetsThreshold);
        Assert.False(disagreement.ActualPolicyEligible);
        Assert.False(disagreement.SelectedForPlan);
        Assert.Equal(new[] { nameof(CandidateExclusionReason.BelowIdleScore) }, disagreement.ExclusionReasons);

        var near = Assert.Single(metric.IdleScoreShadows.Where(shadow => shadow.LegacyIdleScore == 52));
        Assert.Equal(
            LocalIdleScoreShadowPolicy.Calculate(nearFamily, TimeSpan.Zero),
            near.ExperimentalIdleScore);
        Assert.Equal(52, near.LegacyIdleScore);
        Assert.Equal(50, near.IdleThreshold);
        Assert.True(near.ActualPolicyEligible);
        Assert.True(near.SelectedForPlan);
        Assert.Equal(1, near.ProcessCount);
        Assert.Equal(1, near.ReliableActivityProcessCount);
        Assert.Equal(1, near.TargetProcessCount);
        Assert.Equal(1, near.ReliableTargetProcessCount);
        Assert.Equal(near.WorkingSetBytes, near.TargetWorkingSetBytes);
        Assert.Equal(near.WorkingSetBytes, near.TotalWorkingSetBytes);
        Assert.Equal("NearThreshold", near.SamplingReason);
        Assert.Equal(1, near.MaximumReliableProcessCpuPercent);
        Assert.Equal(128d * 1024, near.MaximumReliableProcessIoBytesPerSecond);
        Assert.Equal(100L * 1024 * 1024, near.WorkingSetBytes);
        Assert.False(near.HasForegroundProcess);
        Assert.False(near.HasVisibleWindow);
        Assert.DoesNotContain(metric.IdleScoreShadows, shadow => shadow.FamilyId.Contains("private", StringComparison.OrdinalIgnoreCase));

        var repeated = CandidatePlanCalibrationPolicy.Create(
            metric.RunContext,
            metric.RecordedAt,
            plan,
            OptimizationSettings.For(OptimizationProfile.Turbo) with { MinimumIdleScore = 50 },
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { disagreementFamily, nearFamily, farFamily },
            observedFamilyCount: 3,
            activity: activity);
        Assert.Equal(
            metric.IdleScoreShadows.Select(shadow => shadow.FamilyId),
            repeated.IdleScoreShadows.Select(shadow => shadow.FamilyId));
    }

    [Fact]
    public void ActivityThresholdShadowTrackerSharesOneFormalInputHistory()
    {
        var tracker = new ActivityThresholdShadowTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var family = FamilyWithActivity("family", cpuPercent: 1, ioBytesPerSecond: 128 * 1024);
        var now = DateTimeOffset.UtcNow;

        var first = tracker.Observe(new[] { family }, now, settings);
        var second = tracker.Observe(new[] { family }, now.AddSeconds(3), settings);

        Assert.Equal(5, first.Count);
        Assert.Equal(
            new[] { "turbo-baseline", "turbo-cpu-7.5", "turbo-cpu-8.5", "turbo-io-3.6mib", "turbo-io-4.4mib" },
            first.Select(state => state.Experiment.Key));
        Assert.All(second, state => Assert.Equal(2, state.Activity["family"].SampleCount));
        Assert.All(second, state => Assert.True(state.CandidateIdleReadiness[10544].IsReady));
    }

    [Fact]
    public void WritesStructuredPlanAndAnonymizedOutcomeEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"museram-calibration-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new CalibrationMetricsStore(path);
            var context = new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3")
            {
                RunId = "run-1"
            };
            const string familyKey = @"directory:c:\users\private\edge";
            store.AppendCandidatePlan(new CandidatePlanCalibrationMetric(
                context,
                DateTimeOffset.UtcNow,
                OptimizationPlanOutcome.CandidatesFound,
                TotalPhysicalBytes: 16UL * 1024 * 1024 * 1024,
                AvailablePhysicalBytes: 4UL * 1024 * 1024 * 1024,
                MemoryLoadPercent: 75,
                EffectiveTriggerAvailableBytes: 8UL * 1024 * 1024 * 1024,
                ObservedFamilyCount: 10,
                EvaluatedFamilyCount: 8,
                EligibleFamilyCount: 2,
                SelectedFamilyCount: 2,
                CandidateRatePercent: 25,
                MaxApplications: 7,
                MinimumFamilyWorkingSetBytes: 96L * 1024 * 1024,
                MinimumProcessWorkingSetBytes: 8L * 1024 * 1024,
                LegacyIdleThreshold: 45,
                ActiveCpuThresholdPercent: 8,
                ActiveIoThresholdBytesPerSecond: 4d * 1024 * 1024,
                VisibleWindowIdleDelaySeconds: 120,
                ProcessCooldownSeconds: 18,
                AutoCooldownSeconds: 90,
                IgnoreMemoryPressureThreshold: false,
                QuickCandidateSelection: false,
                LegacyOnlyEligibleCount: 1,
                ShadowOnlyEligibleCount: 0,
                Population: EmptyPopulation(),
                ExclusionReasonCounts: new Dictionary<string, int> { [nameof(CandidateExclusionReason.Foreground)] = 3 })
            {
                LegacyOnlyExperimentalEligibleCount = 2,
                ExperimentalOnlyEligibleCount = 1,
                IdleScoreShadows = new[]
                {
                    new IdleScoreShadowMetric(
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(familyKey)).AsSpan(0, 12)),
                        LegacyIdleScore: 48, IdleConfidenceScore: 44, ExperimentalIdleScore: 62,
                        IdleThreshold: 50, LegacyMeetsThreshold: false,
                        IdleConfidenceMeetsThreshold: false, ExperimentalMeetsThreshold: true,
                        ActualPolicyEligible: false, SelectedForPlan: false, IdleForSeconds: 900,
                        MaximumReliableProcessCpuPercent: 0.5,
                        MaximumReliableProcessIoBytesPerSecond: 32768,
                        HasForegroundProcess: false, HasVisibleWindow: true,
                        WorkingSetBytes: 512L * 1024 * 1024, ProcessCount: 3,
                        ReliableActivityProcessCount: 2,
                        ExclusionReasons: new[] { nameof(CandidateExclusionReason.BelowIdleScore) })
                    {
                        TargetWorkingSetBytes = 400L * 1024 * 1024,
                        TotalWorkingSetBytes = 512L * 1024 * 1024,
                        TargetProcessCount = 2,
                        ReliableTargetProcessCount = 2,
                        SamplingReason = "Disagreement",
                        ProcessInputs = new[]
                        {
                            new IdleScoreProcessInputMetric(
                                WorkingSetBytes: 400L * 1024 * 1024,
                                CpuPercent: 0.5,
                                IoBytesPerSecond: 32768,
                                IsForeground: false,
                                HasVisibleWindow: true,
                                HasReliableActivitySample: true,
                                FormalIdleScore: 62)
                        }
                    }
                },
                ActivityThresholdShadows = new[]
                {
                    new ActivityThresholdShadowMetric(
                        "cpu-8-io-4mib", 8, 4d * 1024 * 1024,
                        EligibleFamilyCount: 3, SelectedFamilyCount: 3,
                        TargetProcessCount: 8, PotentialReleaseBytes: 900,
                        AddedCandidateCount: 1, RemovedCandidateCount: 0,
                        CpuBlockedFamilyCount: 4, IoBlockedFamilyCount: 2)
                }
            });
            store.AppendApplicationOutcome(new ApplicationReboundOutcome(
                context,
                familyKey,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddSeconds(120),
                ObservationWindowSeconds: 120,
                ReleasedBytes: 400,
                RegainedBytes: 100,
                RetainedBytes: 300,
                ReboundPercent: 25,
                BackoffTriggered: false,
                TimeToForeground: TimeSpan.FromSeconds(9))
            {
                LateWorkingSetBytes = 240
            });
            store.AppendMonitoring(new MonitoringCalibrationMetric(
                DateTimeOffset.UtcNow,
                ProcessCaptureMilliseconds: 12.5,
                ConfiguredMonitoringIntervalSeconds: 3,
                ProcessCount: 120,
                FamilyCount: 40,
                AutomaticOptimizationEnabled: true,
                AppWorkingSetBytes: 80L * 1024 * 1024,
                AppPrivateMemoryBytes: 60L * 1024 * 1024,
                AppCpuPercent: 0.2,
                AppIoBytesPerSecond: 1024,
                AppThreadCount: 12,
                AppHandleCount: 300,
                HasReliableAppCpuSample: true,
                HasReliableAppIoSample: true));
            store.AppendCandidateTransition(familyKey, new CandidateTransitionCalibrationMetric(
                context, DateTimeOffset.UtcNow, string.Empty, EnteredCandidate: false,
                ProcessCount: 19, ReliableProcessCount: 18,
                FamilyCpuPercent: 0.2, MaximumProcessCpuPercent: 0.1,
                FamilyIoBytesPerSecond: 6 * 1024 * 1024,
                MaximumProcessIoBytesPerSecond: 5 * 1024 * 1024,
                MaximumIoProcessId: 10544,
                MaximumProcessIoReadBytesPerSecond: 3 * 1024 * 1024,
                MaximumProcessIoWriteBytesPerSecond: 2 * 1024 * 1024,
                MaximumProcessIoSampleIntervalSeconds: 3.01,
                HasForegroundProcess: false, HasVisibleWindow: true,
                ActiveCpuThresholdPercent: 8,
                ActiveIoThresholdBytesPerSecond: 4 * 1024 * 1024,
                ExclusionReasons: new[] { nameof(CandidateExclusionReason.CurrentIoActivity) }));
            store.AppendProcessIoSample(familyKey, new ProcessIoCalibrationMetric(
                context, DateTimeOffset.UtcNow, string.Empty, "threshold-entered",
                ProcessId: 10544, ProcessStartTimeFileTimeUtc: 123456,
                ReadTransferCount: 9000, WriteTransferCount: 5000,
                ReadDeltaBytes: 3000, WriteDeltaBytes: 2000,
                ReadBytesPerSecond: 3 * 1024 * 1024,
                WriteBytesPerSecond: 2 * 1024 * 1024,
                TotalBytesPerSecond: 5 * 1024 * 1024,
                SampleIntervalSeconds: 3.01,
                ProcessCpuPercent: 0.2, ProcessIsForeground: false,
                FamilyIoBytesPerSecond: 6 * 1024 * 1024,
                FamilyHasForegroundProcess: false,
                ActiveIoThresholdBytesPerSecond: 4 * 1024 * 1024,
                FamilyIsCandidate: false, ProcessIsCandidateTarget: false,
                ExclusionReasons: new[] { nameof(CandidateExclusionReason.CurrentIoActivity) }));
            store.AppendProcessCpuSample(familyKey, new ProcessCpuCalibrationMetric(
                context, DateTimeOffset.UtcNow, string.Empty, "threshold-entered",
                ProcessId: 10544, ProcessStartTimeFileTimeUtc: 123456,
                ProcessCpuPercent: 12, ProcessIsForeground: false,
                FamilyCpuPercent: 14, FamilyHasForegroundProcess: false,
                ActiveCpuThresholdPercent: 8,
                FamilyIsCandidate: false, ProcessIsCandidateTarget: false,
                ExclusionReasons: new[] { nameof(CandidateExclusionReason.CurrentCpuActivity) },
                EpisodeDurationSeconds: 3, EpisodeSampleCount: 1,
                EpisodeAverageCpuPercent: 12, EpisodePeakCpuPercent: 12));
            store.AppendLargeMemoryOpportunity(new LargeMemoryOpportunityCalibrationMetric(
                context,
                DateTimeOffset.UtcNow,
                TotalPhysicalBytes: 31UL * 1024 * 1024 * 1024,
                AvailablePhysicalBytes: 19UL * 1024 * 1024 * 1024,
                MemoryLoadPercent: 39,
                EvaluatedFamilyCount: 40,
                EligibleFamilyCount: 3,
                PotentialReleaseBytes: 2L * 1024 * 1024 * 1024,
                ExclusionReasonCounts: new Dictionary<string, int>()));
            store.AppendOptimizationProcess(familyKey, new OptimizationProcessCalibrationMetric(
                context, "run-1", "build-1", DateTimeOffset.UtcNow, string.Empty,
                ProcessIndex: 1, ProcessCount: 2, Success: true, Skipped: false,
                SetProcessWorkingSetSucceeded: true, SetProcessWorkingSetErrorCode: null,
                EmptyWorkingSetSucceeded: false, EmptyWorkingSetErrorCode: 5,
                IdleConfidenceScore: 92, IdleState: "Idle", IdleSeconds: 3600,
                WasForeground: false, HadVisibleWindow: false, SafetyScopeProcessCount: 3,
                WorkingSetBeforeBytes: 500, WorkingSetAfterBytes: 200, PageFaultCountDelta: 12,
                TotalMilliseconds: 95, OpenProcessMilliseconds: 1, IdentityCheckMilliseconds: 2,
                RelationshipCheckMilliseconds: 3, SetProcessWorkingSetMilliseconds: 4,
                EmptyWorkingSetMilliseconds: 5, MeasurementMilliseconds: 80,
                UiDispatchDelayMilliseconds: 6));
            store.AppendOptimizationRun(new OptimizationRunCalibrationMetric(
                context, "run-1", "build-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                SnapshotAlreadyRefreshed: false, CandidateCount: 1, TargetProcessCount: 2,
                SucceededProcessCount: 1, SkippedProcessCount: 1, FailedProcessCount: 0,
                MemoryLoadPercentBefore: 90, AvailablePhysicalBytesBefore: 100,
                MemoryLoadPercentAfter: 80, AvailablePhysicalBytesAfter: 200,
                SnapshotMilliseconds: 20, PlanningMilliseconds: 4, ExecutionMilliseconds: 100,
                CompletionMilliseconds: 3, MaximumUiDispatchDelayMilliseconds: 7,
                AppAverageCpuPercent: 1.5, AppPeakCpuPercent: 8.5,
                SystemPageFaultCountDelta: 40, SystemPageReadCountDelta: 6,
                SystemPageReadIoCountDelta: 2));
            store.AppendResponsivenessStall(new ResponsivenessStallCalibrationMetric(
                DateTimeOffset.UtcNow, "build-1", "ui", 450, true, "run-1"));

            var text = File.ReadAllText(path);
            var lines = File.ReadAllLines(path);

            Assert.Equal(10, lines.Length);
            Assert.Contains("\"SchemaVersion\":10", lines[0]);
            using var firstEnvelope = JsonDocument.Parse(lines[0]);
            using var secondEnvelope = JsonDocument.Parse(lines[1]);
            var firstRoot = firstEnvelope.RootElement;
            var secondRoot = secondEnvelope.RootElement;
            Assert.False(string.IsNullOrWhiteSpace(firstRoot.GetProperty("SessionId").GetString()));
            Assert.Equal(firstRoot.GetProperty("SessionId").GetString(), secondRoot.GetProperty("SessionId").GetString());
            Assert.Equal(1, firstRoot.GetProperty("Sequence").GetInt64());
            Assert.Equal(2, secondRoot.GetProperty("Sequence").GetInt64());
            Assert.NotEqual(default, firstRoot.GetProperty("WrittenAtUtc").GetDateTimeOffset());
            Assert.Equal("run-1", firstRoot.GetProperty("Payload").GetProperty("RunContext").GetProperty("RunId").GetString());
            Assert.Contains("\"Kind\":\"candidate-plan\"", lines[0]);
            Assert.Contains("\"CandidateRatePercent\":25", lines[0]);
            Assert.Contains("\"LegacyOnlyExperimentalEligibleCount\":2", lines[0]);
            Assert.Contains("\"ExperimentalIdleScore\":62", lines[0]);
            Assert.Contains("\"ReliableActivityProcessCount\":2", lines[0]);
            Assert.Contains("\"TargetProcessCount\":2", lines[0]);
            Assert.Contains("\"SamplingReason\":\"Disagreement\"", lines[0]);
            Assert.Contains("\"ProcessInputs\":[{", lines[0]);
            Assert.Contains("\"FormalIdleScore\":62", lines[0]);
            Assert.Contains("\"Key\":\"cpu-8-io-4mib\"", lines[0]);
            Assert.Contains("\"Kind\":\"application-outcome\"", lines[1]);
            Assert.Contains("\"RetainedBytes\":300", lines[1]);
            Assert.Contains("\"TimeToForegroundSeconds\":9", lines[1]);
            Assert.Contains("\"LateWorkingSetBytes\":240", lines[1]);
            Assert.Contains("\"Kind\":\"monitoring\"", lines[2]);
            Assert.Contains("\"ProcessCaptureMilliseconds\":12.5", lines[2]);
            Assert.Contains("\"Kind\":\"candidate-transition\"", lines[3]);
            Assert.Contains("\"MaximumProcessIoBytesPerSecond\":5242880", lines[3]);
            Assert.Contains("\"MaximumIoProcessId\":10544", lines[3]);
            Assert.Contains("\"MaximumProcessIoReadBytesPerSecond\":3145728", lines[3]);
            Assert.Contains("\"MaximumProcessIoWriteBytesPerSecond\":2097152", lines[3]);
            Assert.Contains("\"MaximumProcessIoSampleIntervalSeconds\":3.01", lines[3]);
            Assert.Contains("\"CurrentIoActivity\"", lines[3]);
            Assert.Contains("\"Kind\":\"process-io-sample\"", lines[4]);
            Assert.Contains("\"EventKind\":\"threshold-entered\"", lines[4]);
            Assert.Contains("\"ReadTransferCount\":9000", lines[4]);
            Assert.Contains("\"ReadDeltaBytes\":3000", lines[4]);
            Assert.Contains("\"Kind\":\"process-cpu-sample\"", lines[5]);
            Assert.Contains("\"EpisodeAverageCpuPercent\":12", lines[5]);
            Assert.Contains("\"Kind\":\"large-memory-opportunity\"", lines[6]);
            Assert.Contains("\"PotentialReleaseBytes\":2147483648", lines[6]);
            Assert.Contains("\"Kind\":\"optimization-process\"", lines[7]);
            Assert.Contains("\"SetProcessWorkingSetMilliseconds\":4", lines[7]);
            Assert.Contains("\"EmptyWorkingSetErrorCode\":5", lines[7]);
            Assert.Contains("\"IdleSeconds\":3600", lines[7]);
            Assert.Contains("\"SafetyScopeProcessCount\":3", lines[7]);
            Assert.Contains("\"PageFaultCountDelta\":12", lines[7]);
            Assert.Contains("\"Kind\":\"optimization-run\"", lines[8]);
            Assert.Contains("\"MaximumUiDispatchDelayMilliseconds\":7", lines[8]);
            Assert.Contains("\"AppPeakCpuPercent\":8.5", lines[8]);
            Assert.Contains("\"SystemPageReadCountDelta\":6", lines[8]);
            Assert.Contains("\"Kind\":\"responsiveness-stall\"", lines[9]);
            Assert.Contains("\"Source\":\"ui\"", lines[9]);
            Assert.DoesNotContain(familyKey, text, StringComparison.OrdinalIgnoreCase);
            using var candidateDocument = JsonDocument.Parse(lines[0]);
            using var outcomeDocument = JsonDocument.Parse(lines[1]);
            var candidateFamilyId = candidateDocument.RootElement
                .GetProperty("Payload")
                .GetProperty("IdleScoreShadows")[0]
                .GetProperty("FamilyId")
                .GetString();
            var outcomeFamilyId = outcomeDocument.RootElement
                .GetProperty("Payload")
                .GetProperty("FamilyId")
                .GetString();
            Assert.Equal(outcomeFamilyId, candidateFamilyId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".previous")) File.Delete(path + ".previous");
        }
    }

    private static ProcessPopulationCalibrationMetric EmptyPopulation()
    {
        var empty = new Dictionary<string, int>();
        return new ProcessPopulationCalibrationMetric(
            0, 0, 0, 0, empty, empty, empty, empty, empty, empty, empty);
    }

    private static IReadOnlyList<ProcessFamilySnapshot> IoFamily(
        double totalBytesPerSecond,
        ulong readTransferCount,
        ulong writeTransferCount,
        ulong readDeltaBytes,
        ulong writeDeltaBytes,
        double cpuPercent = 0.2) =>
        new[]
        {
            new ProcessFamilySnapshot(
                "family",
                "sample",
                null,
                new[]
                {
                    new ProcessSnapshot(
                        10544, "sample", null, null, 200L * 1024 * 1024, cpuPercent,
                        totalBytesPerSecond, false, false, true, 90,
                        StartTimeFileTimeUtc: 123456)
                    {
                        IoReadTransferCount = readTransferCount,
                        IoWriteTransferCount = writeTransferCount,
                        IoReadDeltaBytes = readDeltaBytes,
                        IoWriteDeltaBytes = writeDeltaBytes,
                        IoReadBytesPerSecond = totalBytesPerSecond * 2 / 3,
                        IoWriteBytesPerSecond = totalBytesPerSecond / 3,
                        IoSampleIntervalSeconds = 3
                    }
                })
        };

    private static ProcessFamilySnapshot FamilyWithActivity(
        string key,
        double cpuPercent,
        double ioBytesPerSecond,
        double idleScore = 80) => new(
            key,
            key,
            null,
            new[]
            {
                new ProcessSnapshot(
                    10544,
                    key,
                    null,
                    null,
                    512L * 1024 * 1024,
                    cpuPercent,
                    ioBytesPerSecond,
                    false,
                    false,
                    true,
                    idleScore,
                    StartTimeFileTimeUtc: 123456)
                {
                    IoSampleIntervalSeconds = 3
                }
            });

    private static CandidateEvaluation Evaluation(
        string key,
        bool isEligible,
        int targetCount,
        double confidence,
        params CandidateExclusionReason[] reasons) => new(
            key,
            key,
            isEligible,
            ProcessCount: 1,
            TargetProcessCount: targetCount,
            reasons)
        {
            LegacyIdleScore = isEligible ? 80 : 20,
            IdleConfidenceScore = confidence,
            TargetProcessIds = targetCount > 0 ? new[] { 10544 } : Array.Empty<int>()
        };
}
