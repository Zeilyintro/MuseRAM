using MuseRAM.Core;

namespace MuseRAM.Core.Tests;

public sealed class ComponentBackoffTests
{
    private const string FamilyKey = "suite";
    private const string MainPath = @"C:\Apps\Suite\main.exe";
    private const string ServicePath = @"C:\Apps\Suite\service.exe";

    [Fact]
    public void WindowsPackageComponentIdentityIgnoresPackageVersion()
    {
        const string familyKey = "package:openai.codex_2p2nqsd0c76g0";
        const string oldPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.40816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        const string newPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.4816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";

        Assert.Equal(
            ApplicationComponentIdentity.ForExecutable(familyKey, oldPath),
            ApplicationComponentIdentity.ForExecutable(familyKey, newPath));
    }

    [Fact]
    public void RapidReboundBlocksOnlyTheMatchingExecutableComponent()
    {
        var tracker = new ApplicationReboundBackoffTracker();
        var now = DateTimeOffset.UtcNow;
        var mainKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, MainPath);
        var serviceKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, ServicePath);
        tracker.BeginComponent(
            FamilyKey,
            mainKey,
            MainPath,
            500,
            100,
            ReboundBackoffSettings.Default,
            now,
            targetProcessIds: new[] { 1 },
            baselineFamilyProcessIds: new[] { 1, 2 });

        tracker.Observe(new[] { Family(
            Process(1, "main", MainPath, 350),
            Process(2, "service", ServicePath, 900)) }, now + TimeSpan.FromSeconds(15));

        Assert.Contains(mainKey, tracker.BlockedComponentKeys(now + TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain(serviceKey, tracker.BlockedComponentKeys(now + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void ReplacementPidCountsOnlyWhenItsExecutablePathMatchesTheComponent()
    {
        var now = DateTimeOffset.UtcNow;
        var mainKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, MainPath);
        var matching = new ApplicationReboundBackoffTracker();
        matching.BeginComponent(
            FamilyKey, mainKey, MainPath, 500, 100, ReboundBackoffSettings.Default, now,
            targetProcessIds: new[] { 1 }, baselineFamilyProcessIds: new[] { 1, 2 });
        matching.Observe(new[] { Family(
            Process(2, "service", ServicePath, 900),
            Process(3, "main", MainPath, 350)) }, now + TimeSpan.FromSeconds(15));

        var different = new ApplicationReboundBackoffTracker();
        different.BeginComponent(
            FamilyKey, mainKey, MainPath, 500, 100, ReboundBackoffSettings.Default, now,
            targetProcessIds: new[] { 1 }, baselineFamilyProcessIds: new[] { 1, 2 });
        different.Observe(new[] { Family(
            Process(2, "service", ServicePath, 900),
            Process(3, "other", @"C:\Apps\Suite\other.exe", 350)) }, now + TimeSpan.FromSeconds(15));

        Assert.Contains(mainKey, matching.BlockedComponentKeys(now + TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain(mainKey, different.BlockedComponentKeys(now + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void PlannerBlocksTheCurrentUnprotectedScopeWhenOneComponentIsBackingOff()
    {
        var main = Process(1, "main", MainPath, 300L * 1024 * 1024);
        var service = Process(2, "service", ServicePath, 80L * 1024 * 1024);
        var family = Family(main, service);
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            IgnoreMemoryPressureThreshold = true,
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0,
            MinimumIdleScore = 0,
            ProcessCooldown = TimeSpan.Zero
        };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024, 50),
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            automaticBackoffComponents: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ApplicationComponentIdentity.ForExecutable(FamilyKey, MainPath)
            });

        Assert.Empty(plan.Candidates);
        var evaluation = Assert.Single(plan.CandidateEvaluations);
        Assert.Contains(CandidateExclusionReason.AutomaticBackoff, evaluation.ExclusionReasons);
    }

    [Fact]
    public void RuntimeProgressKeepsComponentTargetAndAcceptsLegacyFamilyTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var componentKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, MainPath);
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(FamilyKey, 1, 60, null, false, false)
            {
                TargetKey = componentKey
            },
            new ApplicationBackoffProgress("legacy", 1, 60, null, false, false)
        }, now);

        var captured = tracker.CaptureProgress(now);

        Assert.Contains(captured, item => item.FamilyKey == FamilyKey && item.TargetKey == componentKey);
        Assert.Contains(captured, item => item.FamilyKey == "legacy" && item.TargetKey == "legacy");
    }

    [Fact]
    public void MultipleBackoffComponentsShareOneFamilyRecoveryScope()
    {
        var now = DateTimeOffset.UtcNow;
        var mainKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, MainPath);
        var serviceKey = ApplicationComponentIdentity.ForExecutable(FamilyKey, ServicePath);
        var tracker = new ApplicationReboundBackoffTracker();
        tracker.RestoreProgress(new[]
        {
            new ApplicationBackoffProgress(FamilyKey, 1, 300, null, false, false)
            {
                TargetKey = mainKey
            },
            new ApplicationBackoffProgress(FamilyKey, 2, 600, null, false, false)
            {
                TargetKey = serviceKey
            }
        }, now);

        var request = Assert.Single(tracker.NaturalStableScopeRequests(now));

        Assert.Equal(new[] { mainKey, serviceKey }.Order(), request.ComponentKeys.Order());
    }

    private static ProcessFamilySnapshot Family(params ProcessSnapshot[] processes) =>
        new(FamilyKey, "Suite", @"C:\Apps\Suite", processes);

    private static ProcessSnapshot Process(int pid, string name, string path, long workingSet) =>
        new(pid, name, path, null, workingSet, 0, 0, false, false, true, 90);
}

public sealed class StableStateSuppressionPolicyTests
{
    [Fact]
    public void RecoveryScopeExcludesUnrelatedCurrentFamilyComponents()
    {
        const long mib = 1024L * 1024;
        const string familyKey = "suite";
        const string mainPath = @"C:\Apps\Suite\main.exe";
        const string servicePath = @"C:\Apps\Suite\service.exe";
        var main = new ProcessSnapshot(
            1, "main", mainPath, null, 40 * mib, 0, 0,
            false, false, true, 90, StartTimeFileTimeUtc: 100);
        var service = new ProcessSnapshot(
            2, "service", servicePath, null, 40 * mib, 0, 0,
            true, false, true, 90, StartTimeFileTimeUtc: 200);
        var family = new ProcessFamilySnapshot(
            familyKey, "Suite", @"C:\Apps\Suite", new[] { main, service });
        var mainKey = ApplicationComponentIdentity.ForExecutable(familyKey, mainPath);
        var serviceKey = ApplicationComponentIdentity.ForExecutable(familyKey, servicePath);
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0
        };

        var snapshots = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, CandidateIdleReadiness>
            {
                [main.ProcessId] = new(1, 2, true),
                [service.ProcessId] = new(1, 2, true)
            },
            recoveryScopes: new[]
            {
                new NaturalStableScopeRequest(familyKey, new[] { mainKey }, DateTimeOffset.UtcNow)
            });
        var snapshot = Assert.Single(snapshots);

        Assert.Equal(new[] { mainKey }, snapshot.ComponentKeys);
        Assert.False(snapshot.IsForeground);
        Assert.Equal(new[] { mainKey, serviceKey }.Order(), snapshot.FamilyScopeComponentKeys.Order());
        Assert.Equal(80 * mib, snapshot.FamilyScopeWorkingSetBytes);
        Assert.True(snapshot.FamilyScopeIsForeground);
    }

    [Fact]
    public void ActiveRecoveryScopeSuppressesOverlappingCanonicalObservation()
    {
        const long mib = 1024L * 1024;
        const string familyKey = "suite";
        const string mainPath = @"C:\Apps\Suite\main.exe";
        const string helperPath = @"C:\Apps\Suite\helper.exe";
        var main = new ProcessSnapshot(
            1, "main", mainPath, null, 100 * mib, 0, 0,
            false, false, true, 90, StartTimeFileTimeUtc: 100);
        var helper = new ProcessSnapshot(
            2, "helper", helperPath, null, 100 * mib, 0, 0,
            false, false, true, 90, StartTimeFileTimeUtc: 200);
        var family = new ProcessFamilySnapshot(
            familyKey, "Suite", @"C:\Apps\Suite", new[] { main, helper });
        var mainKey = ApplicationComponentIdentity.ForExecutable(familyKey, mainPath);
        var helperKey = ApplicationComponentIdentity.ForExecutable(familyKey, helperPath);
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0
        };
        var readiness = new Dictionary<int, CandidateIdleReadiness>
        {
            [main.ProcessId] = new(1, 2, true),
            [helper.ProcessId] = new(1, 2, true)
        };
        var initial = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { family }, settings, new ProtectionRules(), readiness));
        var recovery = new NaturalStableScopeRequest(
            familyKey, new[] { mainKey }, DateTimeOffset.UtcNow.AddMinutes(-1));

        var partiallyProtected = Assert.Single(
            StableStateSuppressionPolicy.NaturalStableStateSnapshots(
                new[] { family }, settings,
                new ProtectionRules(new[] { helperPath }, protectRelatedProcesses: false),
                readiness, recoveryScopes: new[] { recovery }));

        Assert.Equal(new[] { mainKey }, partiallyProtected.ComponentKeys);

        var restored = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { family }, settings, new ProtectionRules(), readiness,
            recoveryScopes: new[] { recovery });

        var recoverySnapshot = Assert.Single(restored);
        Assert.Equal(new[] { mainKey }, recoverySnapshot.ComponentKeys);
        Assert.Equal(recovery.StartedAt, recoverySnapshot.RecoveryStartedAt);
        Assert.Equal(new[] { helperKey, mainKey }.Order(),
            recoverySnapshot.FamilyScopeComponentKeys.Order());
        Assert.Equal(initial.ScopeKey, recoverySnapshot.FamilyScopeKey);
        Assert.Equal(initial.LaunchSignature, recoverySnapshot.FamilyScopeLaunchSignature);
    }

    [Fact]
    public void NaturalStableObservationAcceptsAOneTimeStepAndUsesTheLatestPeriodMedian()
    {
        const long mib = 1024L * 1024;
        var samples = new long[]
        {
            100 * mib, 101 * mib, 99 * mib,
            140 * mib, 141 * mib, 139 * mib,
            140 * mib, 142 * mib, 141 * mib
        };

        var stableBytes = NaturalStableObservationPolicy.StableSampleBytes(samples);

        Assert.Equal(141 * mib, stableBytes);
    }

    [Fact]
    public void NaturalStableObservationRejectsGrowthThatContinuesAcrossAllPeriods()
    {
        const long mib = 1024L * 1024;
        var samples = new long[]
        {
            100 * mib, 101 * mib, 102 * mib,
            120 * mib, 121 * mib, 122 * mib,
            140 * mib, 141 * mib, 142 * mib
        };

        Assert.Null(NaturalStableObservationPolicy.StableSampleBytes(samples));
    }

    [Fact]
    public void NaturalStableLaunchSignatureIgnoresLaterPidsWithinTheSameComponent()
    {
        const string path = @"C:\Apps\Suite\main.exe";
        const long mib = 1024L * 1024;
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumProcessWorkingSetBytes = 0,
            MinimumFamilyWorkingSetBytes = 0
        };
        ProcessSnapshot Process(int pid, long startedAt) => new(
            pid, "main", path, null, 200 * mib, 0, 0,
            false, false, true, 90, StartTimeFileTimeUtc: startedAt);
        ProcessFamilySnapshot Family(params ProcessSnapshot[] processes) => new(
            "suite", "Suite", @"C:\Apps\Suite", processes);
        IReadOnlyDictionary<int, CandidateIdleReadiness> Readiness(params int[] processIds) =>
            processIds.ToDictionary(id => id, id => new CandidateIdleReadiness(id, 2, true));

        var original = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { Family(Process(1, 100), Process(2, 200)) },
            settings, new ProtectionRules(), Readiness(1, 2)));
        var childAdded = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { Family(Process(1, 100), Process(2, 200), Process(3, 300)) },
            settings, new ProtectionRules(), Readiness(1, 2, 3)));
        var anchorExited = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { Family(Process(2, 200), Process(3, 300)) },
            settings, new ProtectionRules(), Readiness(2, 3)));

        Assert.Equal(original.LaunchSignature, childAdded.LaunchSignature);
        Assert.NotEqual(original.LaunchSignature, anchorExited.LaunchSignature);
    }

    [Fact]
    public void NaturalStableScopeToleratesASmallTransientChildButNotALargeActiveProcess()
    {
        const string path = @"C:\Apps\Browser\browser.exe";
        const long mib = 1024L * 1024;
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumProcessWorkingSetBytes = 0,
            MinimumFamilyWorkingSetBytes = 0
        };
        ProcessSnapshot Process(int pid, long bytes) => new(
            pid, "browser", path, null, bytes, 0, 0,
            false, false, true, 90, StartTimeFileTimeUtc: pid);
        ProcessFamilySnapshot Family(params ProcessSnapshot[] processes) => new(
            "browser", "Browser", @"C:\Apps\Browser", processes);
        var readiness = new Dictionary<int, CandidateIdleReadiness>
        {
            [1] = new(1, 2, true),
            [2] = new(2, 0, false)
        };

        var smallChild = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { Family(Process(1, 220 * mib), Process(2, 12 * mib)) },
            settings, new ProtectionRules(), readiness));
        var largeChild = Assert.Single(StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { Family(Process(1, 220 * mib), Process(2, 40 * mib)) },
            settings, new ProtectionRules(), readiness));

        Assert.True(smallChild.IsLowActivity);
        Assert.False(largeChild.IsLowActivity);
    }

    [Fact]
    public void StableHistoryKeepsOnlyNineMostRecentConfirmationsWhileMedianSortsACopy()
    {
        var history = StableWorkingSetLearningPolicy.NormalizeSampleHistory(
            Enumerable.Range(1, 12).Select(value => (long)value));

        Assert.Equal(Enumerable.Range(4, 9).Select(value => (long)value), history);
        Assert.Equal(8, StableWorkingSetLearningPolicy.Median(
            StableWorkingSetLearningPolicy.NormalizeSamples(history)));
    }

    [Fact]
    public void CustomStableSamplePoolCanRetainAndEvaluateMoreThanNineSamples()
    {
        var history = StableWorkingSetLearningPolicy.NormalizeSampleHistory(
            Enumerable.Range(1, 120).Select(value => (long)value),
            100);
        var record = new ApplicationStableLearningRecord(
            "suite",
            history,
            DateTimeOffset.UtcNow,
            "launch-1");

        Assert.Equal(100, history.Count);
        Assert.Equal(Enumerable.Range(21, 100).Select(value => (long)value), history);
        Assert.Equal(110, StableStateSuppressionPolicy.StableReferenceBytes(record, 20));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ApplicationBenefitLearningRecord Record = new(
        "suite", 0.5, 3, 0, Now)
    {
        ComponentKey = "suite|component:main",
        ValidSampleCount = 3,
        LastLaunchContributionWeight = 1d,
        AverageLateWorkingSetBytes = 200L * 1024 * 1024,
        StableWorkingSetSamplesBytes = new[]
        {
            180L * 1024 * 1024,
            200L * 1024 * 1024,
            220L * 1024 * 1024
        },
        StableLastObservedAt = Now
    };
    private static readonly ApplicationStableLearningRecord StableRecord = StableRecordWith(
        180L * 1024 * 1024,
        200L * 1024 * 1024,
        220L * 1024 * 1024);

    private static ApplicationStableLearningRecord StableRecordWith(params long[] samples)
    {
        var stableSamples = samples.Select((bytes, index) => new ApplicationStableSample(
            bytes, Now.AddSeconds(index), "launch-1", "cycle-1", 1, PendingHigh: false)).ToArray();
        return new ApplicationStableLearningRecord("suite", samples, Now, "launch-1")
        {
            ComponentKeys = new[] { "suite|component:main" },
            StableSamples = stableSamples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = StableWorkingSetLearningPolicy.Median(samples),
            LastStableLaunchSampleCount = samples.Length
        };
    }

    private static bool IsSuppressed(
        ApplicationStableLearningRecord record,
        long currentWorkingSetBytes,
        StableStateSuppressionSettings settings,
        DateTimeOffset now) =>
        StableStateSuppressionPolicy.SuppressionLimitBytes(record, settings, now) is { } limit &&
        Math.Max(0, currentWorkingSetBytes) <= limit;

    [Fact]
    public void ModesUseDistinctGrowthMarginsAndDisabledNeverSuppresses()
    {
        var mib = 1024L * 1024;

        Assert.True(IsSuppressed(StableRecord, 300 * mib,
            StableStateSuppressionSettings.For(StableStateSuppressionMode.ReduceRepeatedOptimization), Now));
        Assert.False(IsSuppressed(StableRecord, 300 * mib,
            StableStateSuppressionSettings.For(StableStateSuppressionMode.Balanced), Now));
        Assert.True(IsSuppressed(StableRecord, 280 * mib,
            StableStateSuppressionSettings.For(StableStateSuppressionMode.Balanced), Now));
        Assert.False(IsSuppressed(StableRecord, 280 * mib,
            StableStateSuppressionSettings.For(StableStateSuppressionMode.FasterReevaluation), Now));
    }

    [Fact]
    public void SuppressionLimitUsesTheLargerAbsoluteOrRelativeGrowthMargin()
    {
        var mib = 1024L * 1024;
        var balanced = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var relativeDominates = balanced with
        {
            RelativeGrowthMargin = 0.5,
            AbsoluteGrowthMarginBytes = 96 * mib
        };

        Assert.Equal(296 * mib, StableStateSuppressionPolicy.SuppressionLimitBytes(StableRecord, balanced, Now));
        Assert.Equal(
            450 * mib,
            StableStateSuppressionPolicy.SuppressionLimitBytes(
                StableRecordWith(280 * mib, 300 * mib, 320 * mib),
                relativeDominates,
                Now));
        Assert.Null(StableStateSuppressionPolicy.SuppressionLimitBytes(
            StableRecordWith(180 * mib, 220 * mib),
            balanced,
            Now));
    }

    [Fact]
    public void ApplicationSteadyStateLimitCapsPersistedAndRuntimeSuppressionLimits()
    {
        const long mib = 1024L * 1024;
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
        {
            RelativeGrowthMargin = 1.5,
            AbsoluteGrowthMarginBytes = 1024 * mib,
            MaximumStableWorkingSetBytes = 512 * mib
        };

        Assert.Equal(512 * mib,
            StableStateSuppressionPolicy.SuppressionLimitBytes(StableRecord, settings, Now));
        Assert.Equal(512 * mib,
            StableStateSuppressionPolicy.SuppressionLimitBytes(400 * mib, settings));
    }

    [Fact]
    public void CustomParametersControlSampleAgeAndGrowthMargins()
    {
        var mib = 1024L * 1024;
        var strict = new StableStateSuppressionSettings(4, TimeSpan.FromDays(30), 0.1, 16 * mib);
        var permissive = new StableStateSuppressionSettings(3, TimeSpan.FromDays(30), 0.5, 128 * mib);

        Assert.False(IsSuppressed(StableRecord, 220 * mib, strict, Now));
        Assert.True(IsSuppressed(StableRecord, 220 * mib, permissive, Now));
        Assert.False(IsSuppressed(
            StableRecord with { StableLastObservedAt = Now - TimeSpan.FromDays(31) },
            200 * mib, permissive, Now));
    }

    [Fact]
    public void FamilySuppressionUsesOnlyTheCompleteLearnedComponentSet()
    {
        const long mib = 1024L * 1024;
        const string mainPath = @"C:\Apps\Suite\main.exe";
        const string helperPath = @"C:\Apps\Suite\helper.exe";
        const string unrelatedPath = @"C:\Apps\Suite\unrelated.exe";
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", mainPath, null, 300 * mib, 0, 0, false, false, true, 90),
            new ProcessSnapshot(2, "helper", helperPath, null, 300 * mib, 0, 0, false, false, true, 90),
            new ProcessSnapshot(3, "unrelated", unrelatedPath, null, 4 * mib, 0, 0, false, false, true, 90)
        });
        var mainKey = ApplicationComponentIdentity.ForExecutable(family.Key, mainPath);
        var helperKey = ApplicationComponentIdentity.ForExecutable(family.Key, helperPath);
        var stable = new ApplicationStableLearningRecord(
            family.Key, new[] { 480 * mib, 500 * mib, 520 * mib }, Now, "launch")
        {
            ComponentKeys = new[] { mainKey, helperKey }
        };
        var optimizationSettings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var protection = new ProtectionRules();

        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, new[] { stable },
            optimizationSettings, protection,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
            {
                MaximumStableWorkingSetBytes = long.MaxValue
            }, Now);

        Assert.Equal(2, suppressed.Count);
        Assert.Contains(mainKey, suppressed);
        Assert.Contains(helperKey, suppressed);
        Assert.DoesNotContain(ApplicationComponentIdentity.ForExecutable(family.Key, unrelatedPath), suppressed);

        var partialProtection = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = mainPath,
                ProtectEntireFamily = false,
                ProtectedExecutablePaths = new List<string> { helperPath }
            }
        });
        Assert.Empty(StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, new[] { stable },
            optimizationSettings, partialProtection,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo), Now));

        var mainOnlyStable = new ApplicationStableLearningRecord(
            family.Key, new[] { 280 * mib, 300 * mib, 320 * mib }, Now, "main-only-launch")
        {
            ComponentKeys = new[] { mainKey }
        };
        var partialSuppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, new[] { stable, mainOnlyStable },
            optimizationSettings, partialProtection,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo), Now);
        Assert.Equal(new[] { mainKey }, partialSuppressed);

        var restored = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, new[] { stable, mainOnlyStable },
            optimizationSettings, protection,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo) with
            {
                MaximumStableWorkingSetBytes = long.MaxValue
            }, Now);
        Assert.Equal(2, restored.Count);

        var incompleteFamily = family with { Processes = family.Processes.Where(process => process.ProcessId != 2).ToArray() };
        Assert.Empty(StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { incompleteFamily }, new[] { stable },
            optimizationSettings, protection,
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo), Now));
    }

    [Fact]
    public void StableSuppressionDoesNotYieldToShortTermBenefitLearning()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", path, null, 200 * mib, 0, 0, false, false, true, 90)
        });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var stable = new ApplicationStableLearningRecord(
            family.Key, new[] { 190 * mib, 200 * mib, 210 * mib }, Now, "launch-1")
        {
            ComponentKeys = new[] { componentKey }
        };
        var settings = StableStateSuppressionSettings.For(OptimizationProfile.Turbo);
        var optimization = OptimizationSettings.For(OptimizationProfile.Turbo);
        var protection = new ProtectionRules();
        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family }, new[] { stable },
            optimization, protection, settings, Now);

        Assert.Equal(new[] { componentKey }, suppressed);
    }

    [Fact]
    public void StableSuppressionUsesAcceptedSamplesInsteadOfLegacyCompatibilityArray()
    {
        var legacyHeavyRecord = StableRecordWith(180L * 1024 * 1024, 220L * 1024 * 1024) with
        {
            StableWorkingSetSamplesBytes = Enumerable.Repeat(200L * 1024 * 1024, 50).ToArray()
        };

        Assert.False(IsSuppressed(legacyHeavyRecord, 200L * 1024 * 1024,
            new StableStateSuppressionSettings(3, TimeSpan.FromDays(30), 0.5, 128L * 1024 * 1024), Now));
    }

    [Fact]
    public void StableReferenceUsesMedianAndConvergenceToleranceHasLowMemoryGuard()
    {
        const long mib = 1024L * 1024;
        var even = StableRecordWith(100 * mib, 200 * mib, 300 * mib, 400 * mib);

        Assert.Equal(250 * mib, StableStateSuppressionPolicy.StableReferenceBytes(even));
        Assert.False(StableWorkingSetLearningPolicy.IsConverged(16 * mib, 48 * mib));
        Assert.True(StableWorkingSetLearningPolicy.IsConverged(100 * mib, 108 * mib));
        Assert.False(StableWorkingSetLearningPolicy.IsConverged(100 * mib, 120 * mib));
        Assert.True(StableWorkingSetLearningPolicy.IsConverged(400 * mib, 432 * mib));
        Assert.False(StableWorkingSetLearningPolicy.IsConverged(400 * mib, 433 * mib));
    }

    [Fact]
    public void TrustedRangeUsesTheLargestConvergedClusterWithoutExpandingForOutliers()
    {
        const long mib = 1024L * 1024;

        var range = StableWorkingSetLearningPolicy.TrustedRange(
            new[] { 400 * mib, 410 * mib, 900 * mib, 420 * mib },
            minimumSamples: 3);

        Assert.NotNull(range);
        Assert.Equal(400 * mib, range.MinimumBytes);
        Assert.Equal(420 * mib, range.MaximumBytes);
        Assert.Equal(410 * mib, range.MedianBytes);
        Assert.Equal(3, range.SampleCount);
        Assert.Null(StableWorkingSetLearningPolicy.TrustedRange(
            new[] { 400 * mib, 450 * mib, 500 * mib },
            minimumSamples: 3));
    }

    [Fact]
    public void ReferenceRangeShowsTheActualSampleSpanWithoutConvergenceGating()
    {
        const long mib = 1024L * 1024;

        var range = StableWorkingSetLearningPolicy.ReferenceRange(
            new[] { 285 * mib, 316 * mib, 339 * mib },
            minimumSamples: 3);

        Assert.NotNull(range);
        Assert.Equal(285 * mib, range.MinimumBytes);
        Assert.Equal(339 * mib, range.MaximumBytes);
        Assert.Equal(316 * mib, range.MedianBytes);
    }

    [Fact]
    public void HighSamplesFromOneRecoveryCycleCannotMoveTheAnchor()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);

        foreach (var value in new[] { 300L, 304L, 308L })
        {
            record = StableAnchorLearningPolicy.CommitSample(
                record,
                Sample(value * mib, "launch-2", "recovery-2"),
                minimumSamples: 3,
                maximumSamples: 9);
        }

        Assert.Equal(1, record.AnchorGeneration);
        Assert.Equal(200 * mib, record.AnchorGenerationBaselineBytes);
        Assert.Equal(200 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record));
        Assert.Single(record.StableSamples.Where(sample => sample.PendingHigh));
        Assert.Equal(308 * mib, record.StableSamples.Single(sample => sample.PendingHigh).WorkingSetBytes);
    }

    [Fact]
    public void PendingEvidenceExpiresAfterSevenDaysWithoutRemovingAcceptedAnchorSamples()
    {
        const long mib = 1024L * 1024;
        var now = DateTimeOffset.UtcNow;
        var accepted = Sample(200 * mib, "launch-1", "recovery-1") with { ObservedAt = now.AddDays(-30) };
        var pending = Sample(300 * mib, "launch-2", "recovery-2") with
        {
            ObservedAt = now.AddDays(-8),
            PendingHigh = true
        };
        var record = EstablishedAnchor(200 * mib) with
        {
            StableSamples = new[] { accepted, pending },
            StableWorkingSetSamplesBytes = new[] { accepted.WorkingSetBytes, pending.WorkingSetBytes }
        };

        var expired = StableAnchorLearningPolicy.ExpirePendingEvidence(record, now);

        Assert.Single(expired.StableSamples);
        Assert.Equal(accepted.WorkingSetBytes, expired.StableSamples[0].WorkingSetBytes);
    }

    [Fact]
    public void HighSampleClassificationUsesTheAcceptedClusterCenterInsteadOfAStaleLowBaseline()
    {
        const long mib = 1024L * 1024;
        var samples = new[]
        {
            Sample(188 * mib, "launch-1", "recovery-1"),
            Sample(206 * mib, "launch-1", "recovery-2")
        };
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            samples[^1].ObservedAt, "launch-1")
        {
            ComponentKeys = new[] { "app|component:main" },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 2,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 188 * mib
        };

        var updated = StableAnchorLearningPolicy.CommitSample(
            record,
            Sample(226 * mib, "launch-1", "recovery-3"),
            minimumSamples: 3,
            maximumSamples: 9);

        Assert.False(updated.StableSamples[^1].PendingHigh);
        Assert.Equal(3, StableAnchorLearningPolicy.AcceptedSampleCount(updated));
    }

    [Fact]
    public void LoadingReclassifiesExistingPendingSampleInsideTheAcceptedClusterBoundary()
    {
        const long mib = 1024L * 1024;
        var samples = new[]
        {
            Sample(188 * mib, "launch-1", "recovery-1"),
            Sample(206 * mib, "launch-1", "recovery-2"),
            Sample(226 * mib, "launch-1", "recovery-3") with { PendingHigh = true }
        };
        var record = new ApplicationStableLearningRecord(
            "app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            samples[^1].ObservedAt, "launch-1")
        {
            ComponentKeys = new[] { "app|component:main" },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 3,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = 188 * mib
        };

        var normalized = StableAnchorLearningPolicy.ReclassifyPendingHighSamples(record);

        Assert.False(normalized.StableSamples[^1].PendingHigh);
        Assert.Equal(3, StableAnchorLearningPolicy.AcceptedSampleCount(normalized));
    }

    [Fact]
    public void HighSamplesAcrossThreeCyclesButOneLaunchCannotMoveTheAnchor()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            record = StableAnchorLearningPolicy.CommitSample(
                record,
                Sample((300 + cycle) * mib, "launch-2", $"recovery-{cycle}"),
                minimumSamples: 3,
                maximumSamples: 9);
        }

        Assert.Equal(1, record.AnchorGeneration);
        Assert.Equal(200 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record));
    }

    [Fact]
    public void ConvergedHighSamplesAcrossIndependentCyclesCreateANewGeneration()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);
        var evidence = new[]
        {
            Sample(300 * mib, "launch-2", "recovery-2"),
            Sample(304 * mib, "launch-2", "recovery-3"),
            Sample(308 * mib, "launch-3", "recovery-4")
        };

        foreach (var sample in evidence)
        {
            record = StableAnchorLearningPolicy.CommitSample(
                record,
                sample,
                minimumSamples: 3,
                maximumSamples: 9);
        }

        Assert.Equal(2, record.AnchorGeneration);
        Assert.Equal(304 * mib, record.AnchorGenerationBaselineBytes);
        Assert.Equal(304 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record));
        Assert.Equal(3, record.StableSamples.Count(sample =>
            sample.Generation == 2 && !sample.PendingHigh));
    }

    [Fact]
    public void PendingHighSamplesExpandTheReferenceRangeWithoutChangingTheAnchor()
    {
        const long mib = 1024L * 1024;
        var record = StableAnchorLearningPolicy.CommitSample(
            EstablishedAnchor(200 * mib),
            Sample(500 * mib, "launch-2", "recovery-2"),
            minimumSamples: 3,
            maximumSamples: 9);

        var range = StableAnchorLearningPolicy.ReferenceRange(record, maximumSamples: 9);

        Assert.NotNull(range);
        Assert.Equal(200 * mib, range.MinimumBytes);
        Assert.Equal(500 * mib, range.MaximumBytes);
        Assert.Equal(200 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record));
    }

    [Fact]
    public void MixedInitialSamplesWaitForAConvergedClusterBeforeCreatingAnAnchor()
    {
        const long mib = 1024L * 1024;
        var record = new ApplicationStableLearningRecord(
            "app", Array.Empty<long>(), null, null)
        {
            ComponentKeys = new[] { "app|component:main" },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion
        };
        foreach (var value in new[] { 500L, 500L, 300L })
            record = StableAnchorLearningPolicy.CommitSample(
                record, Sample(value * mib, "launch-1", $"recovery-{value}"), 3, 9);

        Assert.Equal(0, record.AnchorGeneration);
        Assert.Equal(0, record.AnchorGenerationBaselineBytes);

        foreach (var cycle in new[] { 2, 3 })
            record = StableAnchorLearningPolicy.CommitSample(
                record, Sample(300 * mib, "launch-1", $"recovery-low-{cycle}"), 3, 9);

        Assert.Equal(1, record.AnchorGeneration);
        Assert.Equal(300 * mib, record.AnchorGenerationBaselineBytes);
    }

    [Fact]
    public void LastNightCrossPlatformSamplesRemainUnacceptedUntilAClusterForms()
    {
        const long mib = 1024L * 1024;
        var record = new ApplicationStableLearningRecord(
            "app", Array.Empty<long>(), null, null)
        {
            ComponentKeys = new[] { "app|component:main" },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion
        };

        foreach (var (value, cycle) in new[]
                 {
                     (33L, "recovery-1"),
                     (266L, "recovery-2"),
                     (293L, "recovery-3")
                 })
        {
            record = StableAnchorLearningPolicy.CommitSample(
                record,
                Sample(value * mib, "launch-1", cycle) with { Generation = 0 },
                minimumSamples: 3,
                maximumSamples: 9);
        }

        Assert.Equal(0, record.AnchorGeneration);
        Assert.Equal(0, StableAnchorLearningPolicy.AcceptedSampleCount(record, 9));
        Assert.Equal(3, record.StableSamples.Count(sample =>
            sample.Generation == 0 && !sample.PendingHigh));
        Assert.Null(StableAnchorLearningPolicy.EffectiveAnchorBytes(record, 9));
    }

    [Fact]
    public void PendingHighSamplesCannotEvictTheTrustedAnchorEvidence()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);
        for (var cycle = 0; cycle < 7; cycle++)
            record = StableAnchorLearningPolicy.CommitSample(
                record,
                Sample((300 + cycle) * mib, "launch-2", "same-recovery"),
                minimumSamples: 3,
                maximumSamples: 9);

        Assert.Equal(4, record.StableSamples.Count);
        Assert.Equal(306 * mib, record.StableSamples.Single(sample => sample.PendingHigh).WorkingSetBytes);
        Assert.Equal(3, StableAnchorLearningPolicy.AcceptedSampleCount(record, 9));
        Assert.Equal(200 * mib, StableAnchorLearningPolicy.EffectiveAnchorBytes(record, 9));
    }

    [Fact]
    public void CurrentLaunchAcceptedCountExcludesPendingHighAndUnclassifiedSamples()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);
        var samples = record.StableSamples.ToArray();
        samples[^1] = samples[^1] with { PendingHigh = true };
        record = record with
        {
            StableSamples = samples,
            StableWorkingSetSamplesBytes = samples.Select(sample => sample.WorkingSetBytes).ToArray()
        };

        Assert.Equal(2, StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
            record, "launch-1", 9));
        Assert.Equal(0, StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
            record with { AnchorGeneration = 0, AnchorGenerationBaselineBytes = 0 }, "launch-1", 9));
    }

    [Fact]
    public void IndependentCyclesInOneLongLaunchCanMigrateAfterThirtyMinutes()
    {
        const long mib = 1024L * 1024;
        var record = EstablishedAnchor(200 * mib);
        var startedAt = DateTimeOffset.UtcNow;
        foreach (var sample in new[]
                 {
                     Sample(300 * mib, "launch-2", "recovery-2") with { ObservedAt = startedAt },
                     Sample(304 * mib, "launch-2", "recovery-3") with { ObservedAt = startedAt.AddMinutes(15) },
                     Sample(308 * mib, "launch-2", "recovery-4") with { ObservedAt = startedAt.AddMinutes(30) }
                 })
            record = StableAnchorLearningPolicy.CommitSample(record, sample, 3, 9);

        Assert.Equal(2, record.AnchorGeneration);
        Assert.Equal(304 * mib, record.AnchorGenerationBaselineBytes);
    }

    [Fact]
    public void LearnedScopeRemainsActiveAfterItsComponentFallsBelowCandidateThresholds()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", path, null, 40 * mib, 0, 0, false, false, true, 90)
        });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var record = new ApplicationStableLearningRecord(
            family.Key, new[] { 40 * mib, 41 * mib, 42 * mib }, Now, "launch")
        {
            ComponentKeys = new[] { componentKey }
        };

        var active = StableStateSuppressionPolicy.ActiveStableRecord(
            family,
            new[] { family },
            new[] { record },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules());

        Assert.Same(record, active);
    }

    [Fact]
    public void RecoveryScopeRemainsObservableBelowCandidateThresholds()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", path, null, 40 * mib, 0, 0, false, false, true, 90, 123)
        });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var snapshots = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, CandidateIdleReadiness> { [1] = new(1, 2, true) },
            recoveryScopes: new[]
            {
                new NaturalStableScopeRequest(family.Key, new[] { componentKey }, Now.AddMinutes(-1))
            });

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(new[] { componentKey }, snapshot.ComponentKeys);
        Assert.Equal(40 * mib, snapshot.WorkingSetBytes);
        Assert.Equal(Now.AddMinutes(-1), snapshot.RecoveryStartedAt);
    }

    private static ApplicationStableLearningRecord EstablishedAnchor(long bytes)
    {
        var samples = new[]
        {
            Sample(bytes, "launch-1", "recovery-1"),
            Sample(bytes, "launch-1", "recovery-1"),
            Sample(bytes, "launch-1", "recovery-1")
        };
        return new ApplicationStableLearningRecord("app", samples.Select(sample => sample.WorkingSetBytes).ToArray(),
            samples[^1].ObservedAt, "launch-1")
        {
            ComponentKeys = new[] { "app|component:main" },
            ModelVersion = StableStateSuppressionPolicy.NaturalStableStateModelVersion,
            LastStableLaunchSampleCount = 3,
            StableSamples = samples,
            AnchorGeneration = 1,
            AnchorGenerationBaselineBytes = bytes
        };
    }

    private static ApplicationStableSample Sample(long bytes, string launch, string recovery) =>
        new(bytes, DateTimeOffset.UtcNow, launch, recovery, Generation: 1, PendingHigh: false);

    [Fact]
    public void FixedAnchorDisplayEndpointIsClampedToTheExactReferenceBytes()
    {
        const long displayed300MiB = 314_572_800;
        const long actualMaximum = 314_521_600;

        var saved = StableAnchorLearningPolicy.ClampFixedAnchorBytes(
            displayed300MiB,
            242_728_960,
            actualMaximum);

        Assert.Equal(actualMaximum, saved);
    }

    [Fact]
    public void FixedAnchorRequiresSamplesButRemainsActiveWhenTheReferenceRangeMoves()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[]
        {
            new ProcessSnapshot(1, "main", path, null, 300 * mib, 0, 0, false, false, true, 90)
        });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var scopeKey = ApplicationStableScopeIdentity.For(family.Key, new[] { componentKey });
        var anchor = new ApplicationStableAnchorSetting(
            family.Key,
            scopeKey,
            StableAnchorMode.Fixed,
            250 * mib);
        var settings = new StableStateSuppressionSettings(
            3,
            TimeSpan.FromDays(30),
            0.2,
            64 * mib);

        var withoutSamples = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            Array.Empty<ApplicationStableLearningRecord>(),
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            settings,
            Now,
            anchorSettings: new[] { anchor });

        Assert.Empty(withoutSamples);
        Assert.Empty(StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            Array.Empty<ApplicationStableLearningRecord>(),
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            settings,
            Now,
            anchorSettings: new[] { anchor with { Mode = StableAnchorMode.Adaptive } }));

        var learned = new ApplicationStableLearningRecord(
            family.Key,
            new[] { 200 * mib, 300 * mib, 400 * mib },
            Now,
            "launch")
        {
            ComponentKeys = new[] { componentKey }
        };
        Assert.Equal(new[] { componentKey }, StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            new[] { learned },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            settings,
            Now,
            anchorSettings: new[] { anchor }));
        Assert.Equal(new[] { componentKey }, StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            new[] { learned },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            settings,
            Now,
            anchorSettings: new[] { anchor with { FixedAnchorBytes = 450 * mib } }));
    }

    [Fact]
    public void FixedAnchorWithoutEligibleSavedSamplesFallsBackToTheRuntimeAnchor()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var process = new ProcessSnapshot(
            1, "main", path, null, 300 * mib, 0, 0, false, false, true, 90, 123);
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[] { process });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var scopeKey = ApplicationStableScopeIdentity.For(family.Key, new[] { componentKey });
        var runtime = new ApplicationStableCandidateStatus(
            family.Key,
            scopeKey,
            $"{componentKey}@123",
            ApplicationStableCandidateState.Converged,
            300 * mib,
            300 * mib,
            300 * mib,
            3,
            Now);
        var anchor = new ApplicationStableAnchorSetting(
            family.Key,
            scopeKey,
            StableAnchorMode.Fixed,
            300 * mib);

        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            Array.Empty<ApplicationStableLearningRecord>(),
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            Now,
            runtimeCandidates: new[] { runtime },
            anchorSettings: new[] { anchor });

        Assert.Equal(new[] { componentKey }, suppressed);
    }

    [Fact]
    public void PersistedStableLimitIsNotWidenedByTheRuntimeAnchor()
    {
        const long mib = 1024L * 1024;
        const string path = @"C:\Apps\Suite\main.exe";
        var process = new ProcessSnapshot(
            1, "main", path, null, 190 * mib, 0, 0, false, false, true, 90, 123);
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[] { process });
        var componentKey = ApplicationComponentIdentity.ForExecutable(family.Key, path);
        var scopeKey = ApplicationStableScopeIdentity.For(family.Key, new[] { componentKey });
        var launchSignature = $"{componentKey}@123";
        var persisted = new ApplicationStableLearningRecord(
            family.Key,
            new[] { 88 * mib, 88 * mib, 88 * mib },
            Now,
            launchSignature)
        {
            ComponentKeys = new[] { componentKey }
        };
        var runtime = new ApplicationStableCandidateStatus(
            family.Key,
            scopeKey,
            launchSignature,
            ApplicationStableCandidateState.Converged,
            154 * mib,
            154 * mib,
            190 * mib,
            3,
            Now);
        var settings = new StableStateSuppressionSettings(
            3,
            TimeSpan.FromDays(30),
            0.25,
            96 * mib);

        var suppressed = StableStateSuppressionPolicy.SuppressedComponentKeys(
            new[] { family },
            new[] { persisted },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            settings,
            Now,
            runtimeCandidates: new[] { runtime });

        Assert.Equal(184 * mib, StableStateSuppressionPolicy.SuppressionLimitBytes(persisted, settings, Now));
        Assert.Empty(suppressed);
    }

    [Fact]
    public void LegacySchemaRecordWithoutContributionWeightHasNoLearningPolicyEligibility()
    {
        var now = DateTimeOffset.UtcNow;
        var legacy = new ApplicationBenefitLearningRecord("legacy", 0.5, 8, 8, now)
        {
            ComponentKey = "legacy|component:main",
            ValidSampleCount = 8,
            DistinctLaunchCount = 3,
            AverageLateWorkingSetBytes = 200L * 1024 * 1024,
            AverageRetainedBytes = 32L * 1024 * 1024,
            AverageReboundPercent = 80,
            RecentBackoffRate = 0.5,
            LateWorkingSetSamplesBytes = Enumerable.Repeat(200L * 1024 * 1024, 8).ToArray()
        };

        Assert.Empty(legacy.StableWorkingSetSamplesBytes);
        Assert.Empty(ProtectionSuggestionPolicy.Create(
            new[] { legacy },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Empty(ApplicationOptimizationThresholdSuggestionPolicy.Create(new[] { legacy }));
    }

    [Fact]
    public void FollowBaseProfileMapsLiteToFewerRepeatsAndTurboToBalanced()
    {
        Assert.Equal(
            StableStateSuppressionMode.ReduceRepeatedOptimization,
            StableStateSuppressionPolicy.ResolveMode(
                OptimizationProfile.Lite,
                StableStateSuppressionMode.FollowBaseProfile));
        Assert.Equal(
            StableStateSuppressionMode.Balanced,
            StableStateSuppressionPolicy.ResolveMode(
                OptimizationProfile.Turbo,
                StableStateSuppressionMode.FollowBaseProfile));
    }

    [Fact]
    public void ManualPlanOverridesStableSuppressionWhileAutomaticPlanDoesNot()
    {
        const string path = @"C:\Apps\Suite\main.exe";
        var process = new ProcessSnapshot(
            1, "main", path, null, 300L * 1024 * 1024, 0, 0,
            false, false, true, 90);
        var family = new ProcessFamilySnapshot("suite", "Suite", @"C:\Apps\Suite", new[] { process });
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            IgnoreMemoryPressureThreshold = true,
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0,
            MinimumIdleScore = 0,
            ProcessCooldown = TimeSpan.Zero
        };
        var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ApplicationComponentIdentity.ForExecutable(family.Key, path)
        };
        var planner = new OptimizationPlanner();

        var automatic = planner.CreatePlan(
            new MemorySnapshot(1, 1, 0), new[] { family }, settings,
            new ProtectionRules(), new Dictionary<int, DateTimeOffset>(), Now,
            manual: false, stableSuppressedComponents: stable);
        var manual = planner.CreatePlan(
            new MemorySnapshot(1, 1, 0), new[] { family }, settings,
            new ProtectionRules(), new Dictionary<int, DateTimeOffset>(), Now,
            manual: true, stableSuppressedComponents: stable);

        Assert.Empty(automatic.Candidates);
        Assert.Single(manual.Candidates);
    }
}

public sealed class ProtectionSuggestionPolicyTests
{
    [Fact]
    public void RequiresEnoughSamplesCrossLaunchEvidenceBackoffsAndHighRebound()
    {
        var now = DateTimeOffset.UtcNow;
        var eligible = Record("eligible", 8, 3, 3, 70, now);
        var tooFewSamples = Record("samples", 7, 2, 3, 90, now);
        var oneLaunch = Record("launch", 10, 1, 5, 90, now);
        var tooFewBackoffs = Record("backoff", 10, 3, 2, 90, now);
        var lowRebound = Record("rebound", 10, 3, 5, 69.9, now);

        var suggestions = ProtectionSuggestionPolicy.Create(
            new[] { eligible, tooFewSamples, oneLaunch, tooFewBackoffs, lowRebound },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("eligible", Assert.Single(suggestions).FamilyKey);
    }

    [Fact]
    public void DismissedSuggestionIdDoesNotAppearAgain()
    {
        var record = Record("eligible", 8, 3, 3, 80, DateTimeOffset.UtcNow);
        var id = ProtectionSuggestionPolicy.SuggestionId(record);

        var suggestions = ProtectionSuggestionPolicy.Create(
            new[] { record },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id });

        Assert.Empty(suggestions);
    }

    private static ApplicationBenefitLearningRecord Record(
        string familyKey,
        int samples,
        int launches,
        int backoffs,
        double rebound,
        DateTimeOffset now) => new(familyKey, 0.2, samples, 0, now)
    {
        ComponentKey = familyKey + "|component:main",
        ExecutablePath = $@"C:\Apps\{familyKey}\main.exe",
        DistinctLaunchCount = launches,
        BackoffTriggerCount = backoffs,
        ValidSampleCount = samples,
        RecentBackoffRate = backoffs / (double)samples,
        AverageReboundPercent = rebound,
        AverageRetainedBytes = 32L * 1024 * 1024,
        LastLaunchContributionWeight = 1d
    };
}

public sealed class ApplicationOptimizationThresholdSuggestionPolicyTests
{
    [Fact]
    public void UsesValidCrossLaunchSamplesAndLateWorkingSetP75WithSafetyMargin()
    {
        var now = DateTimeOffset.UtcNow;
        var eligible = new ApplicationBenefitLearningRecord("eligible", 0.8, 32, 0, now)
        {
            ComponentKey = "eligible|component:main",
            ExecutablePath = @"C:\Apps\eligible\main.exe",
            ValidSampleCount = 8,
            DistinctLaunchCount = 3,
            AverageRetainedBytes = 100L * 1024 * 1024,
            AverageReboundPercent = 10,
            RecentBackoffRate = 0.1,
            RecentQuickReturnRate = 0.1,
            LastLaunchContributionWeight = 1d,
            LateWorkingSetSamplesBytes = new[]
            {
                100L * 1024 * 1024,
                100L * 1024 * 1024,
                100L * 1024 * 1024,
                100L * 1024 * 1024,
                200L * 1024 * 1024,
                200L * 1024 * 1024,
                200L * 1024 * 1024,
                400L * 1024 * 1024
            }
        };
        var invalid = eligible with
        {
            FamilyKey = "invalid",
            ComponentKey = "invalid|component:main",
            ValidSampleCount = 7
        };

        var suggestion = Assert.Single(
            ApplicationOptimizationThresholdSuggestionPolicy.Create(new[] { eligible, invalid }));

        Assert.Equal(8, suggestion.ValidSampleCount);
        Assert.Equal(3, suggestion.DistinctLaunchCount);
        Assert.Equal(250L * 1024 * 1024, suggestion.LateWorkingSetP75Bytes);
        Assert.Equal(266L * 1024 * 1024, suggestion.TriggerThresholdBytes);
    }

    [Fact]
    public void UsesRecentQuickReturnRateInsteadOfLegacyQuickReturnCount()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ApplicationBenefitLearningRecord("eligible", 0.8, 8, 8, now)
        {
            ComponentKey = "eligible|component:main",
            ValidSampleCount = 8,
            DistinctLaunchCount = 3,
            AverageRetainedBytes = 100L * 1024 * 1024,
            AverageReboundPercent = 10,
            RecentBackoffRate = 0.1,
            RecentQuickReturnRate = 0.1,
            LastLaunchContributionWeight = 1d,
            LateWorkingSetSamplesBytes = Enumerable.Repeat(100L * 1024 * 1024, 8).ToArray()
        };

        Assert.Single(ApplicationOptimizationThresholdSuggestionPolicy.Create(new[] { record }));
    }
}
