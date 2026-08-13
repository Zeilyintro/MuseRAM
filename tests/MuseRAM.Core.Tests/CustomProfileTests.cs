using MuseRAM.Core;

namespace MuseRAM.Core.Tests;

public sealed class CustomProfileTests
{
    [Fact]
    public void StableSuppressionProfilesAreNamedNormalizedCopiesOfBuiltInPresets()
    {
        var profile = CustomStableStateSuppressionProfilePolicy.Create(
            OptimizationProfile.Lite,
            " Quiet memory ",
            0);
        profile.Settings = new StableStateSuppressionSettings(
            99,
            TimeSpan.FromDays(999),
            9,
            long.MaxValue)
        {
            MaximumStableValidationDuration = TimeSpan.FromMinutes(99),
            NaturalStableSampleInterval = TimeSpan.FromMinutes(999),
            MaximumStableSamplesPerLaunch = 99,
            MaximumStableSamplePool = 999
        };

        var normalized = CustomStableStateSuppressionProfilePolicy.Normalize(profile);

        Assert.Equal("Quiet memory", normalized.Name);
        Assert.Equal(OptimizationProfile.Lite, normalized.BaseProfile);
        Assert.Equal(20, normalized.Settings.MinimumSamples);
        Assert.Equal(TimeSpan.FromDays(90), normalized.Settings.MaximumRecordAge);
        Assert.Equal(1.5d, normalized.Settings.RelativeGrowthMargin);
        Assert.Equal(1024L * 1024 * 1024, normalized.Settings.AbsoluteGrowthMarginBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), normalized.Settings.MaximumStableValidationDuration);
        Assert.Equal(TimeSpan.FromMinutes(60), normalized.Settings.NaturalStableSampleInterval);
        Assert.Equal(3, normalized.Settings.MaximumStableSamplesPerLaunch);
        Assert.Equal(100, normalized.Settings.MaximumStableSamplePool);
    }

    [Fact]
    public void StableSuppressionProfileNamesRejectBuiltInAndDuplicateNames()
    {
        var profiles = new[]
        {
            CustomStableStateSuppressionProfilePolicy.Create(
                OptimizationProfile.Turbo,
                "Streaming",
                0)
        };

        Assert.False(CustomStableStateSuppressionProfilePolicy.IsUniqueName(profiles, " streaming "));
        Assert.False(CustomStableStateSuppressionProfilePolicy.IsUniqueName(profiles, "Turbo"));
        Assert.True(CustomStableStateSuppressionProfilePolicy.IsUniqueName(profiles, "Background apps"));
    }

    [Fact]
    public void CopyingBuiltInProfileCreatesEditableNonZeroSettingsInsideTemplateBounds()
    {
        var lite = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        var ultimate = CustomProfilePolicy.Create(OptimizationProfile.Ultimate, "Maximum", 1);

        Assert.Equal(OptimizationProfile.Lite, lite.BaseProfile);
        Assert.InRange(lite.Settings.MaxApplications, 1, 40);
        Assert.True(lite.Settings.MinimumFamilyWorkingSetBytes >= 96L * 1024 * 1024);
        Assert.Equal(TimeSpan.FromMinutes(10), lite.Settings.VisibleWindowIdleDelay);
        Assert.InRange(ultimate.Settings.MaxApplications, 7, 40);
        Assert.True(ultimate.Settings.TriggerAvailableBytes > 0);
        Assert.True(ultimate.Settings.TriggerAvailablePercent > 0);
        Assert.True(ultimate.Settings.ProcessCooldown > TimeSpan.Zero);
        Assert.Equal(0.50d, lite.StableStateSuppression.RelativeGrowthMargin);
        Assert.Equal(64L * 1024 * 1024, ultimate.StableStateSuppression.AbsoluteGrowthMarginBytes);
        Assert.Equal(1024L * 1024 * 1024, lite.StableStateSuppression.MaximumStableWorkingSetBytes);
        Assert.Equal(768L * 1024 * 1024,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo).MaximumStableWorkingSetBytes);
        Assert.Equal(256L * 1024 * 1024, ultimate.StableStateSuppression.MaximumStableWorkingSetBytes);
    }

    [Fact]
    public void CopyingCustomOptimizationProfileCreatesAnIndependentIdAndValueCopy()
    {
        var source = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Source", 0);
        source.Settings = source.Settings with { MaxApplications = 11 };
        source.Rebound = source.Rebound with { LateReboundPercent = 61 };

        var copy = CustomProfilePolicy.Copy(source, "Source copy", 1);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Source copy", copy.Name);
        Assert.Equal(1, copy.SortOrder);
        Assert.Equal(source.Settings, copy.Settings);
        Assert.Equal(source.Rebound, copy.Rebound);
        Assert.Equal(source.StableStateSuppression, copy.StableStateSuppression);

        copy.Settings = copy.Settings with { MaxApplications = 12 };
        copy.Rebound = copy.Rebound with { LateReboundPercent = 62 };

        Assert.Equal(11, source.Settings.MaxApplications);
        Assert.Equal(61, source.Rebound.LateReboundPercent);
    }

    [Fact]
    public void CopyingCustomStableSuppressionProfileCreatesAnIndependentIdAndValueCopy()
    {
        var source = CustomStableStateSuppressionProfilePolicy.Create(
            OptimizationProfile.Lite,
            "Source",
            0);
        source.Settings = source.Settings with { MinimumSamples = 7 };

        var copy = CustomStableStateSuppressionProfilePolicy.Copy(source, "Source copy", 1);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Source copy", copy.Name);
        Assert.Equal(1, copy.SortOrder);
        Assert.Equal(source.BaseProfile, copy.BaseProfile);
        Assert.Equal(source.Settings, copy.Settings);

        copy.Settings = copy.Settings with { MinimumSamples = 8 };

        Assert.Equal(7, source.Settings.MinimumSamples);
    }

    [Theory]
    [InlineData(OptimizationProfile.Turbo)]
    [InlineData(OptimizationProfile.Ultimate)]
    public void TurboAndUltimateCustomProfilesAllowTwoMiBFamilyThreshold(
        OptimizationProfile baseProfile)
    {
        var profile = CustomProfilePolicy.Create(baseProfile, "Low threshold", 0);
        profile.Settings = profile.Settings with { MinimumFamilyWorkingSetBytes = 1 };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(2L * 1024 * 1024, normalized.Settings.MinimumFamilyWorkingSetBytes);
    }

    [Fact]
    public void NormalizeClampsUnsafeValuesAndKeepsLateReboundThresholdStricter()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        profile.Settings = profile.Settings with
        {
            MaxApplications = 99,
            MinimumFamilyWorkingSetBytes = 1,
            IgnoreMemoryPressureThreshold = true,
            AllowForegroundProcessTrim = true,
            ProcessCooldown = TimeSpan.Zero,
            VisibleWindowIdleDelay = TimeSpan.FromMinutes(90),
            ActiveCpuThresholdPercent = 100,
            ActiveIoThresholdBytesPerSecond = 100d * 1024 * 1024
        };
        profile.Rebound = new ReboundBackoffSettings(
            TimeSpan.FromSeconds(2),
            88,
            TimeSpan.FromSeconds(3),
            40,
            TimeSpan.Zero,
            TimeSpan.Zero);
        profile.StableStateSuppression = new StableStateSuppressionSettings(
            99,
            TimeSpan.FromDays(999),
            9,
            long.MaxValue)
        {
            MaximumStableValidationDuration = TimeSpan.Zero,
            NaturalStableSampleInterval = TimeSpan.Zero,
            MaximumStableSamplesPerLaunch = 0,
            MaximumStableSamplePool = 0
        };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(40, normalized.Settings.MaxApplications);
        Assert.Equal(96L * 1024 * 1024, normalized.Settings.MinimumFamilyWorkingSetBytes);
        Assert.False(normalized.Settings.IgnoreMemoryPressureThreshold);
        Assert.False(normalized.Settings.AllowForegroundProcessTrim);
        Assert.True(normalized.Settings.ProcessCooldown > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(15), normalized.Settings.VisibleWindowIdleDelay);
        Assert.Equal(15, normalized.Settings.ActiveCpuThresholdPercent);
        Assert.Equal(8d * 1024 * 1024, normalized.Settings.ActiveIoThresholdBytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), normalized.Rebound.EarlyWindow);
        Assert.Equal(TimeSpan.FromSeconds(20), normalized.Rebound.LateWindow);
        Assert.Equal(88, normalized.Rebound.LateReboundPercent);
        Assert.Equal(TimeSpan.FromMinutes(1), normalized.Rebound.FirstBackoff);
        Assert.Equal(TimeSpan.FromMinutes(1), normalized.Rebound.SecondBackoff);
        Assert.Equal(20, normalized.StableStateSuppression.MinimumSamples);
        Assert.Equal(TimeSpan.FromDays(90), normalized.StableStateSuppression.MaximumRecordAge);
        Assert.Equal(1.5d, normalized.StableStateSuppression.RelativeGrowthMargin);
        Assert.Equal(1024L * 1024 * 1024, normalized.StableStateSuppression.AbsoluteGrowthMarginBytes);
        Assert.Equal(TimeSpan.FromMinutes(3), normalized.StableStateSuppression.MaximumStableValidationDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), normalized.StableStateSuppression.NaturalStableSampleInterval);
        Assert.Equal(1, normalized.StableStateSuppression.MaximumStableSamplesPerLaunch);
        Assert.Equal(20, normalized.StableStateSuppression.MaximumStableSamplePool);
    }

    [Fact]
    public void TrimHistoryRetentionCoversTheMaximumCustomProcessCooldown()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
        profile.Settings = profile.Settings with { ProcessCooldown = TimeSpan.MaxValue };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(ProcessTrimHistoryPolicy.RetentionWindow, normalized.Settings.ProcessCooldown);
    }

    [Fact]
    public void StableStateSuppressionModeSurvivesCustomProfileNormalization()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Voice", 0);
        profile.StableStateSuppressionMode = StableStateSuppressionMode.Balanced;

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(StableStateSuppressionMode.Balanced, normalized.StableStateSuppressionMode);
        profile.StableStateSuppressionMode = (StableStateSuppressionMode)999;
        Assert.Equal(
            StableStateSuppressionMode.FollowBaseProfile,
            CustomProfilePolicy.Normalize(profile).StableStateSuppressionMode);
    }

    [Fact]
    public void MissingCustomSuppressionParametersUseTheBaseProfileDefaults()
    {
        var profile = new CustomOptimizationProfile
        {
            Name = "Legacy Lite",
            BaseProfile = OptimizationProfile.Lite,
            StableStateSuppression = null!
        };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(3, normalized.StableStateSuppression.MinimumSamples);
        Assert.Equal(0.50d, normalized.StableStateSuppression.RelativeGrowthMargin);
        Assert.Equal(128L * 1024 * 1024, normalized.StableStateSuppression.AbsoluteGrowthMarginBytes);
    }

    [Fact]
    public void NamesAreUniqueIgnoringWhitespaceCaseAndBuiltInNames()
    {
        var profiles = new[] { CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Voice", 0) };

        Assert.False(CustomProfilePolicy.IsUniqueName(profiles, " voice "));
        Assert.False(CustomProfilePolicy.IsUniqueName(profiles, "Lite"));
        Assert.True(CustomProfilePolicy.IsUniqueName(profiles, "Streaming"));
        Assert.True(CustomProfilePolicy.IsUniqueName(profiles, "VOICE", profiles[0].Id));
    }

    [Fact]
    public void NormalizeFallsBackOnlyTheInvalidBaseProfile()
    {
        var profile = new CustomOptimizationProfile
        {
            Name = "Recovered",
            BaseProfile = (OptimizationProfile)999,
            Settings = null!,
            Rebound = null!
        };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.Equal(OptimizationProfile.Turbo, normalized.BaseProfile);
        Assert.Equal("Recovered", normalized.Name);
        Assert.Equal(
            OptimizationSettings.For(OptimizationProfile.Turbo).MinimumFamilyWorkingSetBytes,
            normalized.Settings.MinimumFamilyWorkingSetBytes);
        Assert.Equal(
            ReboundBackoffSettings.For(OptimizationProfile.Turbo),
            normalized.Rebound);
    }

    [Fact]
    public void UltimateDerivedProfilesKeepTheirProfileLevelRiskOverrides()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Ultimate, "Maximum", 0);
        profile.Settings = profile.Settings with
        {
            IgnoreMemoryPressureThreshold = true,
            AllowForegroundProcessTrim = true
        };

        var normalized = CustomProfilePolicy.Normalize(profile);

        Assert.True(normalized.Settings.IgnoreMemoryPressureThreshold);
        Assert.True(normalized.Settings.AllowForegroundProcessTrim);
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite, 2, 15, 1, 8)]
    [InlineData(OptimizationProfile.Turbo, 4, 25, 2, 16)]
    [InlineData(OptimizationProfile.Ultimate, 8, 50, 4, 32)]
    public void ActivityThresholdsUseProfileSpecificRanges(
        OptimizationProfile profile,
        double minimumCpu,
        double maximumCpu,
        double minimumIoMiB,
        double maximumIoMiB)
    {
        var custom = CustomProfilePolicy.Create(profile, "Custom", 0);
        custom.Settings = custom.Settings with
        {
            ActiveCpuThresholdPercent = 100,
            ActiveIoThresholdBytesPerSecond = 100d * 1024 * 1024
        };

        var maximum = CustomProfilePolicy.Normalize(custom);
        Assert.Equal(maximumCpu, maximum.Settings.ActiveCpuThresholdPercent);
        Assert.Equal(maximumIoMiB * 1024 * 1024, maximum.Settings.ActiveIoThresholdBytesPerSecond);

        custom.Settings = custom.Settings with
        {
            ActiveCpuThresholdPercent = 0,
            ActiveIoThresholdBytesPerSecond = 0
        };
        var minimum = CustomProfilePolicy.Normalize(custom);
        Assert.Equal(minimumCpu, minimum.Settings.ActiveCpuThresholdPercent);
        Assert.Equal(minimumIoMiB * 1024 * 1024, minimum.Settings.ActiveIoThresholdBytesPerSecond);
    }
}

public sealed class ApplicationReboundBackoffTrackerTests
{
    [Fact]
    public void EarlyRapidReboundStartsFirstBackoff()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, now);

        tracker.Observe(new[] { Family("app", 350) }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(1, tracker.ReboundCount("app"));
        Assert.True(tracker.IsBlocked("app", now + TimeSpan.FromMinutes(4)));
        Assert.False(tracker.IsBlocked("app", now + TimeSpan.FromMinutes(6)));
        var status = tracker.GetBackoffStatus("app", now + TimeSpan.FromMinutes(1));
        Assert.NotNull(status);
        Assert.False(status!.LongTermObservation);
        Assert.Equal(1, status.ReboundCount);
        Assert.Equal(now + TimeSpan.FromSeconds(30) + ReboundBackoffSettings.Default.FirstBackoff, status.BlockedUntil);
        Assert.Null(tracker.GetBackoffStatus("app", now + TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void ReboundCrossingThresholdWithinEarlyWindowStartsBackoffImmediately()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Turbo);
        tracker.Begin("edge", 500, 100, settings, now, targetProcessIds: new[] { 1 });

        tracker.Observe(new[] { Family("edge", 350) }, now + TimeSpan.FromSeconds(12));

        Assert.Equal(1, tracker.ReboundCount("edge"));
        Assert.Contains("edge", tracker.BlockedFamilyKeys(now + TimeSpan.FromSeconds(12)));
        var status = tracker.GetBackoffStatus("edge", now + TimeSpan.FromSeconds(12));
        Assert.NotNull(status);
        Assert.Equal(now + TimeSpan.FromSeconds(12) + settings.FirstBackoff, status!.BlockedUntil);
    }

    [Fact]
    public void EarlyBackoffContinuesToLateWindowForRetainedBenefit()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Turbo);
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.0.0")
        {
            RunId = "run-early-backoff"
        };
        tracker.Begin("edge", 500, 100, settings, now, runContext: context);

        tracker.Observe(new[] { Family("edge", 350) }, now + TimeSpan.FromSeconds(12));

        Assert.Equal(1, tracker.ReboundCount("edge"));
        Assert.Empty(tracker.DrainCompletedOutcomes());

        tracker.Observe(new[] { Family("edge", 300) }, now + settings.LateWindow);

        var outcome = Assert.Single(tracker.DrainCompletedOutcomes());
        Assert.Equal(context, outcome.RunContext);
        Assert.Equal("run-early-backoff", outcome.RunContext?.RunId);
        Assert.Equal(400, outcome.ReleasedBytes);
        Assert.Equal(200, outcome.RegainedBytes);
        Assert.Equal(200, outcome.RetainedBytes);
        Assert.Equal(300, outcome.LateWorkingSetBytes);
        Assert.Equal(50, outcome.ReboundPercent, precision: 3);
        Assert.True(outcome.BackoffTriggered);
        Assert.Equal(1, tracker.ReboundCount("edge"));
    }

    [Fact]
    public void OverlappingBeginDoesNotReplacePendingObservation()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.Parse("2026-07-27T12:00:00Z");
        tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, now);

        tracker.Begin(
            "app",
            500,
            300,
            ReboundBackoffSettings.Default,
            now + TimeSpan.FromSeconds(90));

        Assert.Contains("app", tracker.PendingObservationFamilyKeys(now + TimeSpan.FromSeconds(90)));
        tracker.Observe(new[] { Family("app", 300) }, now + TimeSpan.FromSeconds(120));
        var outcome = Assert.Single(tracker.DrainCompletedOutcomes());

        Assert.Equal(now, outcome.StartedAt);
        Assert.Equal(400, outcome.ReleasedBytes);
        Assert.DoesNotContain("app", tracker.PendingObservationFamilyKeys(now + TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void ReplacementProcessCountsTowardReboundWithoutIncludingExistingSibling()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Turbo);
        tracker.Begin(
            "edge",
            workingSetBefore: 500,
            workingSetAfter: 100,
            settings: settings,
            now: now,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1, 2 });
        var family = new ProcessFamilySnapshot(
            "edge",
            "edge",
            null,
            new[]
            {
                new ProcessSnapshot(2, "existing", null, null, 1_000, 0, 0, false, false, true, 90),
                new ProcessSnapshot(3, "replacement", null, null, 350, 0, 0, false, false, true, 90)
            });

        tracker.Observe(new[] { family }, now + TimeSpan.FromSeconds(12));

        Assert.Equal(1, tracker.ReboundCount("edge"));
    }

    [Fact]
    public void ThirdRapidReboundEntersLongTermObservation()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);

        Assert.Equal(3, tracker.ReboundCount("app"));
        Assert.True(tracker.IsBlocked("app", enteredAt + TimeSpan.FromDays(30)));
        var status = tracker.GetBackoffStatus("app", enteredAt + TimeSpan.FromMinutes(1));
        Assert.NotNull(status);
        Assert.True(status!.LongTermObservation);
        Assert.Null(status.BlockedUntil);
    }

    [Fact]
    public void ForegroundThenSustainedIdlePermitsOneLongTermRetry()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500, foreground: true) },
            severeMemoryPressure: false,
            minimumFamilyWorkingSetBytes: 100,
            growthSettings: StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now: enteredAt + TimeSpan.FromMinutes(1));
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            severeMemoryPressure: false,
            minimumFamilyWorkingSetBytes: 100,
            growthSettings: StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now: enteredAt + TimeSpan.FromMinutes(1));
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            severeMemoryPressure: false,
            minimumFamilyWorkingSetBytes: 100,
            growthSettings: StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now: enteredAt + TimeSpan.FromMinutes(2));

        Assert.False(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void SeverePressureRequiresLargeSustainedIdleFamilyForLongTermRetry()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 99) },
            severeMemoryPressure: true,
            minimumFamilyWorkingSetBytes: 100,
            growthSettings: StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now: enteredAt + TimeSpan.FromMinutes(1));
        Assert.True(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(1)));

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            severeMemoryPressure: true,
            minimumFamilyWorkingSetBytes: 100,
            growthSettings: StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now: enteredAt + TimeSpan.FromMinutes(2));
        Assert.False(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void LargeComponentGrowthEndsLongTermBackoffWithoutWaitingForForegroundPhase()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);
        var growth = new StableStateSuppressionSettings(
            3, TimeSpan.FromDays(30), 0.35, 96);

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 670) }, false, 100, growth,
            enteredAt + TimeSpan.FromMinutes(1));
        Assert.True(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(1)));

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 700) }, false, 100, growth,
            enteredAt + TimeSpan.FromMinutes(2));
        Assert.False(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void LongTermGrowthBaselineSurvivesProgressRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var enteredAt = EnterLongTermObservation(tracker, now);
        var saved = tracker.CaptureProgress(enteredAt + TimeSpan.FromMinutes(1));
        var progress = Assert.Single(saved);
        Assert.Equal(500, progress.LongTermBaselineWorkingSetBytes);

        var restored = new ApplicationReboundBackoffTracker();
        restored.RestoreProgress(saved, enteredAt + TimeSpan.FromMinutes(2));
        var growth = new StableStateSuppressionSettings(
            3, TimeSpan.FromDays(30), 0.35, 96);
        restored.UpdateLongTermRetryPermissions(
            new[] { Family("app", 700) }, false, 100, growth,
            enteredAt + TimeSpan.FromMinutes(3));

        Assert.False(restored.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void LongTermRetryIdleCheckUsesBlockedComponentInsteadOfActiveSibling()
    {
        const string mainPath = @"C:\Apps\main.exe";
        const string helperPath = @"C:\Apps\helper.exe";
        const string familyKey = "directory:c:\\apps";
        var now = DateTimeOffset.UtcNow;
        var mainKey = ApplicationComponentIdentity.ForExecutable(familyKey, mainPath);
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(familyKey, 3, 0, 10, true, false)
            {
                TargetKey = mainKey,
                LongTermBaselineWorkingSetBytes = 200
            }
        }, now);
        var family = new ProcessFamilySnapshot(familyKey, "app", @"C:\Apps", new[]
        {
            new ProcessSnapshot(1, "main", mainPath, null, 200, 0, 0,
                false, false, true, 90),
            new ProcessSnapshot(2, "helper", helperPath, null, 200, 50, 1024 * 1024,
                false, false, true, 0)
        });
        var growth = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);

        tracker.UpdateLongTermRetryPermissions(new[] { family }, false, 100, growth, now);
        tracker.UpdateLongTermRetryPermissions(
            new[] { family }, false, 100, growth,
            now + BackgroundActivityTracker.MinimumObservation);

        Assert.False(tracker.IsBlocked(familyKey, now + BackgroundActivityTracker.MinimumObservation));
    }

    [Fact]
    public void OneHourAndSustainedIdlePermitsLongTermRetry()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) }, false, 100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            enteredAt + TimeSpan.FromMinutes(59));
        Assert.True(tracker.IsBlocked("app", enteredAt + TimeSpan.FromMinutes(59)));

        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) }, false, 100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            enteredAt + TimeSpan.FromHours(1));
        Assert.False(tracker.IsBlocked("app", enteredAt + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void StableLongTermRetryDowngradesReboundCount()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);
        var retryAt = enteredAt + TimeSpan.FromHours(1);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            false,
            100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            retryAt - TimeSpan.FromMinutes(1));
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            false,
            100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            retryAt);

        tracker.Begin(
            "app", 500, 100, ReboundBackoffSettings.Default, retryAt,
            runContext: new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo,
                OptimizationTriggerKind.Automatic, "1.0.0"));
        tracker.Observe(new[] { Family("app", 200) }, retryAt + TimeSpan.FromSeconds(120));

        Assert.Equal(2, tracker.ReboundCount("app"));
        Assert.False(tracker.IsBlocked("app", retryAt + TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void RapidReboundDuringLongTermRetryReturnsToLongTermObservation()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = EnterLongTermObservation(tracker, now);
        var retryAt = enteredAt + TimeSpan.FromHours(1);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            false,
            100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            retryAt - TimeSpan.FromMinutes(1));
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) },
            false,
            100,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            retryAt);

        tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, retryAt);
        tracker.Observe(new[] { Family("app", 500) }, retryAt + TimeSpan.FromSeconds(12));

        Assert.Equal(4, tracker.ReboundCount("app"));
        Assert.True(tracker.GetBackoffStatus("app", retryAt + TimeSpan.FromSeconds(12))!.LongTermObservation);
    }

    [Fact]
    public void SlowLowReboundDoesNotStartBackoff()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, now);

        tracker.Observe(new[] { Family("app", 250) }, now + TimeSpan.FromSeconds(30));
        tracker.Observe(new[] { Family("app", 300) }, now + TimeSpan.FromSeconds(120));

        Assert.Equal(0, tracker.ReboundCount("app"));
        Assert.False(tracker.IsBlocked("app", now + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SmallAbsoluteHighPercentageReboundStillStartsBackoff()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var mebibyte = 1024L * 1024;
        tracker.Begin(
            "app",
            100 * mebibyte,
            80 * mebibyte,
            ReboundBackoffSettings.Default,
            now,
            learnOutcome: true);

        tracker.Observe(new[] { Family("app", 100 * mebibyte) }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(1, tracker.ReboundCount("app"));
        Assert.Empty(tracker.LearningRecords);

        tracker.Observe(new[] { Family("app", 100 * mebibyte) }, now + ReboundBackoffSettings.Default.LateWindow);

        Assert.Single(tracker.LearningRecords);
    }

    [Fact]
    public void LegacyLearningCleanupKeepsRecordsWithValidObservationSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("legacy", 0.2, 8, 3, now)
            {
                LegacySampleCount = 8,
                ValidSampleCount = 0
            },
            new ApplicationBenefitLearningRecord("learned", 0.8, 3, 0, now)
            {
                ComponentKey = "learned|component:main",
                ValidSampleCount = 3,
                AverageRetainedBytes = 800
            }
        }, now);

        var removed = tracker.RemoveLegacyOnlyLearning();

        Assert.Equal(1, removed);
        var learned = Assert.Single(tracker.LearningRecords);
        Assert.Equal("learned", learned.FamilyKey);
        Assert.Equal(3, learned.ValidSampleCount);
        Assert.Equal(0, learned.LegacySampleCount);
    }

    [Fact]
    public void LearningCleanupForOneFamilyRemovesEveryComponentAndKeepsOtherFamilies()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("editor", 0.8, 3, 0, now)
            {
                ComponentKey = "editor|component:main",
                ValidSampleCount = 3
            },
            new ApplicationBenefitLearningRecord("EDITOR", 0.7, 2, 0, now)
            {
                ComponentKey = "editor|component:helper",
                ValidSampleCount = 2
            },
            new ApplicationBenefitLearningRecord("browser", 0.6, 4, 0, now)
            {
                ComponentKey = "browser|component:main",
                ValidSampleCount = 4
            }
        }, now);

        var removed = tracker.RemoveLearningForFamily("Editor", now);

        Assert.Equal(2, removed);
        var remaining = Assert.Single(tracker.LearningRecords);
        Assert.Equal("browser", remaining.FamilyKey);
        Assert.DoesNotContain("editor", tracker.OutcomeMultipliers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("browser", tracker.OutcomeMultipliers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LearningCleanupCancelsAnActiveLateGuardAndPreventsDelayedCommit()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "editor|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("editor", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "editor", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "editor", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = startedAt + settings.MaximumStableValidationDuration,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };

        tracker.Observe(new[] { Family("editor", 700 * mib) }, startedAt + TimeSpan.FromSeconds(30));
        tracker.Observe(new[] { Family("editor", 700 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        foreach (var seconds in new[] { 120, 150, 180 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(700 * mib) },
                startedAt + TimeSpan.FromSeconds(seconds),
                settings);
        }
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);

        Assert.Equal(1, tracker.RemoveLearningForFamily("editor", startedAt + TimeSpan.FromMinutes(3)));
        Assert.Empty(tracker.LearningRecords);
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.NaturalStableObservationStatuses());
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib) },
            startedAt + settings.MaximumStableValidationDuration,
            settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Empty(tracker.StableCandidateStatuses);
    }

    [Fact]
    public void ResetStableAnchorLearningKeepsBenefitLearningAndOtherScopes()
    {
        const long mib = 1024L * 1024;
        var now = DateTimeOffset.UtcNow;
        var componentKey = "editor|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("editor", new[] { componentKey });
        var otherComponentKey = "browser|component:main";
        var otherScopeKey = ApplicationStableScopeIdentity.For("browser", new[] { otherComponentKey });
        ApplicationStableLearningRecord StableRecord(string familyKey, string component, long bytes) =>
            new(familyKey, new[] { bytes }, now, "launch-1")
            {
                ComponentKeys = new[] { component },
                ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
                StableSamples = new[]
                {
                    new ApplicationStableSample(bytes, now, "launch-1", "cycle-1", 1, false)
                },
                AnchorGeneration = 1,
                AnchorGenerationBaselineBytes = bytes
            };
        var tracker = new ApplicationReboundBackoffTracker(
            new[]
            {
                new ApplicationBenefitLearningRecord("editor", 0.8, 3, 0, now)
                {
                    ComponentKey = componentKey,
                    ValidSampleCount = 3
                }
            },
            now,
            new[]
            {
                StableRecord("editor", componentKey, 500 * mib),
                StableRecord("browser", otherComponentKey, 700 * mib)
            });

        Assert.True(tracker.ResetStableAnchorLearning(scopeKey));

        Assert.Single(tracker.LearningRecords);
        var remaining = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(otherScopeKey, ApplicationStableScopeIdentity.For(remaining));
        Assert.False(tracker.ResetStableAnchorLearning(scopeKey));
    }

    [Fact]
    public void HighPercentageReboundOnSecondTrimStartsBackoffAfterLowFirstRebound()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Turbo);

        tracker.Begin("edge", 1_000, 300, settings, now);
        tracker.Observe(new[] { Family("edge", 400) }, now + settings.EarlyWindow);
        tracker.Observe(new[] { Family("edge", 400) }, now + settings.LateWindow);
        Assert.Equal(0, tracker.ReboundCount("edge"));

        var secondTrim = now + TimeSpan.FromMinutes(3);
        tracker.Begin("edge", 453, 400, settings, secondTrim);
        tracker.Observe(new[] { Family("edge", 453) }, secondTrim + settings.EarlyWindow);

        Assert.Equal(1, tracker.ReboundCount("edge"));
        Assert.True(tracker.IsBlocked("edge", secondTrim + settings.EarlyWindow));
    }

    [Fact]
    public void BenefitLearningAppliesOnlyALightWeightToTwoSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("app", 0.2, 2, 0, now)
            {
                ValidSampleCount = 2
            }
        }, now);

        Assert.Equal(0.84, tracker.OutcomeMultipliers["app"], precision: 3);
        Assert.Equal(0.2, tracker.LearningConfidences["app"], precision: 3);
    }

    [Fact]
    public void RepeatedAutomaticOutcomesAreAggregatedInsteadOfOverwritten()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        for (var pass = 0; pass < 3; pass++)
        {
            var started = now + TimeSpan.FromHours(pass);
            tracker.Begin(
                "app",
                500,
                100,
                ReboundBackoffSettings.Default,
                started,
                learnOutcome: true);
            tracker.Observe(new[] { Family("app", 200) }, started + TimeSpan.FromSeconds(120));
        }

        var record = Assert.Single(tracker.LearningRecords);
        Assert.Equal(3, record.SampleCount);
        Assert.Equal(0, record.QuickReturnCount);
        Assert.Equal(0.6, record.AverageOutcomeMultiplier, precision: 3);
        Assert.InRange(tracker.OutcomeMultipliers["app"], 0.83, 0.85);
        Assert.Equal(0.4, tracker.LearningConfidences["app"], precision: 3);
    }

    [Fact]
    public void MultipleOutcomesFromOneLaunchCountAsOneEquallyWeightedSample()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        const string componentKey = "app|component:main";

        tracker.BeginComponent(
            "app", componentKey, null, 500, 100, ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        tracker.Observe(new[] { Family("app", 200) }, now + ReboundBackoffSettings.Default.LateWindow);

        tracker.BeginComponent(
            "app", componentKey, null, 500, 100, ReboundBackoffSettings.Default,
            now + TimeSpan.FromHours(1), learnOutcome: true, targetProcessIds: new[] { 1 },
            launchSignature: "launch-1");
        tracker.Observe(new[] { Family("app", 300) }, now + TimeSpan.FromHours(1) + ReboundBackoffSettings.Default.LateWindow);

        var sameLaunch = Assert.Single(tracker.LearningRecords);
        Assert.Equal(1, sameLaunch.SampleCount);
        Assert.Equal(1, sameLaunch.ValidSampleCount);
        Assert.Equal(1, sameLaunch.DistinctLaunchCount);
        Assert.Equal(2, sameLaunch.LastLaunchObservationCount);
        Assert.Equal(250, Assert.Single(sameLaunch.LateWorkingSetSamplesBytes));
        Assert.Equal(1d, sameLaunch.LastLaunchContributionWeight);
        Assert.Equal(0.5, sameLaunch.AverageOutcomeMultiplier, precision: 3);

        tracker.BeginComponent(
            "app", componentKey, null, 500, 100, ReboundBackoffSettings.Default,
            now + TimeSpan.FromHours(2), learnOutcome: true, targetProcessIds: new[] { 1 },
            launchSignature: "launch-2");
        tracker.Observe(new[] { Family("app", 200) }, now + TimeSpan.FromHours(2) + ReboundBackoffSettings.Default.LateWindow);

        var acrossLaunches = Assert.Single(tracker.LearningRecords);
        Assert.Equal(2, acrossLaunches.SampleCount);
        Assert.Equal(2, acrossLaunches.ValidSampleCount);
        Assert.Equal(2, acrossLaunches.DistinctLaunchCount);
        Assert.Equal(0.55, acrossLaunches.AverageOutcomeMultiplier, precision: 3);
    }

    [Fact]
    public void ReboundObservationsDoNotProduceNaturalStableSamples()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        RecordStableObservation(tracker, now, 0, componentKey, "launch-1", 600 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Empty(Assert.Single(tracker.LearningRecords).StableWorkingSetSamplesBytes);
        Assert.Empty(tracker.StableCandidateStatuses);

        RecordStableObservation(tracker, now, 1, componentKey, "launch-1", 380 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Empty(Assert.Single(tracker.LearningRecords).StableWorkingSetSamplesBytes);
        Assert.Empty(tracker.StableCandidateStatuses);

        RecordStableObservation(tracker, now, 2, componentKey, "launch-1", 350 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        var converged = Assert.Single(tracker.LearningRecords);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Equal(443L * mib + mib / 3, converged.AverageLateWorkingSetBytes);

        RecordStableObservation(tracker, now, 3, componentKey, "launch-1", 340 * mib);
        Assert.Empty(tracker.FamilyStableLearningRecords);

        RecordStableObservation(tracker, now, 4, componentKey, "launch-2", 400 * mib);
        RecordStableObservation(tracker, now, 5, componentKey, "launch-2", 420 * mib);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void StableLearningExcludesQuickForegroundReturnAndLateBackoff()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var quickReturn = new ApplicationReboundBackoffTracker();

        quickReturn.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib, ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "quick");
        quickReturn.Observe(new[] { Family("app", 200 * mib, foreground: true) }, now + TimeSpan.FromSeconds(3));
        quickReturn.Observe(new[] { Family("app", 200 * mib) }, now + ReboundBackoffSettings.Default.LateWindow);

        Assert.Empty(quickReturn.DrainCompletedStableObservations());

        Assert.Empty(Assert.Single(quickReturn.LearningRecords).StableWorkingSetSamplesBytes);
        Assert.Empty(quickReturn.StableCandidateStatuses);

        var lateBackoff = new ApplicationReboundBackoffTracker();
        lateBackoff.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib, ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "rebound");
        lateBackoff.Observe(new[] { Family("app", 900 * mib) }, now + ReboundBackoffSettings.Default.LateWindow);

        Assert.Empty(lateBackoff.DrainCompletedStableObservations());

        Assert.Empty(Assert.Single(lateBackoff.LearningRecords).StableWorkingSetSamplesBytes);
        Assert.Empty(lateBackoff.StableCandidateStatuses);
    }

    [Fact]
    public void StableLearningUsesAggregateReboundWithoutSmallComponentVeto()
    {
        const long mib = 1024L * 1024;
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        void Observe(int hour, long mainBytes, long helperBytes)
        {
            var started = now + TimeSpan.FromHours(hour);
            var run = new OptimizationRunContext("builtin:Turbo", OptimizationProfile.Turbo,
                OptimizationTriggerKind.Automatic, "test") { RunId = $"aggregate-{hour}" };
            tracker.BeginComponent("app", "app|component:main", null, 900 * mib, 100 * mib,
                ReboundBackoffSettings.Default, started, true, targetProcessIds: new[] { 1 },
                runContext: run, launchSignature: "main-launch");
            tracker.BeginComponent("app", "app|component:helper", null, 110 * mib, 100 * mib,
                ReboundBackoffSettings.Default, started, true, targetProcessIds: new[] { 2 },
                runContext: run, launchSignature: "helper-launch");
            tracker.Observe(new[]
            {
                new ProcessFamilySnapshot("app", "app", null, new[]
                {
                    new ProcessSnapshot(1, "main", null, null, mainBytes, 0, 0, false, false, true, 90),
                    new ProcessSnapshot(2, "helper", null, null, helperBytes, 0, 0, false, false, true, 90)
                })
            }, started + ReboundBackoffSettings.Default.LateWindow);
        }

        Observe(1, 180 * mib, 110 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Contains(tracker.DrainCompletedOutcomes(), outcome =>
            outcome.ComponentKey == "app|component:helper" && outcome.BackoffTriggered);

        Observe(2, 190 * mib, 110 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void ReboundSamplesAcrossLaunchesDoNotBecomeNaturalStableSamples()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        RecordStableObservation(tracker, now, 0, componentKey, "launch-1", 69 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());

        RecordStableObservation(tracker, now, 1, componentKey, "launch-2", 71 * mib);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Empty(tracker.FamilyStableLearningRecords);

        RecordStableObservation(tracker, now, 2, componentKey, "launch-3", 72 * mib);
        RecordStableObservation(tracker, now, 3, componentKey, "launch-4", 73 * mib);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void ReboundWorkingSetDoesNotBecomeCompleteApplicationStableReference()
    {
        const long mib = 1024L * 1024;
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var run = new OptimizationRunContext("builtin:Turbo", OptimizationProfile.Turbo,
            OptimizationTriggerKind.Automatic, "test") { RunId = "run-1" };

        void Observe(int hour, long mainBytes, long helperBytes)
        {
            var started = now + TimeSpan.FromHours(hour);
            tracker.BeginComponent("app", "app|component:main", null, 900 * mib, 100 * mib,
                ReboundBackoffSettings.Default, started, true, targetProcessIds: new[] { 1 },
                runContext: run with
                {
                    RunId = $"run-{hour}",
                    ProfileKey = hour == 1 ? "builtin:Lite" : "builtin:Turbo",
                    BaseProfile = hour == 1 ? OptimizationProfile.Lite : OptimizationProfile.Turbo
                }, launchSignature: "main-launch");
            tracker.BeginComponent("app", "app|component:helper", null, 500 * mib, 100 * mib,
                ReboundBackoffSettings.Default, started, true, targetProcessIds: new[] { 2 },
                runContext: run with
                {
                    RunId = $"run-{hour}",
                    ProfileKey = hour == 1 ? "builtin:Lite" : "builtin:Turbo",
                    BaseProfile = hour == 1 ? OptimizationProfile.Lite : OptimizationProfile.Turbo
                }, launchSignature: "helper-launch");
            tracker.Observe(new[]
            {
                new ProcessFamilySnapshot("app", "app", null, new[]
                {
                    new ProcessSnapshot(1, "main", null, null, mainBytes, 0, 0, false, false, true, 90),
                    new ProcessSnapshot(2, "helper", null, null, helperBytes, 0, 0, false, false, true, 90)
                })
            }, started + ReboundBackoffSettings.Default.LateWindow);
        }

        Observe(1, 200 * mib, 300 * mib);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Observe(2, 210 * mib, 310 * mib);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.All(tracker.LearningRecords, record => Assert.Empty(record.StableWorkingSetSamplesBytes));
    }

    [Fact]
    public void LegacyStableLearningIsIgnoredAfterNaturalBaselineMigration()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var saved = new ApplicationBenefitLearningRecord("app", 0.5, 1, 0, now)
        {
            ComponentKey = componentKey,
            ValidSampleCount = 1,
            DistinctLaunchCount = 1,
            LastLaunchSignature = "launch-1",
            LastLaunchObservationCount = 1,
            LastLaunchContributionWeight = 1d,
        };
        var stableSignature = $"{componentKey}::launch-1";
        var savedStable = new ApplicationStableLearningRecord(
            "app", new[] { 365 * mib }, now, stableSignature);
        var tracker = new ApplicationReboundBackoffTracker(new[] { saved }, now, new[] { savedStable });

        RecordStableObservation(tracker, now, 1, componentKey, "launch-1", 360 * mib);
        RecordStableObservation(tracker, now, 2, componentKey, "launch-1", 340 * mib);

        Assert.Empty(tracker.FamilyStableLearningRecords);

        tracker.ClearLearning();
        Assert.Empty(tracker.LearningRecords);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Empty(tracker.StableCandidateStatuses);
    }

    [Fact]
    public void NaturalStableLearningWaitsForPostTrimRecoveryToSettle()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var stableSettings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);

        NaturalStableStateSnapshot Snapshot(long bytes, string launch = "launch-1") => new(
            "app", scopeKey, new[] { componentKey }, launch, bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(70 * mib) }, now, stableSettings);
        Assert.Equal(ApplicationStableObservationDecision.FirstObservation,
            Assert.Single(tracker.DrainCompletedStableObservations()).Decision);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(1), stableSettings);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(198 * mib) }, now + TimeSpan.FromMinutes(4), stableSettings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Equal(StableObservationPhase.ProvisionalValidation,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Phase);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(198 * mib) }, now + TimeSpan.FromMinutes(6), stableSettings);

        var stable = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(198 * mib, Assert.Single(stable.StableWorkingSetSamplesBytes));
        Assert.Equal(StableStateSuppressionPolicy.NaturalStableStateModelVersion, stable.ModelVersion);
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Empty(tracker.NaturalStableObservationComponentKeys());

        Assert.Equal(now + TimeSpan.FromMinutes(6), stable.StableLastObservedAt);
    }

    [Fact]
    public void GlobalReclaimObservationDoesNotWriteStableSamplesOrRegisterBackoff()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var stableSettings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1",
            recoveryOrigin: NaturalStableObservationOrigin.GlobalReclaim);
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", 100 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = now,
            RecoveryOrigin = NaturalStableObservationOrigin.GlobalReclaim
        };

        tracker.Observe(new[] { Family("app", 100 * mib) }, now + ReboundBackoffSettings.Default.LateWindow);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot() }, now + ReboundBackoffSettings.Default.LateWindow, stableSettings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Empty(tracker.NaturalStableScopeRequests(
            now + ReboundBackoffSettings.Default.LateWindow));
        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.False(tracker.IsBlocked("app", now + TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void GlobalReclaimObservationWithAnchorCanConvergeWithoutWritingStableSamplesOrStartingAReview()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var anchor = new ApplicationStableLearningRecord("app", new[] { 100 * mib }, now.AddHours(-1), "launch-0")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[]
            {
                new ApplicationStableSample(100 * mib, now.AddHours(-1), "launch-0", "history", 1, PendingHigh: false)
            },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 100 * mib,
            LastStableLaunchSampleCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            familyStableLearningRecords: new[] { anchor });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1",
            recoveryOrigin: NaturalStableObservationOrigin.GlobalReclaim);
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", 100 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = now,
            RecoveryOrigin = NaturalStableObservationOrigin.GlobalReclaim
        };

        var observationEndsAt = now + ReboundBackoffSettings.Default.LateWindow;
        tracker.Observe(new[] { Family("app", 100 * mib) }, observationEndsAt);
        Assert.Contains(tracker.NaturalStableScopeRequests(observationEndsAt), request =>
            request.Origin == NaturalStableObservationOrigin.GlobalReclaim);

        foreach (var minutes in new[] { 0d, 2d, 3d, 5d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, observationEndsAt + TimeSpan.FromMinutes(minutes), settings);

        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Equal(0, Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule(
                "app", new[] { componentKey }, settings, "launch-1")).CompletedReviewCount);
    }

    [Fact]
    public void GlobalReclaimExpandedScopeKeepsItsIsolationWhenTheScopeLaunchSignatureChanges()
    {
        const long mib = 1024L * 1024;
        const string main = "app|component:main";
        const string helper = "app|component:helper";
        var now = DateTimeOffset.UtcNow;
        var anchor = new ApplicationStableLearningRecord("app", new[] { 100 * mib }, now.AddHours(-1), "old")
        {
            ComponentKeys = new[] { main, helper },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[]
            {
                new ApplicationStableSample(100 * mib, now.AddHours(-1), "old", "history", 1, PendingHigh: false)
            },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 100 * mib
        };
        var tracker = new ApplicationReboundBackoffTracker(
            familyStableLearningRecords: new[] { anchor });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", main, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "main-launch",
            recoveryOrigin: NaturalStableObservationOrigin.GlobalReclaim);
        tracker.BeginComponent(
            "app", helper, null, 1000 * mib, 20 * mib,
            ReboundBackoffSettings.Default, now,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "helper-launch",
            recoveryOrigin: NaturalStableObservationOrigin.GlobalReclaim);
        var snapshot = new NaturalStableStateSnapshot(
            "app", ApplicationStableScopeIdentity.For("app", new[] { main, helper }),
            new[] { main, helper }, "app-scope-launch", 120 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = now,
            RecoveryOrigin = NaturalStableObservationOrigin.GlobalReclaim,
            FamilyScopeKey = ApplicationStableScopeIdentity.For("app", new[] { main, helper }),
            FamilyScopeComponentKeys = new[] { main, helper },
            FamilyScopeLaunchSignature = "app-scope-launch",
            FamilyScopeWorkingSetBytes = 120 * mib
        };

        var observationEndsAt = now + ReboundBackoffSettings.Default.LateWindow;
        tracker.Observe(new[] { Family("app", 120 * mib) }, observationEndsAt);
        foreach (var minutes in new[] { 0d, 2d, 3d, 5d })
            tracker.ObserveNaturalStableStates(new[] { snapshot }, observationEndsAt + TimeSpan.FromMinutes(minutes), settings);

        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Equal(0, Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule("app", new[] { main, helper }, settings, "app-scope-launch"))
            .CompletedReviewCount);
    }

    [Fact]
    public void PostTrimRecoveryCannotConvergeBeforeThreeMinutes()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var trustedSample = new ApplicationStableSample(
            100 * mib, startedAt.AddHours(-1), "launch-0", "cycle-0", 1, PendingHigh: false);
        var trustedRecord = new ApplicationStableLearningRecord(
            "app", new[] { 100 * mib }, trustedSample.ObservedAt, "launch-0")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[] { trustedSample },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 100 * mib,
            LastStableLaunchSampleCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            learningRecords: null,
            now: startedAt,
            familyStableLearningRecords: new[] { trustedRecord });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 50 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = startedAt + settings.MaximumStableValidationDuration,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };

        foreach (var seconds in new[] { 0, 30, 60, 90 })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(200 * mib) }, startedAt + TimeSpan.FromSeconds(seconds), settings);
        tracker.DrainCompletedStableObservations();
        tracker.Observe(new[] { Family("app", 200 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, startedAt + TimeSpan.FromMinutes(2), settings);

        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, startedAt + TimeSpan.FromMinutes(3), settings);

        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Equal(ApplicationStableObservationDecision.Converged,
            Assert.Single(tracker.DrainCompletedStableObservations()).Decision);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) },
            startedAt + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59),
            settings);
        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, startedAt + TimeSpan.FromMinutes(5), settings);

        Assert.Equal(2, Assert.Single(tracker.FamilyStableLearningRecords).StableSamples.Count);
        Assert.Equal(ApplicationStableObservationDecision.HighAnchorPending,
            Assert.Single(tracker.DrainCompletedStableObservations()).Decision);
    }

    [Fact]
    public void ApplicationSteadyStateLimitUsesTheCompleteUnprotectedFamilyScope()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 400 * mib);
        var snapshot = new NaturalStableStateSnapshot(
            "app", scopeKey, new[] { componentKey }, "launch-1", 400 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            FamilyScopeKey = scopeKey,
            FamilyScopeComponentKeys = new[] { componentKey },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = 800 * mib
        };

        tracker.ObserveNaturalStableStates(new[] { snapshot }, now, settings);

        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void CrossingApplicationSteadyStateLimitCancelsRuntimeHoldButKeepsHistory()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var unlimited = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 400 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool firstBootGate = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RequiresFirstBootAnchorGate = firstBootGate
        };

        foreach (var minutes in new[] { 0d, 2d, 3d, 5d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(minutes),
                unlimited);
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Single(tracker.FamilyStableLearningRecords);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(800 * mib) }, now + TimeSpan.FromMinutes(4),
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo));

        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Single(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void ProvisionalValidationRejectsLateGrowthAndExpiresAtTheOuterDeadline()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = now,
            RecoveryDeadline = now + settings.MaximumStableValidationDuration,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };

        foreach (var minutes in new[] { 0d, 2d, 3d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(minutes), settings);
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);

        var reboundAt = now + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(13);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(400 * mib) }, reboundAt, settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableGrowthReviewComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) },
            now + settings.MaximumStableValidationDuration,
            settings);
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void ReboundBackoffStartsStableRecoveryAfterBenefitObservationCompletes()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("app", 2, 0, null, false, false)
            {
                TargetKey = componentKey
            }
        }, startedAt);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        var backoffStartedAt = startedAt + TimeSpan.FromSeconds(30);
        var recoveryStartedAt = startedAt + ReboundBackoffSettings.Default.LateWindow;
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = backoffStartedAt,
            RecoveryDeadline = backoffStartedAt +
                               ApplicationReboundBackoffTracker.LongTermBackoffStableObservationWindow,
            RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
        };

        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromSeconds(30));
        Assert.True(tracker.IsBlocked("app", startedAt + TimeSpan.FromSeconds(30)));
        var pendingRequest = Assert.Single(
            tracker.NaturalStableScopeRequests(startedAt + TimeSpan.FromSeconds(30),
                settings.MaximumStableValidationDuration));
        Assert.Equal(backoffStartedAt, pendingRequest.StartedAt);
        Assert.Equal(
            backoffStartedAt + ApplicationReboundBackoffTracker.LongTermBackoffStableObservationWindow,
            pendingRequest.Deadline);
        Assert.Equal(NaturalStableObservationOrigin.BackoffRecovery, pendingRequest.Origin);

        tracker.Observe(new[] { Family("app", 700 * mib) }, recoveryStartedAt);
        var request = Assert.Single(tracker.NaturalStableScopeRequests(recoveryStartedAt));
        Assert.Equal(backoffStartedAt, request.StartedAt);
        foreach (var seconds in new[] { 0, 30, 60, 90, 120, 150, 180 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(700 * mib) },
                recoveryStartedAt + TimeSpan.FromSeconds(seconds),
                settings);
        }

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.True(tracker.IsBlocked("app", startedAt + TimeSpan.FromMinutes(5)));

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib) },
            backoffStartedAt + TimeSpan.FromMinutes(5),
            settings);

        var record = Assert.Single(tracker.FamilyStableLearningRecords);
        var sample = Assert.Single(record.StableSamples);
        Assert.StartsWith("backoff:", sample.RecoveryCycleId, StringComparison.Ordinal);
        Assert.Equal(700 * mib, sample.WorkingSetBytes);
        Assert.False(tracker.IsBlocked(
            "app", backoffStartedAt + TimeSpan.FromMinutes(5)));
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());
        var runtime = Assert.Single(tracker.StableCandidateStatuses);
        Assert.Equal(ApplicationStableCandidateState.Converged, runtime.State);
        Assert.Equal(700 * mib, runtime.CandidateBytes);
    }

    [Fact]
    public void FailedLateReboundGrowthReviewDoesNotRestartBackoffRecoveryOrClearBackoff()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("app", 2, 0, null, false, false)
            {
                TargetKey = componentKey
            }
        }, startedAt);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt + TimeSpan.FromSeconds(30),
            RecoveryDeadline = startedAt + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30),
            RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
        };

        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromSeconds(30));
        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        foreach (var seconds in new[] { 30, 120, 150, 180, 210 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(700 * mib) },
                startedAt + TimeSpan.FromSeconds(seconds),
                settings);
        }
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());

        var reboundAt = startedAt + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(43);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(1000 * mib) }, reboundAt, settings);
        Assert.Contains(componentKey, tracker.NaturalStableGrowthReviewComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(1000 * mib) },
            startedAt + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30),
            settings);

        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.True(tracker.IsBlocked("app", startedAt + TimeSpan.FromMinutes(11)));
        Assert.Empty(tracker.NaturalStableScopeRequests(
            startedAt + TimeSpan.FromMinutes(11),
            settings.MaximumStableValidationDuration));
    }

    [Fact]
    public void PendingPostTrimComponentsShareOneFamilyScopeAndDeadline()
    {
        const long mib = 1024L * 1024;
        var startedAt = DateTimeOffset.UtcNow;
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.BeginComponent("app", "app|component:main", null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt, learnOutcome: true,
            targetProcessIds: new[] { 1 }, launchSignature: "main-launch");
        tracker.BeginComponent("app", "app|component:helper", null, 800 * mib, 80 * mib,
            ReboundBackoffSettings.Default, startedAt + TimeSpan.FromSeconds(10), learnOutcome: true,
            targetProcessIds: new[] { 2 }, launchSignature: "helper-launch");

        var request = Assert.Single(tracker.NaturalStableScopeRequests(
            startedAt + TimeSpan.FromSeconds(30), settings.MaximumStableValidationDuration));

        Assert.Equal(new[] { "app|component:helper", "app|component:main" }, request.ComponentKeys);
        Assert.Equal(startedAt, request.StartedAt);
        Assert.Equal(DateTimeOffset.MaxValue, request.Deadline);
        Assert.Equal(NaturalStableObservationOrigin.PostTrim, request.Origin);
    }

    [Fact]
    public void ExistingBackoffObservationKeepsItsScopeWhenAnotherComponentIsAlsoBlocked()
    {
        const long mib = 1024L * 1024;
        const string familyKey = "app";
        const string main = "app|component:main";
        const string helper = "app|component:helper";
        var now = DateTimeOffset.UtcNow;
        ApplicationBenefitLearningRecord Learning(string componentKey) => new(
            familyKey, 0.5, 1, 0, now)
        {
            ComponentKey = componentKey,
            ValidSampleCount = 1,
            DistinctLaunchCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { Learning(main), Learning(helper) }, now);
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(familyKey, 1, 300, null, false, false)
            {
                TargetKey = main
            },
            new ApplicationBackoffProgress(familyKey, 2, 600, null, false, false)
            {
                TargetKey = helper
            }
        }, now);
        var scopeKey = ApplicationStableScopeIdentity.For(familyKey, new[] { main });
        tracker.ObserveNaturalStableStates(new[]
        {
            new NaturalStableStateSnapshot(
                familyKey, scopeKey, new[] { main }, "launch-1", 100 * mib,
                IsForeground: false, IsLowActivity: true)
            {
                RecoveryStartedAt = now,
                RecoveryDeadline = now + TimeSpan.FromMinutes(5),
                RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
            }
        }, now, StableStateSuppressionSettings.For(OptimizationProfile.Turbo));

        var requests = tracker.NaturalStableScopeRequests(now + TimeSpan.FromSeconds(1));

        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, request => request.ComponentKeys.SequenceEqual(new[] { main }));
        Assert.Contains(requests, request => request.ComponentKeys.SequenceEqual(new[] { helper }));
        Assert.DoesNotContain(requests, request => request.ComponentKeys.Count == 2);
    }

    [Fact]
    public void StaggeredPendingComponentsKeepTheOriginalPostTrimDeadline()
    {
        const long mib = 1024L * 1024;
        var startedAt = DateTimeOffset.UtcNow;
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var tracker = new ApplicationReboundBackoffTracker();
        const string main = "app|component:main";
        const string helper = "app|component:helper";
        tracker.BeginComponent("app", main, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt, learnOutcome: true,
            targetProcessIds: new[] { 1 }, launchSignature: "main-launch");
        tracker.BeginComponent("app", helper, null, 800 * mib, 80 * mib,
            ReboundBackoffSettings.Default, startedAt + TimeSpan.FromSeconds(10), learnOutcome: true,
            targetProcessIds: new[] { 2 }, launchSignature: "helper-launch");
        ProcessFamilySnapshot Family(long mainBytes, long helperBytes) => new("app", "app", null, new[]
        {
            new ProcessSnapshot(1, "main", null, null, mainBytes, 0, 0, false, false, true, 90),
            new ProcessSnapshot(2, "helper", null, null, helperBytes, 0, 0, false, false, true, 90)
        });
        NaturalStableStateSnapshot Snapshot(NaturalStableScopeRequest request) => new(
            "app", ApplicationStableScopeIdentity.For("app", request.ComponentKeys), request.ComponentKeys,
            "app-launch", 300 * mib, IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = request.StartedAt,
            RecoveryDeadline = request.Deadline,
            RecoveryOrigin = request.Origin
        };

        var firstCompletion = startedAt + ReboundBackoffSettings.Default.LateWindow;
        tracker.Observe(new[] { Family(200 * mib, 200 * mib) }, firstCompletion);
        var sharedRequest = Assert.Single(tracker.NaturalStableScopeRequests(
            firstCompletion, settings.MaximumStableValidationDuration));
        Assert.Equal(new[] { helper, main }, sharedRequest.ComponentKeys);
        Assert.Equal(NaturalStableObservationOrigin.PostTrim, sharedRequest.Origin);
        Assert.Equal(DateTimeOffset.MaxValue, sharedRequest.Deadline);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(sharedRequest) }, firstCompletion, settings);

        var secondCompletion = firstCompletion + TimeSpan.FromSeconds(10);
        tracker.Observe(new[] { Family(200 * mib, 200 * mib) }, secondCompletion);
        var completedRequest = Assert.Single(tracker.NaturalStableScopeRequests(
            secondCompletion, settings.MaximumStableValidationDuration));
        tracker.ObserveNaturalStableStates(new[] { Snapshot(completedRequest) }, secondCompletion, settings);

        var active = Assert.Single(tracker.NaturalStableObservationStatuses());
        Assert.Equal(NaturalStableObservationOrigin.PostTrim, active.Origin);
        Assert.Equal(DateTimeOffset.MaxValue, active.Deadline);
    }

    [Fact]
    public void SustainedGrowthStopsBackoffStableSamplingWithoutClearingBackoff()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt + TimeSpan.FromSeconds(30),
            RecoveryDeadline = startedAt + TimeSpan.FromSeconds(30) +
                               ReboundBackoffSettings.Default.FirstBackoff,
            RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(100 * mib) }, startedAt, settings);
        foreach (var sample in new[]
                 {
                     (30, 700L), (60, 740L), (90, 780L), (120, 820L),
                     (150, 860L), (180, 900L), (210, 900L), (240, 900L)
                 })
        {
            var observedAt = startedAt + TimeSpan.FromSeconds(sample.Item1);
            tracker.Observe(new[] { Family("app", sample.Item2 * mib) }, observedAt);
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Item2 * mib) }, observedAt, settings);
        }

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.True(tracker.IsBlocked("app", startedAt + TimeSpan.FromMinutes(4)));
        Assert.Equal(1, tracker.ReboundCount("app"));

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(900 * mib) },
            startedAt + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), settings);
        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void LongTermBackoffConvergenceBecomesSessionStableWithoutResettingReboundHistory()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("app", 2, 0, null, false, false)
            {
                TargetKey = componentKey
            }
        }, startedAt);
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt + TimeSpan.FromSeconds(30),
            RecoveryDeadline = startedAt + TimeSpan.FromSeconds(30) +
                               ApplicationReboundBackoffTracker.LongTermBackoffStableObservationWindow,
            RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(100 * mib) }, startedAt, settings);
        foreach (var sample in new[]
                 {
                     (30, 700L), (60, 720L), (90, 740L), (120, 760L),
                     (150, 780L), (180, 800L), (210, 800L), (240, 800L)
                 })
        {
            var observedAt = startedAt + TimeSpan.FromSeconds(sample.Item1);
            tracker.Observe(new[] { Family("app", sample.Item2 * mib) }, observedAt);
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Item2 * mib) }, observedAt, settings);
        }

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(800 * mib) },
            startedAt + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), settings);

        Assert.Equal(3, tracker.ReboundCount("app"));
        Assert.False(tracker.IsBlocked("app", startedAt + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30)));
        Assert.Null(tracker.GetBackoffStatus("app", startedAt + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30)));
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.StartsWith("backoff:",
            Assert.Single(tracker.FamilyStableLearningRecords).StableSamples.Single().RecoveryCycleId,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultiComponentBackoffConvergesIntoOneFamilyScopeAndClearsAllBackoffs()
    {
        const long mib = 1024L * 1024;
        const string familyKey = "app";
        const string mainKey = "app|component:main";
        const string serviceKey = "app|component:service";
        var startedAt = DateTimeOffset.UtcNow;
        ApplicationBenefitLearningRecord Learning(string componentKey) => new(
            familyKey, 0.5, 1, 0, startedAt)
        {
            ComponentKey = componentKey,
            ValidSampleCount = 1,
            DistinctLaunchCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { Learning(mainKey), Learning(serviceKey) },
            startedAt);
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(familyKey, 1, 300, null, false, false)
            {
                TargetKey = mainKey
            },
            new ApplicationBackoffProgress(familyKey, 2, 600, null, false, false)
            {
                TargetKey = serviceKey
            }
        }, startedAt);
        var componentKeys = new[] { mainKey, serviceKey };
        var scopeKey = ApplicationStableScopeIdentity.For(familyKey, componentKeys);
        NaturalStableStateSnapshot Snapshot() => new(
            familyKey, scopeKey, componentKeys, "launch-1", 300 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt
        };
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);

        foreach (var seconds in new[] { 0, 30, 60, 90, 120, 150, 180, 240, 300 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() },
                startedAt + TimeSpan.FromSeconds(seconds),
                settings);
        }

        Assert.False(tracker.IsBlocked(familyKey, startedAt + TimeSpan.FromMinutes(5)));
        Assert.Null(tracker.GetBackoffStatus(familyKey, startedAt + TimeSpan.FromMinutes(5)));
        var stable = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(componentKeys.Order(), stable.ComponentKeys.Order());
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void BackoffRecoveryIgnoresUnlearnedSiblingAndCreatesCurrentFamilySessionHold()
    {
        const long mib = 1024L * 1024;
        const string familyKey = "app";
        const string mainPath = @"C:\Apps\Suite\main.exe";
        const string servicePath = @"C:\Apps\Suite\service.exe";
        const string helperPath = @"C:\Apps\Suite\helper.exe";
        var startedAt = DateTimeOffset.UtcNow;
        var family = new ProcessFamilySnapshot(familyKey, "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", mainPath, null, 100 * mib, 0, 0, false, false, true, 90, 100),
            new ProcessSnapshot(2, "service", servicePath, null, 100 * mib, 0, 0, false, false, true, 90, 200),
            new ProcessSnapshot(3, "helper", helperPath, null, 100 * mib, 0, 0, false, false, true, 90, 300)
        });
        var mainKey = ApplicationComponentIdentity.ForExecutable(familyKey, mainPath);
        var serviceKey = ApplicationComponentIdentity.ForExecutable(familyKey, servicePath);
        var helperKey = ApplicationComponentIdentity.ForExecutable(familyKey, helperPath);
        ApplicationBenefitLearningRecord Learning(string componentKey) => new(
            familyKey, 0.5, 1, 0, startedAt)
        {
            ComponentKey = componentKey,
            ValidSampleCount = 1,
            DistinctLaunchCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { Learning(mainKey), Learning(serviceKey) },
            startedAt);
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(familyKey, 1, 300, null, false, false) { TargetKey = mainKey },
            new ApplicationBackoffProgress(familyKey, 2, 600, null, false, false) { TargetKey = serviceKey }
        }, startedAt);
        var optimizationSettings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0
        };
        var stableSettings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        var readiness = family.Processes.ToDictionary(
            process => process.ProcessId,
            _ => new CandidateIdleReadiness(1, 2, true));

        foreach (var seconds in new[] { 0, 30, 60, 90, 120, 150, 180, 240, 300 })
        {
            var snapshots = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
                new[] { family }, optimizationSettings, new ProtectionRules(), readiness,
                tracker.FamilyStableLearningRecords,
                tracker.NaturalStableScopeRequests(startedAt + TimeSpan.FromSeconds(seconds)));
            tracker.ObserveNaturalStableStates(
                snapshots, startedAt + TimeSpan.FromSeconds(seconds), stableSettings);
        }

        Assert.False(tracker.IsBlocked(familyKey, startedAt + TimeSpan.FromMinutes(5)));
        var stable = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(new[] { mainKey, serviceKey }.Order(), stable.ComponentKeys.Order());
        Assert.DoesNotContain(helperKey, stable.ComponentKeys);

        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, tracker.FamilyStableLearningRecords,
            optimizationSettings, new ProtectionRules(), stableSettings,
            startedAt + TimeSpan.FromMinutes(5), tracker.StableCandidateStatuses);
        Assert.Equal(new[] { helperKey, mainKey, serviceKey }.Order(), suppressed.Order());

        var scopeKey = ApplicationStableScopeIdentity.For(familyKey, new[] { mainKey, serviceKey });
        var familyScopeKey = ApplicationStableScopeIdentity.For(
            familyKey, new[] { helperKey, mainKey, serviceKey });
        Assert.Contains(tracker.StableCandidateStatuses,
            candidate => string.Equals(candidate.ComponentKey, scopeKey, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tracker.StableCandidateStatuses,
            candidate => string.Equals(candidate.ComponentKey, familyScopeKey, StringComparison.OrdinalIgnoreCase));
        var cappedSnapshot = new NaturalStableStateSnapshot(
            familyKey, scopeKey, new[] { mainKey, serviceKey }, "launch-1", 400 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            FamilyScopeKey = familyScopeKey,
            FamilyScopeComponentKeys = new[] { helperKey, mainKey, serviceKey },
            FamilyScopeLaunchSignature = "family-launch-1",
            FamilyScopeWorkingSetBytes = 800 * mib
        };

        tracker.ObserveNaturalStableStates(
            new[] { cappedSnapshot },
            startedAt + TimeSpan.FromMinutes(6),
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo));

        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.NaturalStableScopeRequests(
            startedAt + TimeSpan.FromMinutes(6),
            stableSettings.MaximumStableValidationDuration));
        Assert.Single(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void ForegroundActivityKeepsBackoffObservationAndResetsStableProgress()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes, bool foreground = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: foreground, IsLowActivity: !foreground)
        {
            RecoveryStartedAt = startedAt
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(100 * mib) }, startedAt, settings);
        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromSeconds(30));
        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib, foreground: true) },
            startedAt + TimeSpan.FromMinutes(2),
            settings);

        Assert.NotEmpty(tracker.NaturalStableScopeRequests(startedAt + TimeSpan.FromMinutes(3)));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib) },
            startedAt + TimeSpan.FromMinutes(3),
            settings);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void HighBackoffSampleProtectsTheCurrentLaunchWithoutRaisingTheTrustedAnchor()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var trustedSample = new ApplicationStableSample(
            200 * mib, startedAt.AddHours(-1), "launch-0", "cycle-0", 1, PendingHigh: false);
        var trustedRecord = new ApplicationStableLearningRecord(
            "app", new[] { 200 * mib }, trustedSample.ObservedAt, "launch-0")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[] { trustedSample },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 200 * mib,
            LastStableLaunchSampleCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            learningRecords: null,
            now: startedAt,
            familyStableLearningRecords: new[] { trustedRecord });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(100 * mib) }, startedAt, settings);
        tracker.DrainCompletedStableObservations();
        foreach (var seconds in new[] { 30, 60, 90, 120, 150 })
        {
            tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromSeconds(seconds));
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(700 * mib) }, startedAt + TimeSpan.FromSeconds(seconds), settings);
        }
        tracker.Observe(new[] { Family("app", 700 * mib) }, startedAt + TimeSpan.FromMinutes(3));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib) }, startedAt + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(700 * mib) }, startedAt + TimeSpan.FromMinutes(5), settings);

        var record = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(200 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record));
        Assert.Contains(record.StableSamples, sample =>
            sample.WorkingSetBytes == 700 * mib && sample.PendingHigh);
        var runtime = Assert.Single(tracker.StableCandidateStatuses);
        Assert.Equal(ApplicationStableCandidateState.Converged, runtime.State);
        Assert.Equal(700 * mib, runtime.CandidateBytes);
        Assert.Contains(tracker.DrainCompletedStableObservations(), observation =>
            observation.Decision == ApplicationStableObservationDecision.HighAnchorPending);
    }

    [Fact]
    public void NormalRecoveryRestartsStableObservationAfterTheBenefitWindow()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var trustedSample = new ApplicationStableSample(
            100 * mib, startedAt.AddHours(-1), "launch-0", "cycle-0", 1, PendingHigh: false);
        var trustedRecord = new ApplicationStableLearningRecord(
            "app", new[] { 100 * mib }, trustedSample.ObservedAt, "launch-0")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[] { trustedSample },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 100 * mib,
            LastStableLaunchSampleCount = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            learningRecords: null,
            now: startedAt,
            familyStableLearningRecords: new[] { trustedRecord });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        tracker.BeginComponent(
            "app", componentKey, @"C:\Apps\app.exe", 800 * mib, 20 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1 },
            launchSignature: "launch-1");

        NaturalStableStateSnapshot Snapshot(long bytes, DateTimeOffset recoveryStartedAt) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = recoveryStartedAt
        };

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(20 * mib, startedAt) }, startedAt, settings);
        var benefitCompletedAt = startedAt + TimeSpan.FromMinutes(2);
        tracker.Observe(new[] { Family("app", 30 * mib) }, benefitCompletedAt);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(30 * mib, benefitCompletedAt) }, benefitCompletedAt, settings);

        foreach (var seconds in new[] { 30, 60, 90, 120, 150 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(250 * mib, benefitCompletedAt) },
                benefitCompletedAt + TimeSpan.FromSeconds(seconds),
                settings);
        }

        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(250 * mib, benefitCompletedAt) },
            benefitCompletedAt + TimeSpan.FromMinutes(3),
            settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(250 * mib, benefitCompletedAt) },
            benefitCompletedAt + TimeSpan.FromMinutes(5), settings);

        var stable = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(100 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(stable));
        Assert.Contains(stable.StableSamples, sample =>
            sample.WorkingSetBytes >= 249 * mib &&
            sample.WorkingSetBytes <= 251 * mib &&
            sample.PendingHigh);
        Assert.Equal(
            ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void MissingRecoveryScopeClearsAwaitingStableEligibility()
    {
        const string componentKey = "app|component:main";
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.BeginComponent(
            "app", componentKey, @"C:\Apps\app.exe", 800, 20,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1 },
            launchSignature: "launch-1");
        tracker.Observe(new[] { Family("app", 30) }, startedAt + TimeSpan.FromMinutes(2));
        Assert.Contains(componentKey, tracker.NaturalStableRecoveryEligibleComponentKeys());

        tracker.ObserveNaturalStableStates(
            Array.Empty<NaturalStableStateSnapshot>(),
            startedAt + TimeSpan.FromMinutes(2),
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo));

        Assert.Empty(tracker.NaturalStableRecoveryEligibleComponentKeys());
    }

    [Fact]
    public void RollingHighSampleReplacesTheOldestHighSlotWithoutEvictingTrustedEvidence()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        const string launchSignature = "launch-1";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new ApplicationStableSample(200 * mib, now.AddMinutes(-60), launchSignature, "cycle-1", 1, false),
            new ApplicationStableSample(205 * mib, now.AddMinutes(-45), launchSignature, "cycle-2", 1, false),
            new ApplicationStableSample(700 * mib, now.AddMinutes(-30), launchSignature, "cycle-3", 1, true)
        };
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(), samples[^1].ObservedAt, launchSignature)
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 200 * mib,
            LastStableLaunchSampleCount = 3,
            HistoricalReviewScheduleVersion = 2
        };
        var tracker = new ApplicationReboundBackoffTracker(
            learningRecords: null,
            now: now,
            familyStableLearningRecords: new[] { record });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        PrimeNaturalRecovery(tracker, now, componentKey, launchSignature, 710 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, launchSignature, bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(710 * mib) }, now, settings);
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(710 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(710 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(710 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        var updated = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(3, updated.StableSamples.Count);
        Assert.Equal(2, updated.LastStableLaunchSampleCount);
        Assert.Contains(updated.StableSamples, sample => sample.WorkingSetBytes == 200 * mib && !sample.PendingHigh);
        Assert.Contains(updated.StableSamples, sample => sample.WorkingSetBytes == 205 * mib && !sample.PendingHigh);
        Assert.DoesNotContain(updated.StableSamples, sample => sample.WorkingSetBytes == 700 * mib);
        Assert.Contains(updated.StableSamples, sample => sample.WorkingSetBytes == 710 * mib && sample.PendingHigh);
        Assert.Equal((200 * mib + 205 * mib) / 2, StableAnchorLearningPolicy.EffectiveAnchorBytes(updated));
    }

    [Fact]
    public void PendingHighSlotDoesNotBlockAThirdAcceptedSampleInTheSameLaunch()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        const string launchSignature = "launch-1";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new ApplicationStableSample(200 * mib, now.AddMinutes(-60), launchSignature, "cycle-1", 1, false),
            new ApplicationStableSample(205 * mib, now.AddMinutes(-45), launchSignature, "cycle-2", 1, false),
            new ApplicationStableSample(700 * mib, now.AddMinutes(-30), launchSignature, "cycle-3", 1, true)
        };
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(), samples[^1].ObservedAt, launchSignature)
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 200 * mib,
            LastStableLaunchSampleCount = 3,
            HistoricalReviewScheduleVersion = 2
        };
        var tracker = new ApplicationReboundBackoffTracker(
            learningRecords: null, now: now, familyStableLearningRecords: new[] { record });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableWorkingSetBytes = long.MaxValue
        };
        PrimeNaturalRecovery(tracker, now, componentKey, launchSignature, 210 * mib);
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scopeKey, new[] { componentKey }, launchSignature, 210 * mib,
            IsForeground: false, IsLowActivity: true);

        foreach (var minutes in new[] { 0d, 2d, 3d, 5d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, now + TimeSpan.FromMinutes(minutes), settings);

        var updated = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(4, updated.StableSamples.Count);
        Assert.Equal(3, StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
            updated, launchSignature));
        Assert.Equal(3, updated.LastStableLaunchSampleCount);
        Assert.Contains(updated.StableSamples, sample => sample.WorkingSetBytes == 700 * mib && sample.PendingHigh);
        Assert.Contains(updated.StableSamples, sample => sample.WorkingSetBytes == 210 * mib && !sample.PendingHigh);
    }

    [Fact]
    public void OneLateStepExtendsOnlyUntilAOneMinuteTailConverges()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 50 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt
        };

        foreach (var sample in new[] { (0, 200L), (30, 200L), (60, 200L), (90, 260L) })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Item2 * mib) },
                startedAt + TimeSpan.FromSeconds(sample.Item1), settings);
        tracker.DrainCompletedStableObservations();
        tracker.Observe(new[] { Family("app", 260 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, startedAt + TimeSpan.FromMinutes(2), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, startedAt + TimeSpan.FromMinutes(3), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, startedAt + TimeSpan.FromMinutes(5), settings);
        Assert.Single(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void SustainedGrowthRemainsInRollingObservationUntilAStablePlatformForms()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 50 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt
        };

        foreach (var sample in new[] { (0, 180L), (30, 200L), (60, 220L), (90, 240L) })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Item2 * mib) },
                startedAt + TimeSpan.FromSeconds(sample.Item1), settings);
        tracker.DrainCompletedStableObservations();
        tracker.Observe(new[] { Family("app", 260 * mib) }, startedAt + TimeSpan.FromMinutes(2));
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, startedAt + TimeSpan.FromMinutes(2), settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(280 * mib) }, startedAt + TimeSpan.FromMinutes(2.5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(300 * mib) }, startedAt + TimeSpan.FromMinutes(3), settings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        var growthStatus = Assert.Single(tracker.NaturalStableObservationStatuses());
        Assert.False(growthStatus.IsGrowthReview);
        Assert.Equal(300 * mib, growthStatus.LatestWorkingSetBytes);
        Assert.NotNull(growthStatus.LastObservedAt);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, startedAt + TimeSpan.FromMinutes(5), settings);

        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void NewPostTrimRecoveryCanRestartObservationAfterSameLaunchWasExcluded()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        NaturalStableStateSnapshot Snapshot(long bytes, DateTimeOffset recoveryStartedAt) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = recoveryStartedAt
        };

        PrimeNaturalRecovery(tracker, startedAt, componentKey, "launch-1", 180 * mib);
        foreach (var sample in new[] { (0, 180L), (30, 200L), (60, 220L), (90, 240L), (120, 260L) })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Item2 * mib, startedAt - ReboundBackoffSettings.Default.LateWindow) },
                startedAt + TimeSpan.FromSeconds(sample.Item1),
                settings);
        }

        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());

        var nextRecoveryCompletedAt = startedAt + TimeSpan.FromMinutes(5);
        PrimeNaturalRecovery(
            tracker,
            nextRecoveryCompletedAt,
            componentKey,
            "launch-1",
            200 * mib);
        tracker.ObserveNaturalStableStates(
            new[]
            {
                Snapshot(
                    200 * mib,
                    nextRecoveryCompletedAt - ReboundBackoffSettings.Default.LateWindow)
            },
            nextRecoveryCompletedAt,
            settings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
    }

    [Fact]
    public void NewLaunchNeedsPostTrimRecoveryUntilLongTermSampleRequirementIsMet()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        NaturalStableStateSnapshot Snapshot(long bytes, string launch) => new(
            "app", scopeKey, new[] { componentKey }, launch, bytes,
            IsForeground: false, IsLowActivity: true);

        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib, "launch-1") }, now, settings);
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib, "launch-1") },
            now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib, "launch-1") },
            now + TimeSpan.FromMinutes(5), settings);
        Assert.Single(tracker.FamilyStableLearningRecords.Single().StableWorkingSetSamplesBytes);

        tracker.ObserveNaturalStableStates(
            Array.Empty<NaturalStableStateSnapshot>(),
            now + TimeSpan.FromMinutes(6), settings);
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib, "launch-2") },
            now + TimeSpan.FromMinutes(7), settings);

        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.DrainCompletedStableObservations());
        var record = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Single(record.StableWorkingSetSamplesBytes);
        Assert.Equal("launch-1", record.LastStableLaunchSignature);
    }

    [Fact]
    public void NaturalStableLearningUsesCustomObservationIntervalAndPerLaunchLimit()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableValidationDuration = TimeSpan.FromMinutes(3),
            NaturalStableSampleInterval = TimeSpan.FromMinutes(5),
            MaximumStableSamplesPerLaunch = 2
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool firstBootGate = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RequiresFirstBootAnchorGate = firstBootGate
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);
        Assert.Single(tracker.FamilyStableLearningRecords.Single().StableWorkingSetSamplesBytes);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(201 * mib) }, now + TimeSpan.FromMinutes(20), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, now + TimeSpan.FromMinutes(22), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, now + TimeSpan.FromMinutes(23), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, now + TimeSpan.FromMinutes(25), settings);
        var firstReview = tracker.FamilyStableLearningRecords.Single();
        Assert.Equal(2, firstReview.StableWorkingSetSamplesBytes.Count);
        Assert.Equal(0, firstReview.HistoricalReviewSuccessCount);
        Assert.Equal(now + TimeSpan.FromMinutes(38), Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule("app", new[] { componentKey }, settings)).NextReviewAt);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(35), settings);
        Assert.Equal(2, tracker.FamilyStableLearningRecords.Single().StableWorkingSetSamplesBytes.Count);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) }, now + TimeSpan.FromMinutes(40), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) }, now + TimeSpan.FromMinutes(42), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) }, now + TimeSpan.FromMinutes(43), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) }, now + TimeSpan.FromMinutes(45), settings);
        var rolled = tracker.FamilyStableLearningRecords.Single();
        Assert.Equal(2, rolled.StableWorkingSetSamplesBytes.Count);
        Assert.Equal(0, rolled.HistoricalReviewSuccessCount);
        Assert.Equal(0, rolled.LastStableLaunchSampleCount);
        Assert.DoesNotContain(202 * mib, rolled.StableWorkingSetSamplesBytes);
        Assert.Equal(now + TimeSpan.FromMinutes(43), rolled.StableLastObservedAt);
    }

    [Fact]
    public void HistoricalStableReviewCommitsOnTheNextStableRefreshAfterOneMinuteTail()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            NaturalStableSampleInterval = TimeSpan.FromMinutes(5)
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        var reviewStartedAt = now + TimeSpan.FromMinutes(20);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, reviewStartedAt, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, reviewStartedAt + TimeSpan.FromSeconds(30), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) }, reviewStartedAt + TimeSpan.FromMinutes(1), settings);

        Assert.Contains(componentKey, tracker.NaturalStableReviewComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Equal(StableObservationPhase.ProvisionalValidation,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Phase);
        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(203 * mib) },
            reviewStartedAt + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(3),
            settings);

        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Equal(2, Assert.Single(tracker.FamilyStableLearningRecords).StableSamples.Count);
    }

    [Fact]
    public void ConvergedCurrentLaunchExposesTheNextHistoricalReviewSchedule()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        var schedule = Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule("app", new[] { componentKey }, settings));
        Assert.Equal(now + TimeSpan.FromMinutes(20), schedule.NextReviewAt);
        Assert.Equal(0, schedule.CompletedReviewCount);
        Assert.Equal(3, schedule.InitialReviewTarget);

        var nextLaunch = Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule(
                "app", new[] { componentKey }, settings, "launch-2"));
        Assert.Equal(0, nextLaunch.CompletedReviewCount);
    }

    [Fact]
    public void HistoricalReviewSessionProgressRestoresOnlyForTheSameLaunch()
    {
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var record = new ApplicationStableLearningRecord("app", new[] { 200L, 200L, 200L }, now, "launch-1")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = new[]
            {
                new ApplicationStableSample(200, now - TimeSpan.FromMinutes(30), "launch-a", "history", 1, PendingHigh: false),
                new ApplicationStableSample(200, now - TimeSpan.FromMinutes(15), "launch-b", "history", 1, PendingHigh: false),
                new ApplicationStableSample(200, now, "launch-1", "history", 1, PendingHigh: false)
            },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 200,
            LastStableLaunchSampleCount = 3,
            HistoricalReviewScheduleVersion = 2
        };
        var tracker = new ApplicationReboundBackoffTracker(
            familyStableLearningRecords: new[] { record });
        tracker.RestoreHistoricalReviewSessionProgress(new[]
        {
            new HistoricalReviewSessionProgress(scopeKey, "launch-1", 2)
        });
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);

        Assert.Equal(2, Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule("app", new[] { componentKey }, settings, "launch-1"))
            .CompletedReviewCount);
        Assert.Equal(0, Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule("app", new[] { componentKey }, settings, "launch-2"))
            .CompletedReviewCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HistoricalReviewScheduleStartsFreshForEachApplicationLaunch(
        int persistedCompletedReviews)
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var sample = new ApplicationStableSample(
            200 * mib, now, "launch-1", "cycle-1", 1, PendingHigh: false);
        var record = new ApplicationStableLearningRecord(
            "app", new[] { sample.WorkingSetBytes }, now, "launch-1")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 1,
            StableSamples = new[] { sample },
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = sample.WorkingSetBytes,
            HistoricalReviewSuccessCount = persistedCompletedReviews,
            HistoricalReviewScheduleVersion = 2
        };
        var tracker = new ApplicationReboundBackoffTracker(
            Array.Empty<ApplicationBenefitLearningRecord>(), now, new[] { record });

        var schedule = Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule(
                "app",
                new[] { componentKey },
                StableStateSuppressionSettings.For(OptimizationProfile.Turbo)));

        Assert.Equal(now + TimeSpan.FromMinutes(15), schedule.NextReviewAt);
        Assert.Equal(0, schedule.CompletedReviewCount);
    }

    [Fact]
    public void PendingClusterCannotRetainHistoricalReviewProgressAndKeepsFifteenMinuteSchedule()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var pendingSamples = Enumerable.Range(0, 3)
            .Select(index => new ApplicationStableSample(
                (200 + index) * mib,
                now - TimeSpan.FromMinutes(30 - index * 15),
                "launch-1",
                $"passive:launch-1:{index}",
                Generation: 0,
                PendingHigh: false))
            .ToArray();
        var record = new ApplicationStableLearningRecord(
            "app", pendingSamples.Select(sample => sample.WorkingSetBytes).ToArray(),
            now, "launch-1")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            StableSamples = pendingSamples,
            AnchorGeneration = 0,
            AnchorGenerationBaselineBytes = 0,
            HistoricalReviewSuccessCount = 3,
            HistoricalReviewScheduleVersion = 2
        };

        var tracker = new ApplicationReboundBackoffTracker(
            Array.Empty<ApplicationBenefitLearningRecord>(), now, new[] { record });
        var migrated = Assert.Single(tracker.FamilyStableLearningRecords);

        Assert.Equal(0, migrated.HistoricalReviewSuccessCount);
        Assert.Equal(2, migrated.HistoricalReviewScheduleVersion);
        var schedule = Assert.IsType<NaturalStableReviewSchedule>(tracker.GetNaturalStableReviewSchedule(
            "app", new[] { componentKey },
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo)));
        Assert.Equal(0, schedule.CompletedReviewCount);
        Assert.Equal(now + TimeSpan.FromMinutes(15), schedule.NextReviewAt);
    }

    [Fact]
    public void LegacyStableRecordUsesAcceptedLaunchSamplesToInitializeReviewSchedule()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 3)
            .Select(index => new ApplicationStableSample(
                (200 + index) * mib,
                now - TimeSpan.FromMinutes((2 - index) * 18),
                "launch-1",
                $"passive:launch-1:{index}",
                1,
                PendingHigh: false))
            .ToArray();
        var legacyRecord = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            now, "launch-1")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 3,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = samples[0].WorkingSetBytes
        };

        var tracker = new ApplicationReboundBackoffTracker(
            Array.Empty<ApplicationBenefitLearningRecord>(), now, new[] { legacyRecord });
        var migrated = Assert.Single(tracker.FamilyStableLearningRecords);
        var schedule = Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule(
                "app",
                new[] { componentKey },
                StableStateSuppressionSettings.For(OptimizationProfile.Turbo)));

        Assert.Equal(0, migrated.HistoricalReviewSuccessCount);
        Assert.Equal(2, migrated.HistoricalReviewScheduleVersion);
        Assert.Equal(now + TimeSpan.FromMinutes(15), schedule.NextReviewAt);
    }

    [Fact]
    public void VersionOneReviewProgressDropsPendingHighReviewsAndWaitsForANewCycle()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var accepted = Enumerable.Range(0, 3)
            .Select(index => new ApplicationStableSample(
                (21 + index) * mib,
                now - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(index),
                "launch-low",
                $"low-cycle-{index}",
                1,
                PendingHigh: false))
            .ToArray();
        var pendingHigh = Enumerable.Range(0, 6)
            .Select(index => new ApplicationStableSample(
                (79 + index % 2) * mib,
                now - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(index * 18),
                index < 3 ? "launch-high-1" : "launch-high-2",
                index < 3 ? "passive:launch-high-1" : "passive:launch-high-2",
                1,
                PendingHigh: true))
            .ToArray();
        var samples = accepted.Concat(pendingHigh).ToArray();
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            samples[^1].ObservedAt, "launch-high-2")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 0,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 22 * mib,
            HistoricalReviewSuccessCount = 2,
            HistoricalReviewScheduleVersion = 1
        };
        var tracker = new ApplicationReboundBackoffTracker(
            Array.Empty<ApplicationBenefitLearningRecord>(), now, new[] { record });
        var migrated = Assert.Single(tracker.FamilyStableLearningRecords);
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var schedule = Assert.IsType<NaturalStableReviewSchedule>(
            tracker.GetNaturalStableReviewSchedule(
                "app", new[] { componentKey }, settings, "launch-high-2"));

        Assert.Equal(0, migrated.HistoricalReviewSuccessCount);
        Assert.Equal(2, migrated.HistoricalReviewScheduleVersion);
        Assert.Equal(2, schedule.HighMigrationRecoveryCycleCount);
        Assert.False(schedule.AwaitingNewRecoveryCycle);

        tracker.ObserveNaturalStableStates(
            new[]
            {
                new NaturalStableStateSnapshot(
                    "app",
                    ApplicationStableScopeIdentity.For("app", new[] { componentKey }),
                    new[] { componentKey },
                    "launch-high-2",
                    80 * mib,
                    IsForeground: false,
                    IsLowActivity: true)
            },
            now + TimeSpan.FromHours(4),
            settings);

        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
    }

    [Fact]
    public void PersistedCurrentLaunchSampleRebuildsSessionHoldAfterTrackerRestart()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new ApplicationStableSample(188 * mib, now - TimeSpan.FromMinutes(4),
                "launch-1", "cycle-1", 1, PendingHigh: false),
            new ApplicationStableSample(206 * mib, now - TimeSpan.FromMinutes(2),
                "launch-1", "cycle-2", 1, PendingHigh: false),
            new ApplicationStableSample(226 * mib, now - TimeSpan.FromMinutes(1),
                "launch-1", "cycle-3", 1, PendingHigh: true)
        };
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            samples[^1].ObservedAt, "launch-1")
        {
            ComponentKeys = new[] { componentKey },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 3,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 188 * mib
        };
        var tracker = new ApplicationReboundBackoffTracker(
            Array.Empty<ApplicationBenefitLearningRecord>(), now, new[] { record });
        tracker.EnablePersistedSessionHoldRestoration();
        var snapshot = new NaturalStableStateSnapshot(
            "app", scopeKey, new[] { componentKey }, "launch-1", 226 * mib,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(
            new[] { snapshot }, now, StableStateSuppressionSettings.For(OptimizationProfile.Turbo));

        var status = Assert.Single(tracker.StableCandidateStatuses);
        Assert.Equal(ApplicationStableCandidateState.Converged, status.State);
        Assert.Equal(226 * mib, status.CandidateBytes);
    }

    [Fact]
    public void StableReviewExceedingTheHistoricalLimitStaysExcludedForTheCurrentRuntime()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            NaturalStableSampleInterval = TimeSpan.FromMinutes(5)
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool foreground = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: foreground, IsLowActivity: !foreground);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);
        Assert.Single(tracker.FamilyStableLearningRecords);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib) }, now + TimeSpan.FromMinutes(20), settings);
        Assert.Contains(componentKey, tracker.NaturalStableReviewComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(21), settings);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Excluded,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords).StableSamples);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib) }, now + TimeSpan.FromMinutes(22), settings);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib) }, now + TimeSpan.FromMinutes(26), settings);
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Excluded,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void ForegroundInterruptedStableReviewDoesNotStartTheReviewCooldown()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            NaturalStableSampleInterval = TimeSpan.FromMinutes(5)
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool foreground = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: foreground, IsLowActivity: !foreground);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib) }, now + TimeSpan.FromMinutes(20), settings);
        Assert.Contains(componentKey, tracker.NaturalStableReviewComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib, foreground: true) },
            now + TimeSpan.FromMinutes(21), settings);
        Assert.Contains(componentKey, tracker.NaturalStableReviewComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(205 * mib) }, now + TimeSpan.FromMinutes(21.1), settings);
        Assert.Contains(componentKey, tracker.NaturalStableReviewComponentKeys());
    }

    [Fact]
    public void RegularStableObservationContinuesPastTenMinutesUntilAPlatformForms()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            MaximumStableValidationDuration = TimeSpan.FromMinutes(10)
        };
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(300 * mib) }, now + TimeSpan.FromMinutes(9), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(450 * mib) }, now + TimeSpan.FromMinutes(9.5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(600 * mib) }, now + TimeSpan.FromMinutes(10), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Equal(DateTimeOffset.MaxValue,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Deadline);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.Normalize().MaximumStableValidationDuration);
    }

    [Fact]
    public void TimedOutFirstRecoveryDoesNotRestartFromAnExistingLongTermBackoff()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("app", 3, 0, 10, false, false)
            {
                TargetKey = componentKey
            }
        }, startedAt);
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 100 * mib,
            ReboundBackoffSettings.Default, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt - TimeSpan.FromSeconds(10),
            RecoveryDeadline = startedAt - TimeSpan.FromSeconds(10) +
                               ApplicationReboundBackoffTracker.LongTermBackoffStableObservationWindow,
            RecoveryOrigin = NaturalStableObservationOrigin.BackoffRecovery
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot(100 * mib) }, startedAt, settings);
        for (var minute = 1; minute <= 9; minute++)
        {
            var observedAt = startedAt + TimeSpan.FromMinutes(minute);
            var workingSetBytes = (100L + minute * 20L) * mib;
            tracker.Observe(new[] { Family("app", workingSetBytes) }, observedAt);
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(workingSetBytes) }, observedAt, settings);
        }

        var expiredAt = startedAt - TimeSpan.FromSeconds(10) +
                        ApplicationReboundBackoffTracker.LongTermBackoffStableObservationWindow +
                        TimeSpan.FromSeconds(1);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(300 * mib) }, expiredAt, settings);

        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableRecoveryEligibleComponentKeys(
            expiredAt, settings.MaximumStableValidationDuration));
        Assert.Empty(tracker.NaturalStableScopeRequests(
            expiredAt + TimeSpan.FromSeconds(1), settings.MaximumStableValidationDuration));
        Assert.True(Assert.IsType<ApplicationBackoffStatus>(
            tracker.GetBackoffStatus("app", expiredAt)).LongTermObservation);
    }

    [Fact]
    public void RestoredLongTermBackoffPastTheStableDeadlineDoesNotRequestAWindow()
    {
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("app", 3, 0, 11 * 60, false, false)
            {
                TargetKey = componentKey
            }
        }, now);
        var window = StableStateSuppressionSettings.For(OptimizationProfile.Turbo)
            .MaximumStableValidationDuration;

        Assert.Empty(tracker.NaturalStableScopeRequests(now, window));
        Assert.True(Assert.IsType<ApplicationBackoffStatus>(
            tracker.GetBackoffStatus("app", now)).LongTermObservation);
    }

    [Fact]
    public void ProvisionalGrowthEntersReviewAndRejectsAHigherPlatform()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true)
        {
            FamilyScopeKey = scopeKey,
            FamilyScopeComponentKeys = new[] { componentKey },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = bytes
        };

        foreach (var minutes in new[] { 0d, 2d, 3d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(minutes), settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(3.1), settings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Equal(64 * mib,
            Assert.Single(tracker.NaturalStableObservationStatuses()).RequiredIncreaseBytes);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(410 * mib) }, now + TimeSpan.FromMinutes(7.9), settings);
        Assert.Contains(componentKey, tracker.NaturalStableGrowthReviewComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(410 * mib) }, now + TimeSpan.FromMinutes(8), settings);
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void GrowthReviewReturnsToTheOriginalPlatformAndRestartsTwoMinuteValidation()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool lowActivity = true) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: lowActivity)
        {
            FamilyScopeKey = scopeKey,
            FamilyScopeComponentKeys = new[] { componentKey },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = bytes
        };

        foreach (var minutes in new[] { 0d, 2d, 3d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(minutes), settings);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(3.25), settings);
        Assert.Contains(componentKey, tracker.NaturalStableGrowthReviewComponentKeys());
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(4), settings);

        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Equal(StableObservationPhase.ProvisionalValidation,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Phase);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(5.9), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(6), settings);
        Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void ActiveGrowthSamplesDoNotEnterGrowthReview()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: false);

        foreach (var sample in new[]
                 {
                     (Seconds: 0, Bytes: 200L),
                     (Seconds: 30, Bytes: 220L),
                     (Seconds: 60, Bytes: 240L),
                     (Seconds: 90, Bytes: 260L),
                     (Seconds: 120, Bytes: 280L)
                 })
        {
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(sample.Bytes * mib) },
                now + TimeSpan.FromSeconds(sample.Seconds),
                settings);
        }

        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void OneTimeWorkingSetSpikeDoesNotRestartTheStableWindow()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool lowActivity = true) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: lowActivity);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib, lowActivity: false) },
            now + TimeSpan.FromMinutes(2.5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(206 * mib) }, now + TimeSpan.FromMinutes(4), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(206 * mib) }, now + TimeSpan.FromMinutes(6), settings);

        Assert.Single(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void BackgroundActivityPreventsAStableSampleFromBeingCommitted()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: false);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.False(Assert.Single(tracker.NaturalStableObservationStatuses()).LatestIsLowActivity);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(204 * mib) },
            now + settings.MaximumStableValidationDuration,
            settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
    }

    [Fact]
    public void OneTimeStepFollowedByAPlateauCanCompleteTheObservation()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool lowActivity = true) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: lowActivity);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib, lowActivity: false) },
            now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) },
            now + TimeSpan.FromMinutes(3), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) },
            now + TimeSpan.FromMinutes(4), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(260 * mib) }, now + TimeSpan.FromMinutes(6), settings);
        Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(
            ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Equal(
            ApplicationStableObservationDecision.Converged,
            Assert.Single(tracker.DrainCompletedStableObservations()).Decision);
    }

    [Fact]
    public void LargeDownwardStepRequiresANewStableTailBeforeCommit()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 400 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(400 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(2), settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(200 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        Assert.Equal(200 * mib, Assert.Single(
            Assert.Single(tracker.FamilyStableLearningRecords).StableWorkingSetSamplesBytes));
    }

    [Fact]
    public void RuntimeStableStateMigratesEquivalentLaunchSignatureWithinItsLimit()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, string launch) => new(
            "app", scopeKey, new[] { componentKey }, launch, bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib, "launch-1") }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib, "launch-1") }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib, "launch-1") }, now + TimeSpan.FromMinutes(5), settings);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(210 * mib, "launch-2") }, now + TimeSpan.FromMinutes(6), settings);

        var migratedStatus = Assert.Single(tracker.StableCandidateStatuses);
        Assert.Equal(ApplicationStableCandidateState.Converged, migratedStatus.State);
        Assert.Equal("launch-2", migratedStatus.LaunchSignature);
        var migratedRecord = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal("launch-2", migratedRecord.LastStableLaunchSignature);
        Assert.Equal(0, migratedRecord.LastStableLaunchSampleCount);
        Assert.Single(migratedRecord.StableWorkingSetSamplesBytes);
        Assert.All(migratedRecord.StableSamples,
            sample => Assert.Equal("launch-2", sample.LaunchSignature));

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(210 * mib, "launch-2") }, now + TimeSpan.FromMinutes(20), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(212 * mib, "launch-2") }, now + TimeSpan.FromMinutes(23), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(212 * mib, "launch-2") }, now + TimeSpan.FromMinutes(25), settings);

        var secondSample = Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Equal(0, secondSample.LastStableLaunchSampleCount);
        Assert.Equal(2, secondSample.StableWorkingSetSamplesBytes.Count);
    }

    [Fact]
    public void PostTrimObservationPreservingAConvergedStatusIsNotHistoricalReview()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);

        var secondRecovery = now + TimeSpan.FromMinutes(6);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 50 * mib,
            ReboundBackoffSettings.Default, secondRecovery,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        var postTrim = Snapshot(205 * mib) with
        {
            RecoveryStartedAt = secondRecovery,
            RecoveryDeadline = secondRecovery + settings.MaximumStableValidationDuration,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };
        tracker.ObserveNaturalStableStates(new[] { postTrim }, secondRecovery, settings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableReviewComponentKeys());
        Assert.Equal(NaturalStableObservationOrigin.PostTrim,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Origin);

        tracker.Observe(new[] { Family("app", 205 * mib) }, secondRecovery + TimeSpan.FromMinutes(2));
        tracker.ObserveNaturalStableStates(
            new[] { postTrim with { WorkingSetBytes = 207 * mib } },
            secondRecovery + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { postTrim with { WorkingSetBytes = 207 * mib } },
            secondRecovery + TimeSpan.FromMinutes(3), settings);

        Assert.Single(Assert.Single(tracker.FamilyStableLearningRecords)
            .StableWorkingSetSamplesBytes);
        Assert.Contains(componentKey, tracker.NaturalStableProvisionalValidationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { postTrim with { WorkingSetBytes = 207 * mib } },
            secondRecovery + TimeSpan.FromMinutes(5),
            settings);

        Assert.Equal(2, Assert.Single(tracker.FamilyStableLearningRecords)
            .StableWorkingSetSamplesBytes.Count);
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());
    }

    [Fact]
    public void ApplicationWithoutBenefitEvidenceDoesNotBecomeStableByBeingIdle()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new NaturalStableStateSnapshot(
            "new-app", "new-app|scope:new-app|component:main",
            new[] { "new-app|component:main" }, "launch-1", 200L * 1024 * 1024,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { snapshot }, now);
        tracker.ObserveNaturalStableStates(
            new[] { snapshot }, now + ApplicationReboundBackoffTracker.NaturalStableRecoveryEligibilityWindow);

        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void DisablingNaturalStableObservationClearsRuntimeStateButKeepsLearning()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);
        var request = Assert.Single(tracker.NaturalStableScopeRequests(now));
        var snapshot = new NaturalStableStateSnapshot(
            request.FamilyKey,
            ApplicationStableScopeIdentity.For(request.FamilyKey, request.ComponentKeys),
            request.ComponentKeys, "launch-1", 70 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = request.StartedAt,
            RecoveryDeadline = request.Deadline,
            RecoveryOrigin = request.Origin
        };

        tracker.ObserveNaturalStableStates(new[] { snapshot }, now, settings);
        Assert.NotEmpty(tracker.NaturalStableObservationComponentKeys());
        Assert.NotEmpty(tracker.NaturalStableRecoveryEligibleComponentKeys());

        tracker.ObserveNaturalStableStates(
            Array.Empty<NaturalStableStateSnapshot>(), now + TimeSpan.FromSeconds(1),
            suppressionSettings: null, enabled: false);

        Assert.Empty(tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableGrowthReviewComponentKeys());
        Assert.Empty(tracker.NaturalStableRecoveryEligibleComponentKeys());
        Assert.Empty(tracker.NaturalStableScopeRequests(now + TimeSpan.FromSeconds(1)));
        Assert.Empty(tracker.StableCandidateStatuses);
        Assert.NotEmpty(tracker.LearningRecords);
    }

    [Fact]
    public void UnstableNaturalRecoveryKeepsRollingPastTenLowActivityMinutes()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var stableSettings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(70 * mib) }, now, stableSettings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(120 * mib) }, now + TimeSpan.FromMinutes(3), stableSettings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(170 * mib) }, now + TimeSpan.FromMinutes(6), stableSettings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(220 * mib) }, now + TimeSpan.FromMinutes(9), stableSettings);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(270 * mib) }, now + TimeSpan.FromMinutes(10), stableSettings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Empty(tracker.NaturalStableRecoveryEligibleComponentKeys(
            now + TimeSpan.FromMinutes(10),
            ApplicationReboundBackoffTracker.NaturalStableRecoveryEligibilityWindow));
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);

        tracker.BeginComponent(
            "app", componentKey, null, 500 * mib, 100 * mib,
            ReboundBackoffSettings.Default, now + TimeSpan.FromMinutes(11),
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        tracker.ObserveNaturalStableStates(
            new[]
            {
                Snapshot(100 * mib) with
                {
                    RecoveryStartedAt = now + TimeSpan.FromMinutes(11),
                    RecoveryDeadline = DateTimeOffset.MaxValue,
                    RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
                }
            },
            now + TimeSpan.FromMinutes(11));
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void CurrentLaunchNaturalStableStateSuppressesRepeatedOptimizationBeforeThreeLaunchSamples()
    {
        const long mib = 1024L * 1024;
        var now = DateTimeOffset.UtcNow;
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var protection = new ProtectionRules();
        ProcessFamilySnapshot App(long bytes) => new(
            "app", "app", @"C:\Apps", new[]
            {
                new ProcessSnapshot(1, "app", @"C:\Apps\app.exe", null, bytes, 0, 0,
                    false, false, true, 90, StartTimeFileTimeUtc: 123)
            });

        var family = App(200 * mib);
        var readiness = new Dictionary<int, CandidateIdleReadiness>
        {
            [1] = new(1, 2, IsReady: true)
        };
        var natural = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { family }, settings, protection, readiness);
        var componentKey = Assert.Single(Assert.Single(natural).ComponentKeys);
        var tracker = new ApplicationReboundBackoffTracker();
        var stableSettings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, Assert.Single(natural).LaunchSignature, 200 * mib);
        tracker.ObserveNaturalStableStates(natural, now, stableSettings);
        tracker.DrainCompletedStableObservations();
        tracker.ObserveNaturalStableStates(
            natural,
            now + TimeSpan.FromMinutes(3),
            stableSettings);
        tracker.ObserveNaturalStableStates(
            natural,
            now + TimeSpan.FromMinutes(5),
            stableSettings);

        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, tracker.FamilyStableLearningRecords,
            settings, protection, StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now + TimeSpan.FromMinutes(5), tracker.StableCandidateStatuses);
        Assert.Equal(Assert.Single(natural).ComponentKeys, suppressed.OrderBy(value => value).ToArray());
        Assert.Single(tracker.FamilyStableLearningRecords);

        var grown = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { App(350 * mib) }, tracker.FamilyStableLearningRecords,
            settings, protection, StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            now + TimeSpan.FromMinutes(6), tracker.StableCandidateStatuses);
        Assert.Empty(grown);
    }

    [Fact]
    public void ForegroundActivityDoesNotDiscardUnconfirmedRecoveryOrLearnImmediately()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool foreground = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: foreground, IsLowActivity: !foreground);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(70 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib, foreground: true) },
            now + TimeSpan.FromMinutes(1), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableRecoveryEligibleComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void ForegroundGrowthAboveConfirmedLimitReleasesTheCurrentLaunchSuppression()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(long bytes, bool foreground = false) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: foreground, IsLowActivity: !foreground);

        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib, foreground: true) },
            now + TimeSpan.FromMinutes(6), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(400 * mib) }, now + TimeSpan.FromMinutes(7), settings);

        Assert.Single(tracker.FamilyStableLearningRecords.Single().StableWorkingSetSamplesBytes);
        Assert.Equal(ApplicationStableCandidateState.Excluded,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void TurboSeverePressureKeepsUnconfirmedNaturalRecoveryRunning()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);
        var snapshot = new NaturalStableStateSnapshot(
            "app", scopeKey, new[] { componentKey }, "launch-1", 70 * mib,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(new[] { snapshot }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { snapshot }, now + TimeSpan.FromSeconds(3), settings,
            severeMemoryPressure: true);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableRecoveryEligibleComponentKeys());
        Assert.Equal(ApplicationStableCandidateState.Provisional,
            Assert.Single(tracker.StableCandidateStatuses).State);
    }

    [Fact]
    public void TurboSeverePressureCanStartAnUnconfirmedNaturalRecoveryWindow()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 70 * mib);
        var snapshot = new NaturalStableStateSnapshot(
            "app", scopeKey, new[] { componentKey }, "launch-1", 70 * mib,
            IsForeground: false, IsLowActivity: true);

        tracker.ObserveNaturalStableStates(
            new[] { snapshot }, now,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            severeMemoryPressure: true);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.Contains(componentKey, tracker.NaturalStableRecoveryEligibleComponentKeys());
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void TurboSeverePressureDoesNotTerminatePostTrimRecoveryThatPreservesAConvergedStatus()
    {
        const long mib = 1024L * 1024;
        const string componentKey = "app|component:main";
        var scopeKey = ApplicationStableScopeIdentity.For("app", new[] { componentKey });
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        NaturalStableStateSnapshot Snapshot(long bytes) => new(
            "app", scopeKey, new[] { componentKey }, "launch-1", bytes,
            IsForeground: false, IsLowActivity: true);

        PrimeNaturalRecovery(tracker, now, componentKey, "launch-1", 200 * mib);
        tracker.ObserveNaturalStableStates(new[] { Snapshot(200 * mib) }, now, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(2), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(3), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(202 * mib) }, now + TimeSpan.FromMinutes(5), settings);

        var recoveryStartedAt = now + TimeSpan.FromMinutes(6);
        tracker.BeginComponent(
            "app", componentKey, null, 1000 * mib, 50 * mib,
            ReboundBackoffSettings.Default, recoveryStartedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        var postTrim = Snapshot(205 * mib) with
        {
            RecoveryStartedAt = recoveryStartedAt,
            RecoveryDeadline = recoveryStartedAt + settings.MaximumStableValidationDuration,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };
        tracker.ObserveNaturalStableStates(new[] { postTrim }, recoveryStartedAt, settings);
        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());

        tracker.ObserveNaturalStableStates(
            new[] { postTrim }, recoveryStartedAt + TimeSpan.FromSeconds(3), settings,
            severeMemoryPressure: true);

        Assert.Contains(componentKey, tracker.NaturalStableObservationComponentKeys());
        Assert.NotEmpty(tracker.NaturalStableScopeRequests(
            recoveryStartedAt + TimeSpan.FromSeconds(4),
            settings.MaximumStableValidationDuration));
        Assert.Equal(ApplicationStableCandidateState.Converged,
            Assert.Single(tracker.StableCandidateStatuses).State);
        Assert.Single(tracker.FamilyStableLearningRecords);

        var benefitCompletedAt = recoveryStartedAt + ReboundBackoffSettings.Default.LateWindow;
        tracker.Observe(new[] { Family("app", 205 * mib) }, benefitCompletedAt);

        Assert.Contains(componentKey, tracker.NaturalStableRecoveryEligibleComponentKeys());
        Assert.NotEmpty(tracker.NaturalStableScopeRequests(
            benefitCompletedAt,
            settings.MaximumStableValidationDuration));
    }

    [Fact]
    public void PidSnapshotWithoutLaunchSignatureCannotProveSameLaunch()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        for (var launch = 0; launch < 2; launch++)
        {
            var started = now + TimeSpan.FromHours(launch);
            tracker.BeginComponent(
                "app",
                "app|component:main",
                null,
                500,
                100,
                ReboundBackoffSettings.Default,
                started,
                learnOutcome: true,
                targetProcessIds: new[] { 1 });
            tracker.Observe(
                new[] { Family("app", 200) },
                started + ReboundBackoffSettings.Default.LateWindow);
        }

        var record = Assert.Single(tracker.LearningRecords);
        Assert.Equal(2, record.ValidSampleCount);
        Assert.Equal(2, record.DistinctLaunchCount);
    }

    [Fact]
    public void BuiltInStableValidationAndPressureDefaultsMatchProfiles()
    {
        var lite = StableStateSuppressionSettings.For(OptimizationProfile.Lite);
        var turbo = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var ultimate = StableStateSuppressionSettings.For(OptimizationProfile.Ultimate);

        Assert.Equal(TimeSpan.FromMinutes(10), lite.MaximumStableValidationDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), turbo.MaximumStableValidationDuration);
        Assert.Equal(TimeSpan.FromMinutes(3), ultimate.MaximumStableValidationDuration);
        Assert.False(lite.IgnoreRegularObservationUnderSeverePressure);
        Assert.False(turbo.IgnoreRegularObservationUnderSeverePressure);
        Assert.True(ultimate.IgnoreRegularObservationUnderSeverePressure);
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite, 10)]
    [InlineData(OptimizationProfile.Turbo, 5)]
    [InlineData(OptimizationProfile.Ultimate, 3)]
    public void ProvisionalValidationTimeoutReturnsToRollingObservation(
        OptimizationProfile profile,
        int validationMinutes)
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, startedAt) }, startedAt);
        var settings = StableStateSuppressionSettings.For(profile);
        PrimeNaturalRecovery(tracker, startedAt, component, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(bool lowActivity = true) => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: lowActivity)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = DateTimeOffset.MaxValue,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };

        foreach (var minutes in new[] { 0d, 2d, 3d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(minutes), settings);
        Assert.Equal(StableObservationPhase.ProvisionalValidation,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Phase);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(lowActivity: false) },
            startedAt + TimeSpan.FromMinutes(3 + validationMinutes), settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        Assert.Equal(StableObservationPhase.Observing,
            Assert.Single(tracker.NaturalStableObservationStatuses()).Phase);
    }

    [Fact]
    public void ProvisionalStableStateCommitsAfterTwoContinuousValidationMinutes()
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, startedAt) }, startedAt);
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        PrimeNaturalRecovery(tracker, startedAt, component, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(DateTimeOffset observedAt) => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = DateTimeOffset.MaxValue,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim,
            FamilyScopeKey = scope,
            FamilyScopeComponentKeys = new[] { component },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = 200 * mib
        };

        foreach (var offset in new[] { 0d, 2d, 2.5d, 3d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot(startedAt + TimeSpan.FromMinutes(offset)) },
                startedAt + TimeSpan.FromMinutes(offset), settings);

        Assert.Empty(tracker.FamilyStableLearningRecords);
        var validation = Assert.Single(tracker.NaturalStableObservationStatuses());
        Assert.Equal(StableObservationPhase.ProvisionalValidation, validation.Phase);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(startedAt + TimeSpan.FromMinutes(4)) },
            startedAt + TimeSpan.FromMinutes(4), settings);
        Assert.Empty(tracker.FamilyStableLearningRecords);

        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(startedAt + TimeSpan.FromMinutes(5)) },
            startedAt + TimeSpan.FromMinutes(5), settings);
        Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.Empty(tracker.NaturalStableProvisionalValidationComponentKeys());
    }

    [Fact]
    public void ProvisionalValidationResumesWithoutCountingTimeWhileMuseRamWasClosed()
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        var startedAt = DateTimeOffset.UtcNow;
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, startedAt) }, startedAt);
        PrimeNaturalRecovery(tracker, startedAt, component, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = DateTimeOffset.MaxValue,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim,
            FamilyScopeKey = scope,
            FamilyScopeComponentKeys = new[] { component },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = 200 * mib
        };

        foreach (var minutes in new[] { 0d, 2d, 3d, 4d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(minutes), settings);
        var savedAt = startedAt + TimeSpan.FromMinutes(4);
        var progress = tracker.CaptureNaturalStableObservationProgress();

        var restoredAt = savedAt + TimeSpan.FromMinutes(10);
        var restored = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, restoredAt) }, restoredAt);
        restored.RestoreNaturalStableObservationProgress(progress, savedAt, restoredAt);
        restored.ObserveNaturalStableStates(new[] { Snapshot() }, restoredAt, settings);
        Assert.Empty(restored.FamilyStableLearningRecords);

        restored.ObserveNaturalStableStates(
            new[] { Snapshot() }, restoredAt + TimeSpan.FromMinutes(1), settings);
        Assert.Single(restored.FamilyStableLearningRecords);
    }

    [Fact]
    public void OneLowActivityPulseDoesNotResetProvisionalValidation()
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        var startedAt = DateTimeOffset.UtcNow;
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, startedAt) }, startedAt);
        PrimeNaturalRecovery(tracker, startedAt, component, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot(bool lowActivity = true) => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: lowActivity)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = DateTimeOffset.MaxValue,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim,
            FamilyScopeKey = scope,
            FamilyScopeComponentKeys = new[] { component },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = 200 * mib
        };

        foreach (var minutes in new[] { 0d, 2d, 3d, 4d })
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(minutes), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot(lowActivity: false) }, startedAt + TimeSpan.FromMinutes(4.5), settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(5), settings);

        Assert.Single(tracker.FamilyStableLearningRecords);
    }

    [Fact]
    public void BackoffObservationUsesBackoffStartAndContinuesUnderSeverePressure()
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        var rebound = ReboundBackoffSettings.For(OptimizationProfile.Turbo);
        tracker.BeginComponent(
            "app", component, null, 500 * mib, 50 * mib, rebound, startedAt,
            learnOutcome: true, targetProcessIds: new[] { 1 }, launchSignature: "launch-1");
        var reboundAt = startedAt + rebound.EarlyWindow;
        tracker.Observe(new[] { Family("app", 400 * mib) }, reboundAt);

        var request = Assert.Single(tracker.NaturalStableScopeRequests(reboundAt));
        Assert.Equal(NaturalStableObservationOrigin.BackoffRecovery, request.Origin);
        Assert.Equal(reboundAt, request.StartedAt);
        Assert.Equal(reboundAt + rebound.FirstBackoff, request.Deadline);

        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = request.StartedAt,
            RecoveryDeadline = request.Deadline,
            RecoveryOrigin = request.Origin,
            FamilyScopeKey = scope,
            FamilyScopeComponentKeys = new[] { component },
            FamilyScopeLaunchSignature = "launch-1",
            FamilyScopeWorkingSetBytes = 200 * mib
        };
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Ultimate);
        foreach (var offset in new[] { 0d, 2d, 2.5d, 3d, 4d, 5d })
        {
            var observedAt = reboundAt + TimeSpan.FromMinutes(offset);
            tracker.Observe(new[] { Family("app", 200 * mib) }, observedAt);
            tracker.ObserveNaturalStableStates(
                new[] { Snapshot() }, observedAt, settings, severeMemoryPressure: true);
        }

        Assert.Single(tracker.FamilyStableLearningRecords);
        Assert.False(tracker.IsBlocked("app", reboundAt + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SeverePressurePausesRegularObservationWithoutDiscardingIt()
    {
        const long mib = 1024L * 1024;
        const string component = "app|component:main";
        var scope = ApplicationStableScopeIdentity.For("app", new[] { component });
        var startedAt = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker(
            new[] { StableBenefitRecord(component, startedAt) }, startedAt);
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Ultimate);
        PrimeNaturalRecovery(tracker, startedAt, component, "launch-1", 200 * mib);
        NaturalStableStateSnapshot Snapshot() => new(
            "app", scope, new[] { component }, "launch-1", 200 * mib,
            IsForeground: false, IsLowActivity: true)
        {
            RecoveryStartedAt = startedAt,
            RecoveryDeadline = DateTimeOffset.MaxValue,
            RecoveryOrigin = NaturalStableObservationOrigin.PostTrim
        };

        tracker.ObserveNaturalStableStates(new[] { Snapshot() }, startedAt, settings);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(1), settings,
            severeMemoryPressure: true);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(4), settings,
            severeMemoryPressure: true);
        tracker.ObserveNaturalStableStates(
            new[] { Snapshot() }, startedAt + TimeSpan.FromMinutes(5), settings);

        var status = Assert.Single(tracker.NaturalStableObservationStatuses());
        Assert.Equal(StableObservationPhase.Observing, status.Phase);
        Assert.Empty(tracker.FamilyStableLearningRecords);
    }

    private static ApplicationBenefitLearningRecord StableBenefitRecord(
        string componentKey,
        DateTimeOffset observedAt) =>
        new("app", 0.5d, 1, 0, observedAt)
        {
            ComponentKey = componentKey,
            ValidSampleCount = 1,
            DistinctLaunchCount = 1,
            LastLaunchSignature = "launch-1",
            LastLaunchObservationCount = 1,
            LastLaunchContributionWeight = 1d
        };

    private static void RecordStableObservation(
        ApplicationReboundBackoffTracker tracker,
        DateTimeOffset origin,
        int hour,
        string componentKey,
        string launchSignature,
        long lateWorkingSetBytes)
    {
        const long mib = 1024L * 1024;
        var started = origin + TimeSpan.FromHours(hour);
        tracker.BeginComponent(
            "app",
            componentKey,
            null,
            1000 * mib,
            100 * mib,
            ReboundBackoffSettings.Default,
            started,
            learnOutcome: true,
            targetProcessIds: new[] { 1 },
            launchSignature: launchSignature);
        tracker.Observe(
            new[] { Family("app", lateWorkingSetBytes) },
            started + ReboundBackoffSettings.Default.LateWindow);
    }

    private static void PrimeNaturalRecovery(
        ApplicationReboundBackoffTracker tracker,
        DateTimeOffset completedAt,
        string componentKey,
        string launchSignature,
        long lateWorkingSetBytes)
    {
        const long mib = 1024L * 1024;
        var startedAt = completedAt - ReboundBackoffSettings.Default.LateWindow;
        tracker.BeginComponent(
            "app",
            componentKey,
            executablePath: null,
            1000 * mib,
            50 * mib,
            ReboundBackoffSettings.Default,
            startedAt,
            learnOutcome: true,
            targetProcessIds: new[] { 1 },
            launchSignature: launchSignature);
        tracker.Observe(new[] { Family("app", lateWorkingSetBytes) }, completedAt);
        tracker.DrainCompletedOutcomes();
    }

    [Fact]
    public void TwentyFirstLaunchKeepsFivePercentWeightAcrossRepeatedObservations()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        for (var launch = 1; launch <= 20; launch++)
            RecordLearningOutcome(tracker, now, launch, 100, $"launch-{launch}");

        RecordLearningOutcome(tracker, now, 21, 550, "launch-21");
        var first = Assert.Single(tracker.LearningRecords);
        Assert.Equal(21, first.ValidSampleCount);
        Assert.Equal(21, first.DistinctLaunchCount);
        Assert.Equal(0.05d, first.LastLaunchContributionWeight);
        Assert.Equal(0.8775d, first.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(900L, first.AverageReleasedBytes);
        Assert.Equal(878L, first.AverageRetainedBytes);
        Assert.Equal(122L, first.AverageLateWorkingSetBytes);
        Assert.Equal(2.5d, first.AverageReboundPercent, precision: 6);
        Assert.Equal(0d, first.RecentQuickReturnRate, precision: 6);
        Assert.Equal(0d, first.RecentBackoffRate, precision: 6);
        Assert.Equal(21, first.LateWorkingSetSamplesBytes.Count);

        RecordLearningOutcome(tracker, now, 22, 820, "launch-21");
        var second = Assert.Single(tracker.LearningRecords);
        Assert.Equal(21, second.ValidSampleCount);
        Assert.Equal(21, second.DistinctLaunchCount);
        Assert.Equal(0.05d, second.LastLaunchContributionWeight);
        Assert.Equal(0.87075d, second.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(900L, second.AverageReleasedBytes);
        Assert.Equal(871L, second.AverageRetainedBytes);
        Assert.Equal(129L, second.AverageLateWorkingSetBytes);
        Assert.Equal(3.25d, second.AverageReboundPercent, precision: 6);
        Assert.Equal(0d, second.RecentQuickReturnRate, precision: 6);
        Assert.Equal(0.025d, second.RecentBackoffRate, precision: 6);
        Assert.Equal(21, second.LateWorkingSetSamplesBytes.Count);

        RecordLearningOutcome(tracker, now, 23, 100, "launch-21");
        var third = Assert.Single(tracker.LearningRecords);
        Assert.Equal(21, third.ValidSampleCount);
        Assert.Equal(21, third.DistinctLaunchCount);
        Assert.Equal(0.05d, third.LastLaunchContributionWeight);
        Assert.Equal(0.8805d, third.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(900L, third.AverageReleasedBytes);
        Assert.Equal(881L, third.AverageRetainedBytes);
        Assert.Equal(119L, third.AverageLateWorkingSetBytes);
        Assert.Equal(2.16666666666667d, third.AverageReboundPercent, precision: 6);
        Assert.Equal(0d, third.RecentQuickReturnRate, precision: 6);
        Assert.Equal(0.0166666666666667d, third.RecentBackoffRate, precision: 6);
        Assert.Equal(21, third.LateWorkingSetSamplesBytes.Count);
    }

    [Fact]
    public void HundredValidSamplesStillUseFivePercentForSameLaunchReplacement()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;

        for (var launch = 1; launch <= 100; launch++)
            RecordLearningOutcome(tracker, now, launch, 100, $"launch-{launch}");

        RecordLearningOutcome(tracker, now, 101, 550, "launch-101");
        var first = Assert.Single(tracker.LearningRecords);
        Assert.Equal(100, first.ValidSampleCount);
        Assert.Equal(100, first.DistinctLaunchCount);
        Assert.Equal(0.05d, first.LastLaunchContributionWeight);
        Assert.Equal(0.8775d, first.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(900L, first.AverageReleasedBytes);
        Assert.Equal(878L, first.AverageRetainedBytes);
        Assert.Equal(122L, first.AverageLateWorkingSetBytes);
        Assert.Equal(100, first.LateWorkingSetSamplesBytes.Count);

        RecordLearningOutcome(tracker, now, 102, 820, "launch-101");
        var second = Assert.Single(tracker.LearningRecords);
        Assert.Equal(100, second.ValidSampleCount);
        Assert.Equal(100, second.DistinctLaunchCount);
        Assert.Equal(0.05d, second.LastLaunchContributionWeight);
        Assert.Equal(0.87075d, second.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(900L, second.AverageReleasedBytes);
        Assert.Equal(871L, second.AverageRetainedBytes);
        Assert.Equal(129L, second.AverageLateWorkingSetBytes);
        Assert.Equal(100, second.LateWorkingSetSamplesBytes.Count);
        var suggestion = Assert.Single(ApplicationOptimizationThresholdSuggestionPolicy.Create(new[] { second }));
        Assert.Equal(100L, suggestion.LateWorkingSetP75Bytes);
    }

    [Fact]
    public void LegacyRecordKeepsMissingWeightZeroUntilAProvenSameLaunchRestoresIt()
    {
        var now = DateTimeOffset.UtcNow;
        var legacy = new ApplicationBenefitLearningRecord("app", 0.8, 100, 0, now)
        {
            ComponentKey = "app|component:main",
            LastLaunchSignature = "legacy-launch",
            LastLaunchObservationCount = 1,
            ValidSampleCount = 100
        };
        var tracker = new ApplicationReboundBackoffTracker(new[] { legacy }, now);

        Assert.Equal(0d, Assert.Single(tracker.LearningRecords).LastLaunchContributionWeight);
        RecordLearningOutcome(tracker, now, 1, 550, "new-launch");
        var newLaunch = Assert.Single(tracker.LearningRecords);
        Assert.Equal(0.05d, newLaunch.LastLaunchContributionWeight);
        Assert.Equal(0.7825d, newLaunch.AverageOutcomeMultiplier, precision: 6);

        RecordLearningOutcome(tracker, now, 2, 820, "new-launch");
        var sameLaunch = Assert.Single(tracker.LearningRecords);
        Assert.Equal(0.05d, sameLaunch.LastLaunchContributionWeight);
        Assert.Equal(0.77575d, sameLaunch.AverageOutcomeMultiplier, precision: 6);
    }

    [Fact]
    public void LegacyRecordWithCompleteLaunchSignatureRestoresItsExpectedWeight()
    {
        var now = DateTimeOffset.UtcNow;
        var legacy = new ApplicationBenefitLearningRecord("app", 0.8, 100, 0, now)
        {
            ComponentKey = "app|component:main",
            LastLaunchSignature = "legacy-launch",
            LastLaunchObservationCount = 1,
            DistinctLaunchCount = 1,
            ValidSampleCount = 100,
            LastLaunchAverageOutcomeMultiplier = 0.8d
        };
        var tracker = new ApplicationReboundBackoffTracker(new[] { legacy }, now);

        RecordLearningOutcome(tracker, now, 1, 550, "legacy-launch");

        var restored = Assert.Single(tracker.LearningRecords);
        Assert.Equal(0.05d, restored.LastLaunchContributionWeight);
        Assert.Equal(0.79125d, restored.AverageOutcomeMultiplier, precision: 6);
        Assert.Equal(100, restored.ValidSampleCount);
        Assert.Equal(1, restored.DistinctLaunchCount);
    }

    [Fact]
    public void QuickForegroundReturnsSoftlyLowerLearnedPriority()
    {
        var now = DateTimeOffset.UtcNow;
        var stable = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("app", 0.8, 6, 0, now)
            {
                QuickReturnCount = 6,
                ValidSampleCount = 6,
                RecentQuickReturnRate = 0
            }
        }, now);
        var frequentlyReused = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("app", 0.8, 6, 6, now)
            {
                ValidSampleCount = 6,
                RecentQuickReturnRate = 1
            }
        }, now);

        Assert.Equal(0.8, stable.OutcomeMultipliers["app"], precision: 3);
        Assert.Equal(0.48, frequentlyReused.OutcomeMultipliers["app"], precision: 3);
    }

    [Fact]
    public void BackgroundToForegroundTransitionIsLearnedAsAQuickReturn()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Begin(
            "app",
            500,
            100,
            ReboundBackoffSettings.Default,
            now,
            learnOutcome: true,
            wasForegroundBeforeTrim: false);

        tracker.Observe(new[] { Family("app", 150, foreground: true) }, now + TimeSpan.FromSeconds(3));
        tracker.Observe(new[] { Family("app", 200) }, now + TimeSpan.FromSeconds(120));

        var record = Assert.Single(tracker.LearningRecords);
        Assert.Equal(1, record.QuickReturnCount);
        Assert.Equal(1d, record.RecentQuickReturnRate);
        var outcome = Assert.Single(tracker.DrainCompletedOutcomes());
        Assert.Equal(TimeSpan.FromSeconds(3), outcome.TimeToForeground);
    }

    [Fact]
    public void ForegroundSiblingDoesNotInvalidateTargetComponentBenefitSample()
    {
        const string helperPath = @"C:\Apps\Suite\helper.exe";
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginComponent(
            "suite",
            "suite|component:helper",
            helperPath,
            200,
            100,
            ReboundBackoffSettings.Default,
            now,
            learnOutcome: true,
            wasForegroundBeforeTrim: false,
            targetProcessIds: new[] { 2 },
            baselineFamilyProcessIds: new[] { 1, 2 },
            launchSignature: "helper-launch");
        ProcessFamilySnapshot Family(bool mainForeground) => new("suite", "Suite", null, new[]
        {
            new ProcessSnapshot(1, "main", @"C:\Apps\Suite\main.exe", null, 1000, 0, 0,
                mainForeground, mainForeground, true, 90),
            new ProcessSnapshot(2, "helper", helperPath, null, 100, 0, 0, false, false, true, 90)
        });

        tracker.Observe(new[] { Family(mainForeground: true) }, now + TimeSpan.FromSeconds(3));
        tracker.Observe(new[] { Family(mainForeground: false) }, now + ReboundBackoffSettings.Default.LateWindow);

        var record = Assert.Single(tracker.LearningRecords);
        Assert.Equal(1, record.ValidSampleCount);
        Assert.Equal(0, record.QuickReturnCount);
        Assert.Null(Assert.Single(tracker.DrainCompletedOutcomes()).TimeToForeground);
    }

    [Fact]
    public void ScopedBackoffStatusIgnoresHistoricalProtectedComponent()
    {
        const string familyKey = "suite";
        const string protectedComponent = "suite|component:protected";
        const string unprotectedComponent = "suite|component:helper";
        var now = DateTimeOffset.UtcNow;
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(familyKey, 1, 60, null, false, false)
            {
                TargetKey = protectedComponent
            }
        }, now);

        Assert.NotNull(tracker.GetBackoffStatus(familyKey, now));
        Assert.Null(tracker.GetBackoffStatus(familyKey, new[] { unprotectedComponent }, now));
        Assert.NotNull(tracker.GetBackoffStatus(familyKey, new[] { protectedComponent }, now));
    }

    [Fact]
    public void LearningDecaysAfterThirtyDaysAndExpiresAfterNinety()
    {
        var now = DateTimeOffset.UtcNow;
        var decaying = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("app", 0.4, 6, 0, now - TimeSpan.FromDays(60))
            {
                ValidSampleCount = 6
            }
        }, now);
        var expired = new ApplicationReboundBackoffTracker(new[]
        {
            new ApplicationBenefitLearningRecord("app", 0.4, 6, 0, now - TimeSpan.FromDays(90))
            {
                ValidSampleCount = 6
            }
        }, now);

        Assert.Equal(0.7, decaying.OutcomeMultipliers["app"], precision: 3);
        Assert.Empty(expired.LearningRecords);
        Assert.Empty(expired.OutcomeMultipliers);
    }

    [Fact]
    public void DisablingLearningBeforeObservationDoesNotStoreThePendingOutcome()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, now, learnOutcome: true);

        tracker.Observe(
            new[] { Family("app", 200) },
            now + TimeSpan.FromSeconds(120),
            benefitLearningEnabled: false);

        Assert.Empty(tracker.LearningRecords);
    }

    [Fact]
    public void ConfirmedBackoffProgressCanBeCapturedAndRestored()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var source = new ApplicationReboundBackoffTracker();
        source.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress("edge", 2, 90, null, false, false)
        }, capturedAt);

        var snapshot = source.CaptureProgress(capturedAt + TimeSpan.FromSeconds(30));
        var saved = Assert.Single(snapshot);
        Assert.Equal(60, saved.RemainingBlockSeconds);
        Assert.Null(saved.LongTermObservedSeconds);

        var restoredAt = capturedAt + TimeSpan.FromMinutes(10);
        var restored = new ApplicationReboundBackoffTracker();
        restored.RestoreProgress(snapshot, restoredAt);

        var status = Assert.IsType<ApplicationBackoffStatus>(restored.GetBackoffStatus("edge", restoredAt));
        Assert.Equal(2, status.ReboundCount);
        Assert.True(restored.IsBlocked("edge", restoredAt + TimeSpan.FromSeconds(59)));
        Assert.False(restored.IsBlocked("edge", restoredAt + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void BuiltInProfilesUseDistinctReboundResponses()
    {
        var lite = ReboundBackoffSettings.For(OptimizationProfile.Lite);
        var turbo = ReboundBackoffSettings.For(OptimizationProfile.Turbo);
        var ultimate = ReboundBackoffSettings.For(OptimizationProfile.Ultimate);

        Assert.Equal((50d, 70d, 30d, 60d), Values(lite));
        Assert.Equal((60d, 80d, 5d, 10d), Values(turbo));
        Assert.Equal((75d, 90d, 2d, 5d), Values(ultimate));
        Assert.False(lite.CycleAfterSecondBackoff);
        Assert.False(turbo.CycleAfterSecondBackoff);
        Assert.True(ultimate.CycleAfterSecondBackoff);
        Assert.True(ultimate.AllowSecondBackoffForegroundIdleRetry);
    }

    [Fact]
    public void UltimateBackoffCyclesFirstAndSecondStagesWithoutLongTermObservation()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Ultimate);
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 4; index++)
        {
            var started = now + TimeSpan.FromMinutes(index * 10);
            tracker.Begin("app", 500, 100, settings, started);
            tracker.Observe(new[] { Family("app", 500) }, started + settings.EarlyWindow);
            var progress = Assert.Single(tracker.CaptureProgress(started + settings.EarlyWindow));
            Assert.Equal(index + 1, progress.ReboundCount);
            Assert.Equal(index % 2 == 0 ? 1 : 2, progress.BackoffStage);
            Assert.Null(progress.LongTermObservedSeconds);
        }
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite)]
    [InlineData(OptimizationProfile.Turbo)]
    public void ConservativeProfilesStillEnterLongTermObservationAfterThirdRebound(OptimizationProfile profile)
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = ReboundBackoffSettings.For(profile);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
        {
            var started = now + TimeSpan.FromHours(index * 8);
            tracker.Begin("app", 500, 100, settings, started);
            tracker.Observe(new[] { Family("app", 500) }, started + settings.EarlyWindow);
        }

        Assert.True(Assert.IsType<ApplicationBackoffStatus>(
            tracker.GetBackoffStatus("app", now + TimeSpan.FromHours(16))).LongTermObservation);
    }

    [Fact]
    public void DisabledReboundProtectionKeepsOutcomeObservationWithoutRegisteringBackoff()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Turbo) with { Enabled = false };
        var now = DateTimeOffset.UtcNow;
        tracker.Begin("app", 500, 100, settings, now, learnOutcome: true);

        tracker.Observe(new[] { Family("app", 500) }, now + settings.LateWindow);

        Assert.Null(tracker.GetBackoffStatus("app", now + settings.LateWindow));
        Assert.False(Assert.Single(tracker.DrainCompletedOutcomes()).BackoffTriggered);
    }

    [Fact]
    public void UltimateSecondBackoffCanEndAfterForegroundThenSustainedBackgroundIdle()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var settings = ReboundBackoffSettings.For(OptimizationProfile.Ultimate);
        var stable = StableStateSuppressionSettings.For(OptimizationProfile.Ultimate);
        var now = DateTimeOffset.UtcNow;

        tracker.Begin("app", 500, 100, settings, now);
        tracker.Observe(new[] { Family("app", 500) }, now + settings.EarlyWindow);
        var secondStartedAt = now + TimeSpan.FromMinutes(3);
        tracker.Begin("app", 500, 100, settings, secondStartedAt);
        tracker.Observe(new[] { Family("app", 500) }, secondStartedAt + settings.EarlyWindow);
        var foregroundAt = secondStartedAt + TimeSpan.FromMinutes(1);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500, foreground: true) }, false, 1, stable, foregroundAt);
        Assert.True(tracker.IsBlocked("app", foregroundAt));

        var idleStartedAt = foregroundAt + TimeSpan.FromSeconds(1);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) }, false, 1, stable, idleStartedAt);
        tracker.UpdateLongTermRetryPermissions(
            new[] { Family("app", 500) }, false, 1, stable,
            idleStartedAt + BackgroundActivityTracker.MinimumObservation);

        Assert.False(tracker.IsBlocked(
            "app", idleStartedAt + BackgroundActivityTracker.MinimumObservation));
    }

    private static (double Early, double Late, double FirstMinutes, double SecondMinutes) Values(
        ReboundBackoffSettings settings) =>
        (settings.EarlyReboundPercent, settings.LateReboundPercent,
            settings.FirstBackoff.TotalMinutes, settings.SecondBackoff.TotalMinutes);

    private static ProcessFamilySnapshot Family(string key, long workingSet, bool foreground = false) => new(
        key,
        key,
        null,
        new[]
        {
            new ProcessSnapshot(1, key, null, null, workingSet, 0, 0, foreground, false, true, 90)
        });

    private static DateTimeOffset EnterLongTermObservation(
        ApplicationReboundBackoffTracker tracker,
        DateTimeOffset now)
    {
        DateTimeOffset detectedAt = default;
        for (var pass = 0; pass < 3; pass++)
        {
            var started = now + TimeSpan.FromHours(pass * 2);
            detectedAt = started + TimeSpan.FromSeconds(12);
            tracker.Begin("app", 500, 100, ReboundBackoffSettings.Default, started);
            tracker.Observe(new[] { Family("app", 500) }, detectedAt);
            if (pass == 2)
            {
                tracker.Observe(
                    new[] { Family("app", 500) },
                    started + ReboundBackoffSettings.Default.LateWindow);
            }
        }
        return detectedAt;
    }

    private static IReadOnlyDictionary<string, BackgroundActivity> Activity(
        string key,
        BackgroundActivityState state,
        TimeSpan idleFor) => new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = new BackgroundActivity(
                key,
                state,
                ObservedFor: TimeSpan.FromMinutes(2),
                IdleFor: idleFor,
                SampleCount: 5)
        };

    private static void RecordLearningOutcome(
        ApplicationReboundBackoffTracker tracker,
        DateTimeOffset now,
        int sequence,
        long currentWorkingSet,
        string launchSignature)
    {
        var started = now + TimeSpan.FromHours(sequence);
        tracker.BeginComponent(
            "app",
            "app|component:main",
            null,
            1_000,
            100,
            ReboundBackoffSettings.Default,
            started,
            learnOutcome: true,
            targetProcessIds: new[] { 1 },
            launchSignature: launchSignature);
        tracker.Observe(
            new[] { Family("app", currentWorkingSet) },
            started + ReboundBackoffSettings.Default.LateWindow);
    }
}

public sealed class ApplicationReboundDetailTrackerTests
{
    [Fact]
    public void TracksRegainedWorkingSetForEachTrimmedApplicationFamily()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track("app", "Editor", workingSetAfter: 100, releasedBytes: 400);

        tracker.Observe(new[] { Family("app", 300) }, now + TimeSpan.FromSeconds(30));

        var detail = Assert.Single(tracker.Details);
        Assert.Equal(400, detail.ReleasedBytes);
        Assert.Equal(200, detail.RegainedBytes);
        Assert.Equal(50, detail.ReboundPercent, precision: 3);
        Assert.False(detail.IsComplete);
    }

    [Fact]
    public void ReboundDetailCapsAtTheAmountOriginallyReleased()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track("app", "Editor", workingSetAfter: 100, releasedBytes: 400);

        tracker.Observe(new[] { Family("app", 700) }, now + TimeSpan.FromSeconds(120));

        var detail = Assert.Single(tracker.Details);
        Assert.Equal(400, detail.RegainedBytes);
        Assert.Equal(100, detail.ReboundPercent, precision: 3);
        Assert.True(detail.IsComplete);
        Assert.False(tracker.IsTracking(now + TimeSpan.FromSeconds(120)));
        Assert.Equal(now, tracker.StartedAt);
        Assert.Equal(now + TimeSpan.FromSeconds(120), tracker.ExpectedCompletionAt);
        Assert.Equal(now + TimeSpan.FromSeconds(120), tracker.CompletedAt);
    }

    [Fact]
    public void StartingANewRunReplacesThePreviousRunDetails()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track("old", "Old", 100, 100);

        tracker.BeginRun(now + TimeSpan.FromMinutes(3));
        tracker.Track("new", "New", 200, 300);

        Assert.Equal("new", Assert.Single(tracker.Details).FamilyKey);
        Assert.Null(tracker.CompletedAt);
    }

    [Fact]
    public void ReboundObservationIgnoresUntrimmedSiblingWorkingSetChanges()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track("suite", "Suite", workingSetAfter: 100, releasedBytes: 400, targetProcessIds: new[] { 1 });
        var family = new ProcessFamilySnapshot(
            "suite",
            "Suite",
            null,
            new[]
            {
                new ProcessSnapshot(1, "helper", null, null, 150, 0, 0, false, false, true, 90),
                new ProcessSnapshot(2, "main", null, null, 1000, 0, 0, true, true, true, 10)
            });

        tracker.Observe(new[] { family }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(50, Assert.Single(tracker.Details).RegainedBytes);
    }

    [Fact]
    public void ReboundObservationIncludesNewReplacementProcess()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track(
            "suite",
            "Suite",
            workingSetAfter: 100,
            releasedBytes: 400,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1, 2 });
        var family = new ProcessFamilySnapshot(
            "suite",
            "Suite",
            null,
            new[]
            {
                new ProcessSnapshot(2, "main", null, null, 1000, 0, 0, true, true, true, 10),
                new ProcessSnapshot(3, "replacement", null, null, 150, 0, 0, false, false, true, 90)
            });

        tracker.Observe(new[] { family }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(50, Assert.Single(tracker.Details).RegainedBytes);
    }

    [Fact]
    public void ReboundObservationExcludesNewProcessFromDifferentExecutableComponent()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track(
            "suite",
            "Suite",
            workingSetAfter: 100,
            releasedBytes: 400,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1, 2 },
            targetExecutablePaths: new[] { @"C:\Apps\Suite\helper.exe" });

        tracker.Observe(new[]
        {
            new ProcessFamilySnapshot("suite", "Suite", null, new[]
            {
                new ProcessSnapshot(2, "main", @"C:\Apps\Suite\main.exe", null, 1000, 0, 0, true, true, true, 10),
                new ProcessSnapshot(3, "protected", @"C:\Apps\Suite\protected.exe", null, 150, 0, 0, false, false, true, 90)
            })
        }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(0, Assert.Single(tracker.Details).RegainedBytes);
    }

    [Fact]
    public void ReboundObservationIncludesSameExecutableReplacementWhenPathsAreTracked()
    {
        var tracker = new ApplicationReboundDetailTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.BeginRun(now);
        tracker.Track(
            "suite",
            "Suite",
            workingSetAfter: 100,
            releasedBytes: 400,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1, 2 },
            targetExecutablePaths: new[] { @"C:\Apps\Suite\helper.exe" });

        tracker.Observe(new[]
        {
            new ProcessFamilySnapshot("suite", "Suite", null, new[]
            {
                new ProcessSnapshot(2, "main", @"C:\Apps\Suite\main.exe", null, 1000, 0, 0, true, true, true, 10),
                new ProcessSnapshot(3, "replacement", @"C:\Apps\Suite\helper.exe", null, 150, 0, 0, false, false, true, 90)
            })
        }, now + TimeSpan.FromSeconds(30));

        Assert.Equal(50, Assert.Single(tracker.Details).RegainedBytes);
    }

    private static ProcessFamilySnapshot Family(string key, long workingSet) => new(
        key,
        key,
        null,
        new[] { new ProcessSnapshot(1, key, null, null, workingSet, 0, 0, false, false, true, 90) });

}
