using System.Diagnostics;

namespace MuseRAM.Core.Tests;

public class ProcessSamplerTests
{
    [Fact]
    public void ProcessPathUsesLimitedInformationBeforeMainModuleFallback()
    {
        var fallbackCalls = 0;
        var path = ProcessSampler.ResolveProcessPath(
            Environment.ProcessId,
            () =>
            {
                fallbackCalls++;
                throw new InvalidOperationException("MainModule unavailable");
            });

        Assert.Equal(
            Path.GetFullPath(Environment.ProcessPath!),
            Path.GetFullPath(path!),
            ignoreCase: true);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void ProcessPathFallsBackToMainModuleWhenLimitedQueryFails()
    {
        const string fallbackPath = @"C:\fallback\process.exe";

        var path = ProcessSampler.ResolveProcessPath(int.MaxValue, () => fallbackPath);

        Assert.Equal(fallbackPath, path);
    }

    [Fact]
    public void FirstCaptureMarksActivitySamplesAsUnreliable()
    {
        var sampler = new ProcessSampler();

        var snapshots = sampler.Capture();

        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, snapshot => Assert.False(snapshot.HasReliableActivitySample));
    }

    [Fact]
    public void CaptureExcludesMuseRamItself()
    {
        var snapshots = new ProcessSampler().Capture();

        Assert.DoesNotContain(snapshots, snapshot => snapshot.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void CaptureReportsNonNegativePhaseDiagnostics()
    {
        var sampler = new ProcessSampler();

        _ = sampler.Capture();
        var diagnostics = sampler.LastCaptureDiagnostics;

        Assert.True(diagnostics.TotalMilliseconds > 0);
        Assert.True(diagnostics.RelationshipSnapshotMilliseconds >= 0);
        Assert.True(diagnostics.WindowEnumerationMilliseconds >= 0);
        Assert.True(diagnostics.PathReadMilliseconds >= diagnostics.SlowestPathReadMilliseconds);
        Assert.True(diagnostics.CpuReadMilliseconds >= 0);
        Assert.True(diagnostics.IoReadMilliseconds >= 0);
        Assert.True(diagnostics.ProcessLoopMilliseconds >= 0);
        Assert.True(diagnostics.OtherMilliseconds >= 0);
    }

    [Fact]
    public void ConsecutiveCpuReadFailuresNeverBecomeReliableIdleSamples()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();

        var firstFailure = sampler.SampleCpu(101, startedAt, () => throw new InvalidOperationException());
        var secondFailure = sampler.SampleCpu(101, startedAt + Stopwatch.Frequency, () => throw new InvalidOperationException());
        var firstSuccess = sampler.SampleCpu(101, startedAt + 2 * Stopwatch.Frequency, () => TimeSpan.FromSeconds(10));

        Assert.False(firstFailure.IsReliable);
        Assert.False(secondFailure.IsReliable);
        Assert.False(firstSuccess.IsReliable);
    }

    [Fact]
    public void CpuReadFailuresDoNotReplaceLastSuccessfulBaseline()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();

        _ = sampler.SampleCpu(101, startedAt, () => TimeSpan.FromSeconds(10));
        var firstFailure = sampler.SampleCpu(101, startedAt + Stopwatch.Frequency, () => throw new InvalidOperationException());
        var secondFailure = sampler.SampleCpu(101, startedAt + 2 * Stopwatch.Frequency, () => throw new InvalidOperationException());
        var recovered = sampler.SampleCpu(101, startedAt + 3 * Stopwatch.Frequency, () => TimeSpan.FromSeconds(13));

        Assert.False(firstFailure.IsReliable);
        Assert.False(secondFailure.IsReliable);
        Assert.True(recovered.IsReliable);
        Assert.Equal(100d / Environment.ProcessorCount, recovered.Value, precision: 6);
    }

    [Fact]
    public void ReusedProcessIdMustEstablishANewCpuBaseline()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();
        sampler.ObserveProcessIdentity(101, 1001, "old", @"F:\Apps\Old\old.exe");
        _ = sampler.SampleCpu(101, startedAt, () => TimeSpan.FromSeconds(10));

        sampler.ObserveProcessIdentity(101, 2002, "new", @"F:\Apps\New\new.exe");
        var firstNewSample = sampler.SampleCpu(
            101,
            startedAt + Stopwatch.Frequency,
            () => TimeSpan.FromSeconds(20));

        Assert.False(firstNewSample.IsReliable);
    }

    [Fact]
    public void IoSampleRecordsReadWriteRatesAndActualInterval()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();
        _ = sampler.SampleIo(101, startedAt, readTransferCount: 1_000, writeTransferCount: 2_000);

        var sample = sampler.SampleIo(
            101,
            startedAt + 3 * Stopwatch.Frequency,
            readTransferCount: 7_000,
            writeTransferCount: 5_000);

        Assert.True(sample.IsReliable);
        Assert.Equal(2_000, sample.ReadBytesPerSecond);
        Assert.Equal(1_000, sample.WriteBytesPerSecond);
        Assert.Equal(3_000, sample.Value);
        Assert.Equal(3, sample.SampleIntervalSeconds);
        Assert.Equal(7_000UL, sample.ReadTransferCount);
        Assert.Equal(5_000UL, sample.WriteTransferCount);
        Assert.Equal(6_000UL, sample.ReadDeltaBytes);
        Assert.Equal(3_000UL, sample.WriteDeltaBytes);
    }

    [Fact]
    public void IoCounterRegressionStartsANewUnreliableBaseline()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();
        _ = sampler.SampleIo(101, startedAt, readTransferCount: 10_000, writeTransferCount: 20_000);

        var reset = sampler.SampleIo(
            101,
            startedAt + Stopwatch.Frequency,
            readTransferCount: 9_000,
            writeTransferCount: 21_000);

        Assert.False(reset.IsReliable);
        Assert.Equal(0, reset.Value);
    }

    [Fact]
    public void ReusedProcessIdMustEstablishANewIoBaseline()
    {
        var sampler = new ProcessSampler();
        var startedAt = Stopwatch.GetTimestamp();
        sampler.ObserveProcessIdentity(101, 1001, "old", @"F:\Apps\Old\old.exe");
        _ = sampler.SampleIo(101, startedAt, readTransferCount: 1_000, writeTransferCount: 2_000);

        sampler.ObserveProcessIdentity(101, 2002, "new", @"F:\Apps\New\new.exe");
        var firstNewSample = sampler.SampleIo(
            101,
            startedAt + Stopwatch.Frequency,
            readTransferCount: 50_000,
            writeTransferCount: 60_000);

        Assert.False(firstNewSample.IsReliable);
        Assert.Equal(50_000UL, firstNewSample.ReadTransferCount);
        Assert.Equal(60_000UL, firstNewSample.WriteTransferCount);
        Assert.Equal(0UL, firstNewSample.ReadDeltaBytes);
        Assert.Equal(0UL, firstNewSample.WriteDeltaBytes);
    }

    [Fact]
    public void RecentTrimPenaltyRequiresTheSameProcessIdentity()
    {
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        var trimTimes = new Dictionary<int, DateTimeOffset> { [101] = now.AddMinutes(-1) };
        var trimStartTimes = new Dictionary<int, long> { [101] = 1001 };

        Assert.True(ProcessTrimHistoryPolicy.IsRecentlyTrimmed(
            101, 1001, trimTimes, trimStartTimes, now));
        Assert.False(ProcessTrimHistoryPolicy.IsRecentlyTrimmed(
            101, 2002, trimTimes, trimStartTimes, now));
        Assert.False(ProcessTrimHistoryPolicy.IsRecentlyTrimmed(
            101, null, trimTimes, trimStartTimes, now));
    }

    [Fact]
    public void TrimHistoryExpiresAndDiscardsObservedPidReuse()
    {
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");

        Assert.True(ProcessTrimHistoryPolicy.ShouldDiscard(
            now - ProcessTrimHistoryPolicy.RetentionWindow,
            1001,
            currentProcessObserved: false,
            currentStartTimeFileTimeUtc: null,
            now));
        Assert.True(ProcessTrimHistoryPolicy.ShouldDiscard(
            now.AddMinutes(-1),
            1001,
            currentProcessObserved: true,
            currentStartTimeFileTimeUtc: 2002,
            now));
        Assert.False(ProcessTrimHistoryPolicy.ShouldDiscard(
            now.AddMinutes(-1),
            1001,
            currentProcessObserved: false,
            currentStartTimeFileTimeUtc: null,
            now));
    }

    [Fact]
    public void IdentityAwareCooldownUsesExactBoundaryAndRejectsReusedPid()
    {
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        var process = new ProcessSnapshot(
            101, "app", null, null, 1, 0, 0, false, false, true, 100,
            StartTimeFileTimeUtc: 1001);
        var trimStartTimes = new Dictionary<int, long> { [101] = 1001 };
        var cooldown = TimeSpan.FromSeconds(600);

        Assert.True(ProcessTrimHistoryPolicy.IsCoolingDown(
            process,
            new Dictionary<int, DateTimeOffset> { [101] = now - cooldown + TimeSpan.FromMilliseconds(1) },
            trimStartTimes,
            now,
            cooldown));
        Assert.False(ProcessTrimHistoryPolicy.IsCoolingDown(
            process,
            new Dictionary<int, DateTimeOffset> { [101] = now - cooldown },
            trimStartTimes,
            now,
            cooldown));
        Assert.False(ProcessTrimHistoryPolicy.IsCoolingDown(
            process with { StartTimeFileTimeUtc = 2002 },
            new Dictionary<int, DateTimeOffset> { [101] = now.AddMinutes(-1) },
            trimStartTimes,
            now,
            cooldown));
    }

    [Fact]
    public void IncompleteRelationshipSnapshotFailsTheSamplingRoundClosed()
    {
        var partialRelationships = new Dictionary<int, int?> { [101] = null };

        Assert.Throws<InvalidOperationException>(() =>
            ProcessRelationshipSnapshot.RequireReliable(
                captureSucceeded: false,
                partialRelationships));
        Assert.Same(
            partialRelationships,
            ProcessRelationshipSnapshot.RequireReliable(
                captureSucceeded: true,
                partialRelationships));
    }
}

public class ProcessColdnessPolicyTests
{
    [Fact]
    public void ColdLargeBackgroundProcessGetsMaximumColdnessScore()
    {
        var score = ProcessColdnessPolicy.Calculate(
            512L * 1024 * 1024,
            cpu: 0,
            io: 0,
            isForeground: false,
            hasVisibleWindow: false,
            wasRecentlyTrimmed: false);

        Assert.Equal(100, score);
    }

    [Fact]
    public void RecentTrimAppliesTwentyPointPenalty()
    {
        var score = ProcessColdnessPolicy.Calculate(
            256L * 1024 * 1024,
            cpu: 0,
            io: 0,
            isForeground: false,
            hasVisibleWindow: false,
            wasRecentlyTrimmed: true);

        Assert.Equal(77, score);
    }

    [Fact]
    public void ForegroundAndActivitySignalsReduceColdness()
    {
        var score = ProcessColdnessPolicy.Calculate(
            64L * 1024 * 1024,
            cpu: 9,
            io: 2d * 1024 * 1024,
            isForeground: true,
            hasVisibleWindow: true,
            wasRecentlyTrimmed: false);

        Assert.Equal(0, score);
    }
}

public class ProcessIdleConfidencePolicyTests
{
    [Fact]
    public void FullyIdleBackgroundProcessGetsMaximumConfidence()
    {
        var score = ProcessIdleConfidencePolicy.Calculate(
            cpu: 0,
            io: 0,
            isForeground: false,
            hasVisibleWindow: false);

        Assert.Equal(100, score);
    }

    [Fact]
    public void WorkingSetSizeDoesNotChangeIdleConfidence()
    {
        var small = new ProcessFamilySnapshot(
            "small", "small", null,
            new[] { CreateProcess(1, 64L * 1024 * 1024) });
        var large = new ProcessFamilySnapshot(
            "large", "large", null,
            new[] { CreateProcess(2, 2L * 1024 * 1024 * 1024) });

        Assert.Equal(small.IdleConfidenceScore, large.IdleConfidenceScore);
    }

    private static ProcessSnapshot CreateProcess(int processId, long workingSetBytes) => new(
        processId,
        "idle",
        null,
        null,
        workingSetBytes,
        0,
        0,
        false,
        false,
        true,
        100);
}

public class ExperimentalIdleScorePolicyTests
{
    [Fact]
    public void LongIdleLargeHiddenFamilyReachesOneHundred()
    {
        var family = new ProcessFamilySnapshot(
            "family", "family", null,
            new[]
            {
                new ProcessSnapshot(
                    10, "sample", null, null, 512L * 1024 * 1024, 0.2, 8 * 1024,
                    false, false, true, 90)
            });

        Assert.Equal(100, ExperimentalIdleScorePolicy.Calculate(family, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void DurationSeparatesNewlyBackgroundedAndLongIdleFamilies()
    {
        var family = new ProcessFamilySnapshot(
            "family", "family", null,
            new[]
            {
                new ProcessSnapshot(
                    10, "sample", null, null, 128L * 1024 * 1024, 0.2, 8 * 1024,
                    false, false, true, 90)
            });

        Assert.Equal(60, ExperimentalIdleScorePolicy.Calculate(family, TimeSpan.Zero));
        Assert.Equal(85, ExperimentalIdleScorePolicy.Calculate(family, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void UnreliableFamilyHasNoExperimentalScore()
    {
        var family = new ProcessFamilySnapshot(
            "family", "family", null,
            new[]
            {
                new ProcessSnapshot(
                    10, "sample", null, null, 512L * 1024 * 1024, 0, 0,
                    false, false, false, 100)
            });

        Assert.Equal(0, ExperimentalIdleScorePolicy.Calculate(family, TimeSpan.FromHours(1)));
    }
}

public class LocalIdleScoreShadowPolicyTests
{
    [Fact]
    public void SimilarQuietProcessesReceiveDistinctContinuousScores()
    {
        var first = Family(cpu: 0.05, io: 2 * 1024);
        var second = Family(cpu: 0.35, io: 24 * 1024);

        var firstScore = LocalIdleScoreShadowPolicy.Calculate(first, TimeSpan.FromMinutes(22));
        var secondScore = LocalIdleScoreShadowPolicy.Calculate(second, TimeSpan.FromMinutes(22));

        Assert.True(firstScore > secondScore);
        Assert.InRange(firstScore - secondScore, 0.1, 10);
    }

    [Fact]
    public void IdleDurationChangesSmoothlyInsideTheSameBand()
    {
        var family = Family(cpu: 0.1, io: 4 * 1024);

        var sixteenMinutes = LocalIdleScoreShadowPolicy.Calculate(family, TimeSpan.FromMinutes(16));
        var twentyNineMinutes = LocalIdleScoreShadowPolicy.Calculate(family, TimeSpan.FromMinutes(29));

        Assert.True(twentyNineMinutes > sixteenMinutes);
        Assert.True(twentyNineMinutes - sixteenMinutes < 2);
    }

    private static ProcessFamilySnapshot Family(double cpu, double io) => new(
        "family",
        "family",
        null,
        new[]
        {
            new ProcessSnapshot(
                10, "sample", null, null, 128L * 1024 * 1024, cpu, io,
                false, false, true,
                ProcessColdnessPolicy.Calculate(128L * 1024 * 1024, cpu, io, false, false, false))
        });
}

public class EnhancedSafetyPolicyTests
{
    [Fact]
    public void DefaultPolicyExcludesUnreliableFirstSample()
    {
        var plan = CreatePlan(OptimizationSettings.ForManual(OptimizationProfile.Ultimate));

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.UnreliableActivitySample,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void EnhancedSafetyExcludesUnreliableFirstSample()
    {
        var settings = OptimizationSettings.ForManual(OptimizationProfile.Ultimate) with { EnhancedSafety = true };

        var plan = CreatePlan(settings);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.UnreliableActivitySample,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void ReliableLowActivitySampleCanBecomeCandidate()
    {
        var plan = CreatePlan(
            OptimizationSettings.ForManual(OptimizationProfile.Ultimate),
            hasReliableActivitySample: true);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    private static OptimizationPlan CreatePlan(
        OptimizationSettings settings,
        bool hasReliableActivitySample = false)
    {
        var process = new ProcessSnapshot(
            7001,
            "first-sample",
            null,
            null,
            256L * 1024 * 1024,
            0,
            0,
            false,
            false,
            hasReliableActivitySample,
            82);
        var family = new ProcessFamilySnapshot(
            "name:first-sample",
            "first-sample",
            null,
            new[] { process });

        return new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024, 88),
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);
    }
}

public class CandidateIdleTrackerTests
{
    [Fact]
    public void AutomaticCandidateRequiresTwoConsecutiveReliableLowActivitySamples()
    {
        var tracker = new CandidateIdleTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var family = CreateFamily();

        var first = tracker.Observe(new[] { family }, settings);
        var firstPlan = CreatePlan(family, settings, first, manual: false);

        Assert.Empty(firstPlan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.IdleConfirmationPending,
            Assert.Single(firstPlan.CandidateEvaluations).ExclusionReasons);
        var second = tracker.Observe(new[] { family }, settings);
        Assert.Single(CreatePlan(family, settings, second, manual: false).Candidates);
    }

    [Fact]
    public void QuickCandidateAcceptsOneReliableLowActivitySample()
    {
        var tracker = new CandidateIdleTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            QuickCandidateSelection = true
        };
        var family = CreateFamily();

        var readiness = tracker.Observe(new[] { family }, settings);
        var plan = CreatePlan(family, settings, readiness, manual: false);

        Assert.Equal(1, readiness[7002].ConsecutiveReliableLowActivitySamples);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void QuickCandidateStillRejectsCurrentActivity()
    {
        var tracker = new CandidateIdleTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            QuickCandidateSelection = true
        };
        var family = CreateFamily(cpuPercent: settings.ActiveCpuThresholdPercent);

        var readiness = tracker.Observe(new[] { family }, settings);
        var plan = CreatePlan(family, settings, readiness, manual: false);

        Assert.Empty(plan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.CurrentCpuActivity,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void CurrentActivityImmediatelyRevokesConfirmedIdle()
    {
        var tracker = new CandidateIdleTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var idle = CreateFamily();
        _ = tracker.Observe(new[] { idle }, settings);
        var ready = tracker.Observe(new[] { idle }, settings);
        Assert.True(ready[7002].IsReady);

        var active = CreateFamily(cpuPercent: settings.ActiveCpuThresholdPercent);
        var revoked = tracker.Observe(new[] { active }, settings);
        var plan = CreatePlan(active, settings, revoked, manual: false);

        Assert.False(revoked[7002].IsReady);
        Assert.Empty(plan.Candidates);
        var reasons = Assert.Single(plan.CandidateEvaluations).ExclusionReasons;
        Assert.Contains(CandidateExclusionReason.CurrentCpuActivity, reasons);
        Assert.Contains(CandidateExclusionReason.IdleConfirmationPending, reasons);
    }

    [Fact]
    public void ReusedProcessIdMustBuildNewIdleHistory()
    {
        var tracker = new CandidateIdleTracker();
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var original = CreateFamily(startTimeFileTimeUtc: 100);
        _ = tracker.Observe(new[] { original }, settings);
        Assert.True(tracker.Observe(new[] { original }, settings)[7002].IsReady);

        var reused = CreateFamily(startTimeFileTimeUtc: 200);
        var readiness = tracker.Observe(new[] { reused }, settings)[7002];

        Assert.False(readiness.IsReady);
        Assert.Equal(1, readiness.ConsecutiveReliableLowActivitySamples);
    }

    [Fact]
    public void IndependentBackgroundProcessBuildsIdleHistoryBesideActiveSibling()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var activeSibling = new ProcessSnapshot(
            7001, "suite-main", @"F:\Apps\Suite\suite-main.exe", null,
            160L * 1024 * 1024, 12, 0, true, true, true, 10,
            StartTimeFileTimeUtc: 100);
        var idleSibling = new ProcessSnapshot(
            7002, "suite-background", @"F:\Apps\Suite\suite-background.exe", null,
            256L * 1024 * 1024, 0, 0, false, false, true, 90,
            StartTimeFileTimeUtc: 200);
        var family = new ProcessFamilySnapshot(
            "directory:f:\\apps\\suite", "suite", @"F:\Apps\Suite", new[] { activeSibling, idleSibling });
        var tracker = new CandidateIdleTracker();
        _ = tracker.Observe(new[] { family }, settings);
        var readiness = tracker.Observe(new[] { family }, settings);

        var candidate = Assert.Single(CreatePlan(family, settings, readiness, manual: false).Candidates);

        Assert.Equal(7002, Assert.Single(candidate.TargetProcesses).ProcessId);
    }

    [Fact]
    public void ManualPlanDoesNotWaitForAutomaticIdleConfirmation()
    {
        var settings = OptimizationSettings.ForManual(OptimizationProfile.Turbo);
        var family = CreateFamily();
        var readiness = new CandidateIdleTracker().Observe(new[] { family }, settings);

        Assert.Single(CreatePlan(family, settings, readiness, manual: true).Candidates);
    }

    private static OptimizationPlan CreatePlan(
        ProcessFamilySnapshot family,
        OptimizationSettings settings,
        IReadOnlyDictionary<int, CandidateIdleReadiness> readiness,
        bool manual) => new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual,
            candidateIdleReadiness: readiness);

    private static ProcessFamilySnapshot CreateFamily(
        double cpuPercent = 0,
        long? startTimeFileTimeUtc = 100)
    {
        var process = new ProcessSnapshot(
            7002, "candidate", @"F:\Apps\Candidate\candidate.exe", null,
            256L * 1024 * 1024, cpuPercent, 0, false, false, true, 90,
            StartTimeFileTimeUtc: startTimeFileTimeUtc);
        return new ProcessFamilySnapshot(
            "directory:f:\\apps\\candidate", "candidate", @"F:\Apps\Candidate", new[] { process });
    }
}

public class CandidatePreviewPolicyTests
{
    [Fact]
    public void FamilyBelowProfileWorkingSetIsNotBaseEligibleForPausedPreview()
    {
        var process = new ProcessSnapshot(
            8101, "node_repl", @"F:\Apps\Node\node_repl.exe", null,
            32L * 1024 * 1024, 0, 0, false, false, true, 90);
        var family = new ProcessFamilySnapshot("node", "node_repl", @"F:\Apps\Node", new[] { process });

        var eligible = CandidatePreviewPolicy.CreateBaseEligibleFamily(
            family,
            OptimizationSettings.For(OptimizationProfile.Turbo));

        Assert.Null(eligible);
    }

    [Fact]
    public void LifecycleBlockedFamilyRemainsVisibleBelowProfileWorkingSet()
    {
        var process = new ProcessSnapshot(
            8102, "node_repl", @"F:\Apps\Node\node_repl.exe", null,
            32L * 1024 * 1024, 0, 0, false, false, true, 90);
        var family = new ProcessFamilySnapshot("node", "node_repl", @"F:\Apps\Node", new[] { process });
        var evaluation = Evaluation(
            CandidateExclusionReason.BelowFamilyWorkingSet,
            CandidateExclusionReason.AutomaticBackoff);

        var visible = CandidatePreviewPolicy.CreateLifecycleVisibleFamily(family);

        Assert.NotNull(visible);
        Assert.True(CandidatePreviewPolicy.IsTemporarilyBlocked(
            evaluation,
            hasBaseEligibility: false,
            hasLifecycleVisibility: visible is not null));
    }

    [Fact]
    public void StableHoldBelowProfileWorkingSetDoesNotRemainInCandidatePreview()
    {
        var evaluation = Evaluation(
            CandidateExclusionReason.BelowFamilyWorkingSet,
            CandidateExclusionReason.StableStateSuppression);

        Assert.False(CandidatePreviewPolicy.IsTemporarilyBlocked(
            evaluation,
            hasBaseEligibility: false,
            hasLifecycleVisibility: true));
    }

    [Fact]
    public void ActivityOnlyExclusionRemainsVisibleAsTemporarilyBlocked()
    {
        var evaluation = Evaluation(
            CandidateExclusionReason.BelowProcessWorkingSet,
            CandidateExclusionReason.CurrentIoActivity,
            CandidateExclusionReason.ActiveProcessRelationship);

        Assert.True(CandidatePreviewPolicy.IsTemporarilyBlocked(
            evaluation,
            hasBaseEligibility: true));
    }

    [Fact]
    public void PermanentEligibilityFailureIsNotShownAsTemporarilyBlocked()
    {
        var evaluation = Evaluation(
            CandidateExclusionReason.CurrentIoActivity,
            CandidateExclusionReason.BelowFamilyWorkingSet);

        Assert.False(CandidatePreviewPolicy.IsTemporarilyBlocked(
            evaluation,
            hasBaseEligibility: true));
    }

    [Fact]
    public void FullyProtectedFamilyIsNotShownAsTemporarilyBlocked()
    {
        var evaluation = Evaluation(CandidateExclusionReason.Protected);

        Assert.False(CandidatePreviewPolicy.IsTemporarilyBlocked(
            evaluation,
            hasBaseEligibility: false));
    }

    private static CandidateEvaluation Evaluation(params CandidateExclusionReason[] reasons) =>
        new("family", "app", false, 1, 0, reasons);
}

public class SystemProcessPolicyTests
{
    [Theory]
    [InlineData("svchost")]
    [InlineData("TextInputHost")]
    [InlineData("HipsDaemon")]
    [InlineData("MsMpEng.exe")]
    public void SystemWhitelistEntriesAreExcluded(string processName)
    {
        Assert.True(SystemProcessPolicy.IsAlwaysExcluded(processName, null));
    }

    [Fact]
    public void WindowsDirectoryAloneDoesNotExcludeUnlistedProcess()
    {
        Assert.False(SystemProcessPolicy.IsAlwaysExcluded("vendorutility", @"C:\Windows\System32\vendorutility.exe"));
    }
}

public class OptimizationPlannerTests
{
    [Fact]
    public void OptimizationPlanExcludesMuseRamItself()
    {
        var family = CreateFamily(
            processId: Environment.ProcessId,
            key: "museram",
            name: "MuseRAM",
            executablePath: Environment.ProcessPath,
            workingSetBytes: 512L * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void AutomaticPlanRunsWhenAvailableMemoryReachesPressureThreshold()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Lite);
        var family = CreateFamily();
        var memory = new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75);

        var plan = new OptimizationPlanner().CreatePlan(
            memory,
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void DailyForegroundApplicationIsExcludedFromManualPlan()
    {
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { CreateFamily(isForeground: true) },
            OptimizationSettings.ForManual(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        var evaluation = Assert.Single(plan.CandidateEvaluations);
        Assert.Equal(0, evaluation.TargetProcessCount);
        Assert.Equal(0, evaluation.TargetWorkingSetBytes);
        Assert.Equal(0, evaluation.LegacyIdleScore);
        Assert.Equal(0, evaluation.IdleConfidenceScore);
        Assert.True(evaluation.TotalWorkingSetBytes > 0);
    }

    [Fact]
    public void ExtremePlanAllowsForegroundApplication()
    {
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { CreateFamily(isForeground: true) },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void EnhancedSafetyOverridesExtremeForegroundPermission()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            EnhancedSafety = true
        };
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { CreateFamily(isForeground: true) },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.Foreground,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void VisibleApplicationIsExcludedBeforeTenMinutesOutOfForegroundAndKeepsTargets()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { CreateFamily(hasVisibleWindow: true, idleScore: 80, lastForegroundAt: now.AddMinutes(-9)) },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        var evaluation = Assert.Single(plan.CandidateEvaluations);
        Assert.Contains(
            CandidateExclusionReason.VisibleWindowWait,
            evaluation.ExclusionReasons);
        Assert.Equal(1, evaluation.TargetProcessCount);
        Assert.NotEmpty(evaluation.TargetProcessIds);
        Assert.True(evaluation.TargetWorkingSetBytes > 0);
    }

    [Fact]
    public void VisibleApplicationUsesTheProfileIdleScoreAfterTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { CreateFamily(hasVisibleWindow: true, idleScore: 65, lastForegroundAt: now.AddMinutes(-10)) },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void RestoredFamilyIdleDurationSatisfiesVisibleWaitForNewChildProcesses()
    {
        var now = DateTimeOffset.UtcNow;
        var family = CreateFamily(
            hasVisibleWindow: true,
            idleScore: 80,
            lastForegroundAt: now);
        var activity = new Dictionary<string, BackgroundActivity>(StringComparer.OrdinalIgnoreCase)
        {
            [family.Key] = new BackgroundActivity(
                family.Key,
                BackgroundActivityState.Idle,
                TimeSpan.FromMinutes(14),
                TimeSpan.FromMinutes(14),
                280)
        };
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false,
            activity: activity);

        Assert.Single(plan.Candidates);
        Assert.DoesNotContain(
            CandidateExclusionReason.VisibleWindowWait,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void QuickCandidateDoesNotWaitForVisibleBackgroundWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = OptimizationSettings.For(OptimizationProfile.Lite) with
        {
            QuickCandidateSelection = true
        };
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { CreateFamily(hasVisibleWindow: true, idleScore: 80, lastForegroundAt: now) },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.Single(plan.Candidates);
        Assert.DoesNotContain(
            CandidateExclusionReason.VisibleWindowWait,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void VisibleApplicationCanBecomeCandidateAfterItsBackgroundWait()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { CreateFamily(hasVisibleWindow: true, idleScore: 64, lastForegroundAt: now.AddMinutes(-11)) },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.Single(plan.Candidates);
        Assert.DoesNotContain(
            CandidateExclusionReason.BelowIdleScore,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void ProtectedApplicationIsExcludedFromManualPlan()
    {
        const string protectedPath = @"F:\Apps\Example\example.exe";
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { CreateFamily(executablePath: protectedPath) },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(new[] { protectedPath }),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
        var evaluation = Assert.Single(plan.CandidateEvaluations);
        Assert.Contains(CandidateExclusionReason.Protected, evaluation.ExclusionReasons);
        Assert.Equal(0, evaluation.TargetProcessCount);
        Assert.Equal(0, evaluation.LegacyIdleScore);
        Assert.Equal(0, evaluation.IdleConfidenceScore);
        Assert.Equal(0, evaluation.TotalWorkingSetBytes);
    }

    [Fact]
    public void PartialProtectionKeepsTheUnselectedProcessInTheManualPlan()
    {
        const string applicationPath = @"F:\Apps\Media\media.exe";
        const string protectedPath = @"F:\Apps\Media\capture.exe";
        var protectedProcess = Assert.Single(CreateFamily(
            processId: 111,
            executablePath: protectedPath,
            name: "capture").Processes);
        var unprotectedProcess = Assert.Single(CreateFamily(
            processId: 112,
            executablePath: @"F:\Apps\Media\helper.exe",
            name: "helper").Processes);
        var family = new ProcessFamilySnapshot(
            "media",
            "Media",
            @"F:\Apps\Media",
            new[] { protectedProcess, unprotectedProcess });
        var rules = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = applicationPath,
                ProtectedExecutablePaths = new List<string> { protectedPath }
            }
        });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            rules,
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(112, Assert.Single(candidate.Family.Processes).ProcessId);
        Assert.Contains(CandidateExclusionReason.Protected, Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void RecentlyTrimmedApplicationIsExcludedDuringCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = OptimizationSettings.For(OptimizationProfile.Lite);
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { CreateFamily() },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset> { [101] = now - settings.ProcessCooldown / 2 },
            now,
            manual: true);

        Assert.False(plan.ShouldRun);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void ReusedPidDoesNotInheritProcessCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var family = CreateFamily(startTimeFileTimeUtc: 2002);
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset> { [101] = now.AddSeconds(-1) },
            now,
            manual: true,
            lastTrimProcessStartTimes: new Dictionary<int, long> { [101] = 1001 });

        Assert.True(plan.ShouldRun);
        Assert.DoesNotContain(
            CandidateExclusionReason.ProcessCooldown,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void ProtectedRootDoesNotProtectChildInUnrelatedApplicationFamily()
    {
        const string protectedPath = @"F:\Games\Protected\game.exe";
        var root = CreateFamily(
            processId: 100,
            key: "protected-root",
            name: "game",
            executablePath: protectedPath);
        var child = CreateFamily(
            processId: 101,
            parentProcessId: 100,
            key: "separate-child-family",
            name: "helper",
            executablePath: @"F:\Shared\Helper\helper.exe");
        var unprotected = CreateFamily(
            processId: 102,
            key: "unprotected",
            name: "notes",
            executablePath: @"F:\Apps\Notes\notes.exe");

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { root, child, unprotected },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(new[] { protectedPath }),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(
            new[] { "separate-child-family", "unprotected" },
            plan.Candidates.Select(candidate => candidate.Family.Key).Order());
    }

    [Fact]
    public void SevereMemoryPressureExpandsCandidateLimit()
    {
        var families = Enumerable.Range(1, 6)
            .Select(index => CreateFamily(
                processId: 200 + index,
                key: $"app-{index}",
                name: $"app{index}",
                executablePath: $@"F:\Apps\App{index}\app{index}.exe"))
            .ToArray();

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            families,
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(4, plan.Candidates.Count);
    }

    [Fact]
    public void EquivalentCandidatesUseStableFamilyKeyOrdering()
    {
        var lowIo = CreateFamily(
            processId: 301,
            key: "low-io",
            name: "lowio",
            executablePath: @"F:\Apps\LowIo\lowio.exe",
            ioBytesPerSecond: 0);
        var higherIo = CreateFamily(
            processId: 302,
            key: "higher-io",
            name: "higherio",
            executablePath: @"F:\Apps\HigherIo\higherio.exe",
            ioBytesPerSecond: 3d * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { higherIo, lowIo },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(new[] { "higher-io", "low-io" }, plan.Candidates.Select(candidate => candidate.Family.Key));
    }

    [Fact]
    public void PotentialReleaseSortsBeforeIdleConfidence()
    {
        var highConfidenceSmall = CreateFamily(
            processId: 303,
            key: "high-confidence",
            workingSetBytes: 64L * 1024 * 1024,
            cpuPercent: 0,
            idleScore: 90);
        var lowerConfidenceLarge = CreateFamily(
            processId: 304,
            key: "large-benefit",
            workingSetBytes: 1024L * 1024 * 1024,
            cpuPercent: 1.5,
            idleScore: 90);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { lowerConfidenceLarge, highConfidenceSmall },
            OptimizationSettings.For(OptimizationProfile.Ultimate),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(new[] { "large-benefit", "high-confidence" }, plan.Candidates.Select(candidate => candidate.Family.Key));
        Assert.True(plan.Candidates[0].IdleConfidenceScore < plan.Candidates[1].IdleConfidenceScore);
        Assert.True(plan.Candidates[0].PotentialReleaseBytes > plan.Candidates[1].PotentialReleaseBytes);
    }

    [Fact]
    public void IdleScoreDoesNotControlCandidateEligibility()
    {
        var family = CreateFamily(idleScore: 20);
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with { MinimumIdleScore = 50 };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(100, family.IdleConfidenceScore);
        Assert.Single(plan.Candidates);
        Assert.DoesNotContain(
            CandidateExclusionReason.BelowIdleScore,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void BenefitLearningKeepsProfileActivityEligibilityAuthoritative()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            IntelligentCandidateSelection = true
        };
        var active = CreateFamily(ioBytesPerSecond: 5d * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { active },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void BenefitLearningDoesNotOverrideManualOrPreviewIntent()
    {
        var settings = OptimizationSettings.ForManual(OptimizationProfile.Ultimate) with
        {
            IntelligentCandidateSelection = true
        };
        var active = CreateFamily(ioBytesPerSecond: 5d * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { active },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        Assert.Single(plan.Candidates);

        var preview = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { active },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            intelligentPreview: true);

        Assert.Single(preview.Candidates);
    }

    [Fact]
    public void BenefitLearningUsesPotentialReleaseTieBreakWithoutHistory()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            IntelligentCandidateSelection = true
        };
        var smaller = CreateFamily(processId: 401, key: "small", workingSetBytes: 128L * 1024 * 1024, idleScore: 100);
        var larger = CreateFamily(processId: 402, key: "large", workingSetBytes: 1024L * 1024 * 1024, idleScore: 50);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { smaller, larger },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(new[] { "large", "small" }, plan.Candidates.Select(candidate => candidate.Family.Key));
        Assert.Equal(plan.Candidates[0].IdleConfidenceScore, plan.Candidates[1].IdleConfidenceScore);
    }

    [Fact]
    public void IntelligentSelectionUsesObservedYieldAndRetentionBeforeRawSize()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            IntelligentCandidateSelection = true
        };
        var larger = CreateFamily(processId: 411, key: "large", workingSetBytes: 1024L * 1024 * 1024);
        var smaller = CreateFamily(processId: 412, key: "small", workingSetBytes: 512L * 1024 * 1024);
        var outcomes = new Dictionary<string, double> { ["large"] = 0.1, ["small"] = 0.9 };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { larger, smaller },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            outcomeMultipliers: outcomes,
            learningConfidences: new Dictionary<string, double> { ["large"] = 1, ["small"] = 1 });

        Assert.Equal(new[] { "small", "large" }, plan.Candidates.Select(candidate => candidate.Family.Key));
    }

    [Fact]
    public void ConfidenceAdjustedLearningStillPrioritizesExpectedRetainedRelease()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            IntelligentCandidateSelection = true
        };
        var compositeLeader = CreateFamily(processId: 421, key: "composite", workingSetBytes: 256L * 1024 * 1024, idleScore: 100);
        var benefitLeader = CreateFamily(
            processId: 422,
            key: "benefit",
            workingSetBytes: 1024L * 1024 * 1024,
            cpuPercent: 1.5,
            idleScore: 30);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { benefitLeader, compositeLeader },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            outcomeMultipliers: new Dictionary<string, double> { ["benefit"] = 0.9, ["composite"] = 0.2 },
            learningConfidences: new Dictionary<string, double> { ["benefit"] = 0.2, ["composite"] = 0.2 });

        Assert.Equal("benefit", plan.Candidates[0].Family.Key);
    }

    [Fact]
    public void AutomaticPlanDoesNotUseDeepReleaseObservationState()
    {
        var family = CreateFamily();
        var activity = new Dictionary<string, BackgroundActivity>
        {
            [family.Key] = new(
                family.Key,
                BackgroundActivityState.Observing,
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(20),
                4)
        };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            activity);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void AutomaticPlanAcceptsSustainedIdleApplication()
    {
        var family = CreateFamily();
        var activity = new Dictionary<string, BackgroundActivity>
        {
            [family.Key] = new(
                family.Key,
                BackgroundActivityState.Idle,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2),
                12)
        };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            activity);

        Assert.True(plan.ShouldRun);
        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void SustainedIdleApplicationIsSuggestedForDeepRelease()
    {
        var family = CreateFamily();
        var tracker = new BackgroundActivityTracker();
        var startedAt = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, BackgroundActivity> activity =
            new Dictionary<string, BackgroundActivity>();

        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            activity = tracker.Observe(
                new[] { family },
                startedAt + TimeSpan.FromSeconds(15 * sample));
        }

        var candidate = Assert.Single(BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { family },
            activity,
            new ProtectionRules()));
        Assert.Equal(BackgroundActivityState.Idle, candidate.Activity.State);
        Assert.True(candidate.IsSuggested);
    }

    [Fact]
    public void IsolatedMinimizedActivityPulseDoesNotResetEstablishedIdleTime()
    {
        var tracker = new BackgroundActivityTracker();
        var startedAt = DateTimeOffset.UtcNow;
        var idle = CreateFamily(key: "edge", hasMinimizedWindow: true);
        IReadOnlyDictionary<string, BackgroundActivity> activity = new Dictionary<string, BackgroundActivity>();
        for (var sample = 0; sample < 5; sample++)
        {
            activity = tracker.Observe(new[] { idle }, startedAt + TimeSpan.FromSeconds(15 * sample));
        }
        Assert.Equal(BackgroundActivityState.Idle, activity[idle.Key].State);

        var pulse = CreateFamily(key: "edge", hasMinimizedWindow: true, cpuPercent: 3);
        activity = tracker.Observe(new[] { pulse }, startedAt + TimeSpan.FromSeconds(63));
        Assert.Equal(BackgroundActivityState.Working, activity[idle.Key].State);

        activity = tracker.Observe(new[] { idle }, startedAt + TimeSpan.FromSeconds(66));
        Assert.Equal(BackgroundActivityState.Idle, activity[idle.Key].State);
    }

    [Fact]
    public void ConsecutiveMinimizedActivitySamplesResetIdleTime()
    {
        var tracker = new BackgroundActivityTracker();
        var startedAt = DateTimeOffset.UtcNow;
        var idle = CreateFamily(key: "edge", hasMinimizedWindow: true);
        for (var sample = 0; sample < 5; sample++)
        {
            _ = tracker.Observe(new[] { idle }, startedAt + TimeSpan.FromSeconds(15 * sample));
        }

        var active = CreateFamily(key: "edge", hasMinimizedWindow: true, cpuPercent: 3);
        _ = tracker.Observe(new[] { active }, startedAt + TimeSpan.FromSeconds(63));
        _ = tracker.Observe(new[] { active }, startedAt + TimeSpan.FromSeconds(66));
        var activity = tracker.Observe(new[] { idle }, startedAt + TimeSpan.FromSeconds(69));

        Assert.NotEqual(BackgroundActivityState.Idle, activity[idle.Key].State);
        Assert.Equal(TimeSpan.FromSeconds(3), activity[idle.Key].IdleFor);
    }

    [Fact]
    public void DeepReleaseExcludesCurrentProcess()
    {
        var family = CreateFamily(processId: Environment.ProcessId, name: "MuseRAM", key: "museram");

        var candidates = BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { family },
            new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules());

        Assert.Empty(candidates);
    }

    [Fact]
    public void DeepReleaseDoesNotExcludeApplicationsOnlyBecauseTheyAreGames()
    {
        var family = CreateFamily(name: "steam", key: "steam", executablePath: @"F:\Games\Steam\steam.exe");

        var candidates = BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { family },
            new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules());

        Assert.Equal("steam", Assert.Single(candidates).Family.Key);
    }

    [Fact]
    public void VisibleAncestorInSeparateFamilyDoesNotOverrideChildActivity()
    {
        var parent = CreateFamily(
            processId: 501,
            key: "visible-parent",
            name: "mainapp",
            executablePath: @"F:\Apps\Main\mainapp.exe",
            hasVisibleWindow: true);
        var child = CreateFamily(
            processId: 502,
            parentProcessId: 501,
            key: "separate-plugin",
            name: "plugin",
            executablePath: @"F:\Plugins\plugin.exe");

        var activity = new BackgroundActivityTracker().Observe(
            new[] { parent, child },
            DateTimeOffset.UtcNow);

        Assert.Equal(BackgroundActivityState.Observing, activity[child.Key].State);
    }

    [Fact]
    public void DeepReleaseRequiresThirtyTwoMiBForVisibleApplications()
    {
        var smallVisible = CreateFamily(
            processId: 601,
            key: "small-visible",
            name: "editor",
            executablePath: @"F:\Apps\Editor\editor.exe",
            hasVisibleWindow: true,
            workingSetBytes: 20L * 1024 * 1024);

        var candidates = BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { smallVisible },
            new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules());

        Assert.Empty(candidates);
    }

    [Fact]
    public void DeepReleaseListsLargeVisibleApplicationWithoutSuggestingIt()
    {
        var visible = CreateFamily(
            processId: 602,
            key: "visible",
            name: "editor",
            executablePath: @"F:\Apps\Editor\editor.exe",
            hasVisibleWindow: true,
            workingSetBytes: 40L * 1024 * 1024);

        var candidate = Assert.Single(BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { visible },
            new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules()));

        Assert.False(candidate.IsSuggested);
    }

    [Fact]
    public void AutomaticBackoffSkipsFamilyButManualOptimizationCanOverrideIt()
    {
        var family = CreateFamily(key: "rebounding");
        var planner = new OptimizationPlanner();
        var memory = new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { family.Key };

        var automatic = planner.CreatePlan(
            memory,
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            automaticBackoffFamilies: blocked);
        var manual = planner.CreatePlan(
            memory,
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            automaticBackoffFamilies: blocked);

        Assert.Empty(automatic.Candidates);
        Assert.Single(manual.Candidates);
    }

    [Fact]
    public void ActiveProcessDoesNotBlockIdleSiblingInSameApplicationFamily()
    {
        var active = new ProcessSnapshot(
            801,
            "suite-main",
            @"F:\Apps\Suite\suite-main.exe",
            null,
            160L * 1024 * 1024,
            12,
            0,
            true,
            true,
            true,
            10);
        var idle = new ProcessSnapshot(
            802,
            "suite-helper",
            @"F:\Apps\Suite\suite-helper.exe",
            801,
            256L * 1024 * 1024,
            0,
            0,
            false,
            false,
            true,
            90);
        var family = new ProcessFamilySnapshot(
            "directory:f:\\apps\\suite",
            "suite-main",
            @"F:\Apps\Suite",
            new[] { active, idle });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(802, Assert.Single(candidate.TargetProcesses).ProcessId);
    }

    [Fact]
    public void DisablingIndependentBackgroundOptimizationSkipsFamilyWithActiveProcess()
    {
        var active = new ProcessSnapshot(
            805, "suite-main", @"F:\Apps\Suite\suite-main.exe", null,
            160L * 1024 * 1024, 12, 0, true, true, true, 10);
        var independent = new ProcessSnapshot(
            806, "suite-cache", @"F:\Apps\Suite\suite-cache.exe", null,
            256L * 1024 * 1024, 0, 0, false, false, true, 90);
        var family = new ProcessFamilySnapshot(
            "directory:f:\\apps\\suite", "suite-main", @"F:\Apps\Suite", new[] { active, independent });
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            AllowIndependentBackgroundProcessTrim = false
        };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family }, settings, new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(), DateTimeOffset.UtcNow, manual: false);

        Assert.Empty(plan.Candidates);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(0, 4)]
    public void ConfiguredCpuAndIoThresholdsExcludeActiveProcesses(double cpuPercent, double ioMiBPerSecond)
    {
        var family = CreateFamily(cpuPercent: cpuPercent, ioBytesPerSecond: ioMiBPerSecond * 1024 * 1024);
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            ActiveCpuThresholdPercent = 8,
            ActiveIoThresholdBytesPerSecond = 4d * 1024 * 1024
        };

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family }, settings, new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(), DateTimeOffset.UtcNow, manual: false);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void IdleVisibleMultiProcessApplicationCanBecomeATurboCandidateAfterFiveMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var visibleParent = new ProcessSnapshot(
            803, "msedge", @"F:\Apps\Edge\msedge.exe", null,
            160L * 1024 * 1024, 0, 0, false, true, true, 65,
            LastForegroundAt: now.AddMinutes(-6));
        var backgroundChild = new ProcessSnapshot(
            804, "msedge", @"F:\Apps\Edge\msedge.exe", 803,
            256L * 1024 * 1024, 0, 0, false, false, true, 90);
        var family = new ProcessFamilySnapshot(
            "directory:f:\\apps\\edge",
            "msedge",
            @"F:\Apps\Edge",
            new[] { visibleParent, backgroundChild });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.Equal(new[] { 803, 804 }, Assert.Single(plan.Candidates).TargetProcesses.Select(process => process.ProcessId));
    }

    [Fact]
    public void EdgeLikeGroupedProcessTreeExplainsRejectionAfterVisibleWindowWait()
    {
        var now = DateTimeOffset.UtcNow;
        var processes = new[]
        {
            new ProcessSnapshot(
                803, "msedge", @"F:\Apps\Edge\msedge.exe", null,
                160L * 1024 * 1024, 12, 0, false, true, true, 10,
                LastForegroundAt: now.AddMinutes(-3)),
            new ProcessSnapshot(
                804, "msedge", @"F:\Apps\Edge\msedge.exe", 803,
                256L * 1024 * 1024, 0, 0, false, false, true, 90)
        };
        var family = Assert.Single(ApplicationFamilyGrouper.Group(processes));

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            now,
            manual: false);

        Assert.Empty(plan.Candidates);
        var evaluation = Assert.Single(plan.CandidateEvaluations);
        Assert.Contains(CandidateExclusionReason.VisibleWindowWait, evaluation.ExclusionReasons);
        Assert.Contains(CandidateExclusionReason.CurrentCpuActivity, evaluation.ExclusionReasons);
        Assert.Contains(CandidateExclusionReason.ActiveProcessRelationship, evaluation.ExclusionReasons);
    }

    [Fact]
    public void ActiveParentKeepsIdleNonAuxiliaryChildOutOfTrimTargets()
    {
        var active = new ProcessSnapshot(
            811, "suite-main", @"F:\Apps\Suite\suite-main.exe", null,
            160L * 1024 * 1024, 12, 0, true, true, true, 10);
        var requiredChild = new ProcessSnapshot(
            812, "suite-engine", @"F:\Apps\Suite\suite-engine.exe", 811,
            256L * 1024 * 1024, 0, 0, false, false, true, 90);
        var unrelatedIdle = new ProcessSnapshot(
            813, "suite-cache", @"F:\Apps\Suite\suite-cache.exe", null,
            256L * 1024 * 1024, 0, 0, false, false, true, 90);
        var family = new ProcessFamilySnapshot(
            "directory:f:\\apps\\suite",
            "suite-main",
            @"F:\Apps\Suite",
            new[] { active, requiredChild, unrelatedIdle });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(813, Assert.Single(candidate.TargetProcesses).ProcessId);
    }

    [Fact]
    public void ActiveParentInAnotherFamilyKeepsRequiredChildOutOfCandidates()
    {
        var parent = CreateFamily(
            processId: 821,
            key: "launcher",
            name: "launcher",
            executablePath: @"F:\Apps\Launcher\launcher.exe",
            isForeground: true,
            idleScore: 10,
            workingSetBytes: 160L * 1024 * 1024);
        var child = CreateFamily(
            processId: 822,
            parentProcessId: 821,
            key: "engine",
            name: "suite-engine",
            executablePath: @"F:\Apps\Engine\suite-engine.exe",
            idleScore: 90,
            workingSetBytes: 256L * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { parent, child },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void TurboDoesNotExcludeSteamByProcessName()
    {
        var family = CreateFamily(
            key: "steam",
            name: "steam",
            executablePath: @"F:\Games\Steam\steam.exe",
            idleScore: 90,
            workingSetBytes: 256L * 1024 * 1024);

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void ScheduledStylePlanHonorsBackoffWithManualCandidateWidth()
    {
        var family = CreateFamily();
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            automaticBackoffFamilies: new HashSet<string> { family.Key },
            candidateIdleReadiness: new Dictionary<int, CandidateIdleReadiness>
            {
                [101] = new(101, 2, true)
            },
            enforceUnattendedSafety: true);

        Assert.Empty(plan.Candidates);
        Assert.Contains(
            CandidateExclusionReason.AutomaticBackoff,
            Assert.Single(plan.CandidateEvaluations).ExclusionReasons);
    }

    [Fact]
    public void ScheduledStylePlanRequiresIdleConfirmationWithoutRequiringMemoryPressure()
    {
        var family = CreateFamily();
        var planner = new OptimizationPlanner();
        var arguments = new Dictionary<int, CandidateIdleReadiness>
        {
            [101] = new(101, 1, false)
        };
        var waiting = planner.CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            candidateIdleReadiness: arguments,
            enforceUnattendedSafety: true);

        arguments[101] = new(101, 2, true);
        var ready = planner.CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            candidateIdleReadiness: arguments,
            enforceUnattendedSafety: true);

        Assert.Empty(waiting.Candidates);
        Assert.Contains(
            CandidateExclusionReason.IdleConfirmationPending,
            Assert.Single(waiting.CandidateEvaluations).ExclusionReasons);
        Assert.Single(ready.Candidates);
        Assert.NotEqual(OptimizationPlanOutcome.LowMemoryPressure, ready.Outcome);
    }

    [Fact]
    public void UnattendedPlanWaitsForPendingReboundObservationWhileManualPlanCanOverride()
    {
        var family = CreateFamily();
        var pending = new HashSet<string> { family.Key };
        var readiness = new Dictionary<int, CandidateIdleReadiness>
        {
            [101] = new(101, 2, true)
        };
        var planner = new OptimizationPlanner();
        var unattended = planner.CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            candidateIdleReadiness: readiness,
            enforceUnattendedSafety: true,
            pendingReboundObservationFamilies: pending);
        var manual = planner.CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            new[] { family },
            OptimizationSettings.ForManual(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            pendingReboundObservationFamilies: pending);

        Assert.Empty(unattended.Candidates);
        Assert.Contains(
            CandidateExclusionReason.ReboundObservationPending,
            Assert.Single(unattended.CandidateEvaluations).ExclusionReasons);
        Assert.Single(manual.Candidates);
    }

    [Fact]
    public void SeverePressureDetectionIgnoresTheOptimizationPressureOverride()
    {
        var memory = new MemorySnapshot(
            16UL * 1024 * 1024 * 1024,
            1UL * 1024 * 1024 * 1024,
            94);
        var settings = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            IgnoreMemoryPressureThreshold = true
        };

        Assert.False(OptimizationPlanner.IsSevereMemoryPressure(memory, settings));
        Assert.True(OptimizationPlanner.IsSevereMemoryPressureRegardlessOfOptimizationOverride(
            memory,
            settings));
    }

    private static ProcessFamilySnapshot CreateFamily(
        bool isForeground = false,
        string? executablePath = @"F:\Apps\Example\example.exe",
        bool hasVisibleWindow = false,
        int processId = 101,
        int? parentProcessId = null,
        string key = "example",
        string name = "example",
        double cpuPercent = 0,
        double ioBytesPerSecond = 0,
        double idleScore = 100,
        long workingSetBytes = 512L * 1024 * 1024,
        bool hasMinimizedWindow = false,
        DateTimeOffset? lastForegroundAt = null,
        long? startTimeFileTimeUtc = null)
    {
        var process = new ProcessSnapshot(
            processId,
            name,
            executablePath,
            parentProcessId,
            workingSetBytes,
            cpuPercent,
            ioBytesPerSecond,
            isForeground,
            hasVisibleWindow,
            true,
            idleScore,
            StartTimeFileTimeUtc: startTimeFileTimeUtc,
            HasMinimizedWindow: hasMinimizedWindow,
            LastForegroundAt: lastForegroundAt);
        return new ProcessFamilySnapshot(key, name, executablePath is null ? null : Path.GetDirectoryName(executablePath), new[] { process });
    }
}

public class OptimizationSettingsTests
{
    [Fact]
    public void LegacyGamingProtectionSettingIsDisabledWhenCustomProfileIsNormalized()
    {
        var profile = CustomProfilePolicy.Normalize(new CustomOptimizationProfile
        {
            Name = "Legacy",
            BaseProfile = OptimizationProfile.Turbo,
            Settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
            {
                ProtectGamingProcesses = true
            }
        });

        Assert.False(profile.Settings.ProtectGamingProcesses);
    }

    [Fact]
    public void ProfilesUseEstablishedRuntimeParameters()
    {
        var lite = OptimizationSettings.For(OptimizationProfile.Lite);
        var turbo = OptimizationSettings.For(OptimizationProfile.Turbo);
        var ultimate = OptimizationSettings.For(OptimizationProfile.Ultimate);

        Assert.Equal(2, lite.MaxApplications);
        Assert.Equal(280L * 1024 * 1024, lite.MinimumFamilyWorkingSetBytes);
        Assert.Equal(65, lite.MinimumIdleScore);
        Assert.Equal(TimeSpan.FromMinutes(10), lite.VisibleWindowIdleDelay);
        Assert.False(lite.ProtectGamingProcesses);
        Assert.Equal(4, lite.ActiveCpuThresholdPercent);
        Assert.Equal(2d * 1024 * 1024, lite.ActiveIoThresholdBytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(150), lite.AutoCooldown);
        Assert.True(lite.AllowIndependentBackgroundProcessTrim);

        Assert.Equal(7, turbo.MaxApplications);
        Assert.Equal(88L * 1024 * 1024, turbo.MinimumFamilyWorkingSetBytes);
        Assert.Equal(TimeSpan.FromSeconds(18), turbo.ProcessCooldown);
        Assert.Equal(TimeSpan.FromMinutes(5), turbo.VisibleWindowIdleDelay);
        Assert.False(turbo.ProtectGamingProcesses);
        Assert.Equal(8, turbo.ActiveCpuThresholdPercent);
        Assert.Equal(4d * 1024 * 1024, turbo.ActiveIoThresholdBytesPerSecond);
        Assert.True(turbo.AllowIndependentBackgroundProcessTrim);

        Assert.Equal(0, ultimate.MaxApplications);
        Assert.Equal(48L * 1024 * 1024, ultimate.MinimumFamilyWorkingSetBytes);
        Assert.True(ultimate.IgnoreMemoryPressureThreshold);
        Assert.True(ultimate.AllowForegroundProcessTrim);
        Assert.Equal(30, ultimate.MinimumIdleScore);
        Assert.Equal(TimeSpan.FromSeconds(10), ultimate.ProcessCooldown);
        Assert.Equal(TimeSpan.FromSeconds(60), ultimate.AutoCooldown);
        Assert.Equal(TimeSpan.Zero, ultimate.VisibleWindowIdleDelay);
        Assert.Equal(12, ultimate.ActiveCpuThresholdPercent);
        Assert.Equal(8d * 1024 * 1024, ultimate.ActiveIoThresholdBytesPerSecond);
        Assert.True(ultimate.AllowIndependentBackgroundProcessTrim);
    }

    [Fact]
    public void ManualProfilesUseDedicatedOverrides()
    {
        var lite = OptimizationSettings.ForManual(OptimizationProfile.Lite);
        var turbo = OptimizationSettings.ForManual(OptimizationProfile.Turbo);

        Assert.Equal(3, lite.MaxApplications);
        Assert.Equal(128L * 1024 * 1024, lite.MinimumFamilyWorkingSetBytes);
        Assert.Equal(53, lite.MinimumIdleScore);
        Assert.Equal(TimeSpan.FromSeconds(12), lite.ProcessCooldown);

        Assert.Equal(10, turbo.MaxApplications);
        Assert.Equal(64L * 1024 * 1024, turbo.MinimumFamilyWorkingSetBytes);
        Assert.Equal(35, turbo.MinimumIdleScore);
        Assert.Equal(TimeSpan.FromSeconds(12), turbo.ProcessCooldown);
    }
}

public class ProcessWindowPolicyTests
{
    [Fact]
    public void IncompleteWindowEnumerationFailsTheSamplingRoundClosed()
    {
        var partialWindowStates = new Dictionary<int, ProcessWindowState>();

        Assert.Throws<InvalidOperationException>(() =>
            ProcessWindowPolicy.RequireReliableCapture(
                captureSucceeded: false,
                partialWindowStates));
        Assert.Same(
            partialWindowStates,
            ProcessWindowPolicy.RequireReliableCapture(
                captureSucceeded: true,
                partialWindowStates));
    }

    [Fact]
    public void MinimizedWindowIsNotClassifiedAsActuallyVisible()
    {
        var state = ProcessWindowPolicy.Classify(isVisible: true, isMinimized: true);

        Assert.False(state.HasVisibleWindow);
        Assert.True(state.HasMinimizedWindow);
    }

    [Fact]
    public void WindowOnAnotherVirtualDesktopIsNotPresentOnCurrentDesktop()
    {
        var state = ProcessWindowPolicy.Classify(
            isVisible: true,
            isMinimized: false,
            isOnCurrentVirtualDesktop: false);

        Assert.False(state.HasVisibleWindow);
        Assert.False(state.HasMinimizedWindow);
    }

    [Fact]
    public void WindowStatesFromOneProcessAreMergedAcrossOneEnumeration()
    {
        var visible = ProcessWindowPolicy.Classify(isVisible: true, isMinimized: false);
        var minimized = ProcessWindowPolicy.Classify(isVisible: true, isMinimized: true);

        var state = ProcessWindowPolicy.Merge(visible, minimized);

        Assert.True(state.HasVisibleWindow);
        Assert.True(state.HasMinimizedWindow);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void NonPresentingAuxiliaryWindowsAreIgnored(
        bool isCloaked,
        bool isToolWindow,
        bool isFullyTransparent)
    {
        var state = ProcessWindowPolicy.Classify(
            isVisible: true,
            isMinimized: false,
            isCloaked,
            isToolWindow,
            isFullyTransparent);

        Assert.False(state.HasVisibleWindow);
        Assert.False(state.HasMinimizedWindow);
    }

    [Fact]
    public void SustainedLowActivityVisibleWindowCanBecomeIdle()
    {
        var tracker = new BackgroundActivityTracker();
        var startedAt = DateTimeOffset.UtcNow;
        var visible = ActivityFamily("visible-idle", cpuPercent: 0);
        IReadOnlyDictionary<string, BackgroundActivity> activity =
            new Dictionary<string, BackgroundActivity>();

        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            activity = tracker.Observe(
                new[] { visible },
                startedAt + TimeSpan.FromSeconds(15 * sample));
        }

        Assert.Equal(BackgroundActivityState.Idle, activity[visible.Key].State);
        Assert.Equal(TimeSpan.FromMinutes(1), activity[visible.Key].IdleFor);
    }

    [Fact]
    public void ReliableActivityKeepsVisibleWindowWorking()
    {
        var tracker = new BackgroundActivityTracker();
        var active = ActivityFamily("visible-active", cpuPercent: 2);

        var activity = tracker.Observe(new[] { active }, DateTimeOffset.UtcNow);

        Assert.Equal(BackgroundActivityState.Working, activity[active.Key].State);
    }

    [Fact]
    public void ProfileActivityThresholdAllowsLowerActivityToAccumulateIdleTime()
    {
        var tracker = new BackgroundActivityTracker();
        var startedAt = DateTimeOffset.UtcNow;
        var family = ActivityFamily("turbo-low-activity", cpuPercent: 3);
        IReadOnlyDictionary<string, BackgroundActivity> activity =
            new Dictionary<string, BackgroundActivity>();

        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            activity = tracker.Observe(
                new[] { family },
                startedAt + TimeSpan.FromSeconds(15 * sample),
                activeCpuThresholdPercent: 8,
                activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);
        }

        Assert.Equal(BackgroundActivityState.Idle, activity[family.Key].State);
        Assert.Equal(TimeSpan.FromMinutes(1), activity[family.Key].IdleFor);
    }

    [Fact]
    public void ChangingActivityThresholdPreservesUserUnusedObservation()
    {
        var tracker = new BackgroundActivityTracker(resetIdleOnBackgroundActivity: false);
        var startedAt = DateTimeOffset.UtcNow;
        var family = ActivityFamily("threshold-change", cpuPercent: 0);
        IReadOnlyDictionary<string, BackgroundActivity> activity =
            new Dictionary<string, BackgroundActivity>();

        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            activity = tracker.Observe(
                new[] { family },
                startedAt + TimeSpan.FromSeconds(15 * sample),
                activeCpuThresholdPercent: 8,
                activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);
        }
        Assert.Equal(BackgroundActivityState.Idle, activity[family.Key].State);

        activity = tracker.Observe(
            new[] { family },
            startedAt + TimeSpan.FromSeconds(75),
            activeCpuThresholdPercent: 25,
            activeIoThresholdBytesPerSecond: 16d * 1024 * 1024);

        Assert.Equal(BackgroundActivityState.Idle, activity[family.Key].State);
        Assert.Equal(TimeSpan.FromSeconds(75), activity[family.Key].IdleFor);
        Assert.Equal(BackgroundActivityTracker.MinimumSamples + 1, activity[family.Key].SampleCount);
    }

    [Fact]
    public void BackgroundIoDoesNotResetUserUnusedTime()
    {
        var tracker = new BackgroundActivityTracker(resetIdleOnBackgroundActivity: false);
        var startedAt = DateTimeOffset.UtcNow;
        var idle = ActivityFamily("background-io", cpuPercent: 0);
        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            _ = tracker.Observe(
                new[] { idle },
                startedAt + TimeSpan.FromSeconds(15 * sample),
                activeCpuThresholdPercent: 8,
                activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);
        }
        var active = ActivityFamily("background-io", cpuPercent: 9);
        var working = tracker.Observe(
            new[] { active },
            startedAt + TimeSpan.FromSeconds(75),
            activeCpuThresholdPercent: 8,
            activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);

        Assert.Equal(BackgroundActivityState.Working, working[idle.Key].State);
        Assert.Equal(TimeSpan.FromSeconds(75), working[idle.Key].IdleFor);

        var quiet = tracker.Observe(
            new[] { idle },
            startedAt + TimeSpan.FromSeconds(90),
            activeCpuThresholdPercent: 8,
            activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);

        Assert.Equal(BackgroundActivityState.Idle, quiet[idle.Key].State);
        Assert.Equal(TimeSpan.FromSeconds(90), quiet[idle.Key].IdleFor);
    }

    [Fact]
    public void RestoredActivityProgressResumesFromSavedElapsedTime()
    {
        var tracker = new BackgroundActivityTracker(resetIdleOnBackgroundActivity: false);
        var restoredAt = DateTimeOffset.UtcNow;
        tracker.RestoreProgress(
            "restored-idle",
            observedFor: TimeSpan.FromMinutes(4),
            idleFor: TimeSpan.FromMinutes(3),
            samples: 12,
            restoredAt);

        var activity = tracker.Observe(
            new[] { ActivityFamily("restored-idle", cpuPercent: 0) },
            restoredAt + TimeSpan.FromSeconds(15),
            activeCpuThresholdPercent: 8,
            activeIoThresholdBytesPerSecond: 4d * 1024 * 1024);

        var restored = activity["restored-idle"];
        Assert.Equal(BackgroundActivityState.Idle, restored.State);
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(15), restored.ObservedFor);
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(15), restored.IdleFor);
        Assert.Equal(13, restored.SampleCount);
    }

    [Fact]
    public void SustainedIdleMinimizedApplicationCanBecomeATurboCandidate()
    {
        var process = new ProcessSnapshot(
            901,
            "browser",
            @"F:\Apps\Browser\browser.exe",
            null,
            512L * 1024 * 1024,
            0,
            0,
            false,
            false,
            true,
            90,
            HasMinimizedWindow: true);
        var family = new ProcessFamilySnapshot("browser", "browser", @"F:\Apps\Browser", new[] { process });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            activity: new Dictionary<string, BackgroundActivity>
            {
                ["browser"] = new("browser", BackgroundActivityState.Idle, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10)
            });

        Assert.Single(plan.Candidates);
    }

    [Theory]
    [InlineData(BackgroundActivityState.Observing)]
    [InlineData(BackgroundActivityState.Working)]
    public void MinimizedApplicationEligibilityDoesNotDependOnDeepReleaseActivity(BackgroundActivityState state)
    {
        var process = new ProcessSnapshot(
            902,
            "browser",
            @"F:\Apps\Browser\browser.exe",
            null,
            512L * 1024 * 1024,
            0,
            0,
            false,
            false,
            true,
            90,
            HasMinimizedWindow: true);
        var family = new ProcessFamilySnapshot("browser", "browser", @"F:\Apps\Browser", new[] { process });

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            activity: new Dictionary<string, BackgroundActivity>
            {
                ["browser"] = new("browser", state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 3)
            });

        Assert.Single(plan.Candidates);
    }

    private static ProcessFamilySnapshot ActivityFamily(string key, double cpuPercent)
    {
        var process = new ProcessSnapshot(
            903,
            key,
            $@"F:\Apps\{key}\{key}.exe",
            null,
            512L * 1024 * 1024,
            cpuPercent,
            0,
            false,
            true,
            true,
            90);
        return new ProcessFamilySnapshot(key, key, $@"F:\Apps\{key}", new[] { process });
    }
}

public class OptimizationReboundTrackerTests
{
    [Fact]
    public void PositiveNetGainTracksReboundForTwoMinutes()
    {
        var tracker = new OptimizationReboundTracker();
        var startedAt = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        var before = Memory(4_000);
        var after = Memory(5_000);

        tracker.Begin(before, after, startedAt);

        Assert.True(tracker.IsTracking(startedAt));
        Assert.Equal(50d, tracker.Observe(Memory(4_500), startedAt.AddSeconds(60)));
        Assert.Equal(100d, tracker.Observe(Memory(3_900), startedAt.AddSeconds(90)));
        Assert.Equal(0d, tracker.Observe(Memory(5_200), startedAt.AddSeconds(100)));
    }

    [Fact]
    public void ExpiredTrackingKeepsLastObservedRate()
    {
        var tracker = new OptimizationReboundTracker();
        var startedAt = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        tracker.Begin(Memory(4_000), Memory(5_000), startedAt);
        Assert.Equal(40d, tracker.Observe(Memory(4_600), startedAt.AddSeconds(119)));

        var rate = tracker.Observe(Memory(4_000), startedAt.AddSeconds(121));

        Assert.False(tracker.IsTracking(startedAt.AddSeconds(121)));
        Assert.Equal(40d, rate);
    }

    [Fact]
    public void NonPositiveNetGainDoesNotStartTracking()
    {
        var tracker = new OptimizationReboundTracker();
        var startedAt = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        tracker.Begin(Memory(4_000), Memory(3_900), startedAt);

        Assert.True(tracker.HasResult);
        Assert.False(tracker.IsTracking(startedAt));
        Assert.Equal(0d, tracker.Observe(Memory(3_000), startedAt.AddSeconds(30)));
    }

    private static MemorySnapshot Memory(ulong availableBytes) =>
        new(8_000, availableBytes, 50);
}

public class ProcessTrimSafetyPolicyTests
{
    [Fact]
    public void SafetyScopeContainsAncestorsAndDescendantsButNotUnrelatedSiblings()
    {
        var processes = new[]
        {
            new ProcessSnapshot(1, "parent", null, null, 1, 0, 0, false, false, true, 90),
            new ProcessSnapshot(2, "target", null, 1, 1, 0, 0, false, false, true, 90),
            new ProcessSnapshot(3, "child", null, 2, 1, 0, 0, false, false, true, 90),
            new ProcessSnapshot(4, "sibling", null, 1, 1, 0, 0, false, false, true, 90)
        };

        var scope = ProcessRelationshipPolicy.BuildSafetyScope(2, processes);

        Assert.Equal(new[] { 1, 2, 3 }, scope.OrderBy(id => id));
    }

    [Fact]
    public void ExecutionSafetyScopeRetainsKnownLinksAndAddsNewDescendantsWithoutUnrelatedSiblings()
    {
        var sampledScope = new HashSet<int> { 1, 2, 3, 6 };
        var currentParentProcessIds = new Dictionary<int, int?>
        {
            [1] = null,
            [2] = 1,
            [3] = 2,
            [4] = 3,
            [5] = 1
        };

        var refreshed = ProcessRelationshipPolicy.TryRefreshSafetyScope(
            2,
            sampledScope,
            currentParentProcessIds,
            out var scope);

        Assert.True(refreshed);
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, scope.OrderBy(id => id));
        Assert.False(ProcessInteractionSafetyPolicy.Evaluate(
            scope,
            foregroundProcessId: 4,
            visibleWindowProcessIds: new HashSet<int>()).CanTrim);
        Assert.False(ProcessTrimSafetyPolicy.Evaluate(
            2,
            expectedStartTimeFileTimeUtc: 123,
            actualStartTimeFileTimeUtc: 123,
            scope,
            foregroundProcessId: null,
            visibleWindowProcessIds: new HashSet<int> { 4 }).CanTrim);
    }

    [Fact]
    public void ExecutionSafetyScopeFailsClosedWhenTargetIsMissingFromCurrentSnapshot()
    {
        var refreshed = ProcessRelationshipPolicy.TryRefreshSafetyScope(
            2,
            new HashSet<int> { 1, 2 },
            new Dictionary<int, int?> { [1] = null, [3] = 1 },
            out _);

        Assert.False(refreshed);
    }

    [Fact]
    public void ExecutionSafetyScopeAddsAncestorMissingFromSampledRelationships()
    {
        var refreshed = ProcessRelationshipPolicy.TryRefreshSafetyScope(
            2,
            new HashSet<int> { 2 },
            new Dictionary<int, int?> { [1] = null, [2] = 1 },
            out var scope);

        Assert.True(refreshed);
        Assert.Equal(new[] { 1, 2 }, scope.OrderBy(id => id));
        Assert.False(ProcessInteractionSafetyPolicy.Evaluate(
            scope,
            foregroundProcessId: 1,
            visibleWindowProcessIds: new HashSet<int>()).CanTrim);
    }

    [Fact]
    public void ExecutionSafetyScopeIncludesDescendantsBeyondThePreviousDepthLimit()
    {
        var currentParentProcessIds = new Dictionary<int, int?> { [1] = null };
        for (var processId = 2; processId <= 20; processId++)
        {
            currentParentProcessIds[processId] = processId - 1;
        }

        var refreshed = ProcessRelationshipPolicy.TryRefreshSafetyScope(
            1,
            new HashSet<int> { 1 },
            currentParentProcessIds,
            out var scope);

        Assert.True(refreshed);
        Assert.Contains(20, scope);
    }

    private static readonly HashSet<int> FamilyProcessIds = new() { 101, 102 };

    [Fact]
    public void BaselineIdentityCheckAllowsOnlyTheSampledProcessInstance()
    {
        Assert.True(ProcessIdentitySafetyPolicy.Evaluate(12345, 12345).CanTrim);
        Assert.False(ProcessIdentitySafetyPolicy.Evaluate(12345, 67890).CanTrim);
        Assert.False(ProcessIdentitySafetyPolicy.Evaluate(null, 12345).CanTrim);
        Assert.False(ProcessIdentitySafetyPolicy.Evaluate(12345, null).CanTrim);
    }

    [Fact]
    public void MatchingBackgroundProcessCanBeTrimmed()
    {
        var result = ProcessTrimSafetyPolicy.Evaluate(
            101, 12345, 12345, FamilyProcessIds, null, new HashSet<int>());

        Assert.True(result.CanTrim);
    }

    [Fact]
    public void BaselineInteractionCheckAllowsFamilyThatRemainsVisibleButNotForeground()
    {
        var result = ProcessInteractionSafetyPolicy.Evaluate(
            FamilyProcessIds,
            foregroundProcessId: null,
            visibleWindowProcessIds: new HashSet<int> { 102 });

        Assert.True(result.CanTrim);
    }

    [Fact]
    public void BaselineInteractionCheckSkipsWhenRelatedProcessBecomesForeground()
    {
        var result = ProcessInteractionSafetyPolicy.Evaluate(
            FamilyProcessIds,
            foregroundProcessId: 102,
            visibleWindowProcessIds: new HashSet<int>());

        Assert.False(result.CanTrim);
        Assert.Contains("前台", result.SkipReason);
    }

    [Fact]
    public void BaselineInteractionCheckHonorsExplicitForegroundPermission()
    {
        var result = ProcessInteractionSafetyPolicy.Evaluate(
            FamilyProcessIds,
            foregroundProcessId: 102,
            visibleWindowProcessIds: new HashSet<int>(),
            allowForegroundProcessTrim: true);

        Assert.True(result.CanTrim);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ForegroundPermissionRequiresExplicitOptInAndDisabledEnhancedSafety(
        bool requested,
        bool enhancedSafety,
        bool expected)
    {
        Assert.Equal(expected, ForegroundTrimPolicy.IsAllowed(requested, enhancedSafety));
    }

    [Fact]
    public void BaselineInteractionCheckAllowsFamilyThatRemainsInBackground()
    {
        var result = ProcessInteractionSafetyPolicy.Evaluate(
            FamilyProcessIds,
            foregroundProcessId: null,
            visibleWindowProcessIds: new HashSet<int>());

        Assert.True(result.CanTrim);
    }

    [Fact]
    public void ExecutionInteractionCheckFailsClosedWhenForegroundSnapshotFails()
    {
        var result = ProcessExecutionSafetyPolicy.EvaluateInteraction(
            foregroundSnapshotSucceeded: false,
            FamilyProcessIds,
            foregroundProcessId: null,
            allowForegroundProcessTrim: false);

        Assert.False(result.CanTrim);
        Assert.Contains("前台", result.SkipReason);
    }

    [Fact]
    public void EnhancedExecutionCheckFailsClosedWhenWindowSnapshotFails()
    {
        var result = ProcessExecutionSafetyPolicy.EvaluateEnhanced(
            101,
            expectedStartTimeFileTimeUtc: 12345,
            actualStartTimeFileTimeUtc: 12345,
            FamilyProcessIds,
            foregroundSnapshotSucceeded: true,
            foregroundProcessId: null,
            visibleWindowSnapshotSucceeded: false,
            visibleWindowProcessIds: new HashSet<int>());

        Assert.False(result.CanTrim);
        Assert.Contains("窗口", result.SkipReason);
    }

    [Fact]
    public void ReusedProcessIdIsSkipped()
    {
        var result = ProcessTrimSafetyPolicy.Evaluate(
            101, 12345, 67890, FamilyProcessIds, null, new HashSet<int>());

        Assert.False(result.CanTrim);
        Assert.Contains("PID", result.SkipReason);
    }

    [Fact]
    public void FamilyIsSkippedWhenSiblingBecomesForeground()
    {
        var result = ProcessTrimSafetyPolicy.Evaluate(
            101, 12345, 12345, FamilyProcessIds, 102, new HashSet<int>());

        Assert.False(result.CanTrim);
        Assert.Contains("前台", result.SkipReason);
    }

    [Fact]
    public void FamilyIsSkippedWhenSiblingGetsVisibleWindow()
    {
        var result = ProcessTrimSafetyPolicy.Evaluate(
            101, 12345, 12345, FamilyProcessIds, null, new HashSet<int> { 102 });

        Assert.False(result.CanTrim);
        Assert.Contains("可见", result.SkipReason);
    }

    [Fact]
    public void MissingIdentityFailsClosed()
    {
        var result = ProcessTrimSafetyPolicy.Evaluate(
            101, null, 12345, FamilyProcessIds, null, new HashSet<int>());

        Assert.False(result.CanTrim);
        Assert.Contains("身份", result.SkipReason);
    }
}

public class ProtectionRulesTests
{
    private const string ProtectedPath = @"F:\Games\NovaQuest\NovaQuest.exe";

    [Fact]
    public void RelatedLauncherWindowIsProtectedByTitleToken()
    {
        var launcher = Snapshot(
            10,
            "launcher",
            @"F:\Shared\launcher.exe",
            hasVisibleWindow: true,
            mainWindowTitle: "Nova Quest Settings");
        var family = new ProcessFamilySnapshot("launcher", "launcher", @"F:\Shared", new[] { launcher });
        var rules = new ProtectionRules(new[] { ProtectedPath });

        Assert.True(rules.IsProtected(family, rules.CreateContext(new[] { launcher })));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("discord")]
    public void UnrelatedApplicationIsNotProtectedOnlyBecauseTitleMentionsProtectedApp(string processName)
    {
        var process = Snapshot(
            11,
            processName,
            $@"F:\Apps\{processName}\{processName}.exe",
            hasVisibleWindow: true,
            mainWindowTitle: "Nova Quest Community");
        var family = new ProcessFamilySnapshot(processName, processName, null, new[] { process });
        var rules = new ProtectionRules(new[] { ProtectedPath });

        Assert.False(rules.IsProtected(family, rules.CreateContext(new[] { process })));
    }

    [Fact]
    public void GenericShortTitleTokenDoesNotProtectLauncher()
    {
        var process = Snapshot(
            12,
            "launcher",
            @"F:\Shared\launcher.exe",
            hasVisibleWindow: true,
            mainWindowTitle: "Game Setup");
        var family = new ProcessFamilySnapshot("launcher", "launcher", null, new[] { process });
        var rules = new ProtectionRules(new[] { @"F:\Games\Game\game.exe" });

        Assert.False(rules.IsProtected(family, rules.CreateContext(new[] { process })));
    }

    [Fact]
    public void DisablingRelatedProtectionRemovesOnlyTheExactExecutableFromFamily()
    {
        var selected = Snapshot(20, "client", @"F:\Apps\Suite\client.exe", false, string.Empty);
        var helper = Snapshot(21, "helper", @"F:\Apps\Suite\helper.exe", false, string.Empty);
        var family = new ProcessFamilySnapshot("suite", "client", @"F:\Apps\Suite", new[] { selected, helper });
        var rules = new ProtectionRules(new[] { selected.ExecutablePath! }, protectRelatedProcesses: false);

        var filtered = rules.FilterUnprotectedProcesses(family, rules.CreateContext(family.Processes));

        Assert.NotNull(filtered);
        Assert.Equal(21, Assert.Single(filtered!.Processes).ProcessId);
    }

    [Fact]
    public void RelatedProtectionCoversSiblingComponentsInTheSameProductTree()
    {
        var sonar = Snapshot(30, "SteelSeriesSonar", @"C:\Program Files\SteelSeries\GG\apps\sonar\SteelSeriesSonar.exe", false, string.Empty);
        var engine = Snapshot(31, "SteelSeriesEngine", @"C:\Program Files\SteelSeries\GG\apps\engine\SteelSeriesEngine.exe", false, string.Empty);
        var prism = Snapshot(32, "SteelSeriesPrism", @"C:\Program Files\SteelSeries\GG\apps\engine\prism\SteelSeriesPrism.exe", false, string.Empty);
        var otherProduct = Snapshot(33, "SteelSeriesOther", @"C:\Program Files\SteelSeries\Other\SteelSeriesOther.exe", false, string.Empty);
        var processes = new[] { sonar, engine, prism, otherProduct };
        var rules = new ProtectionRules(new[] { sonar.ExecutablePath! });
        var context = rules.CreateContext(processes);

        Assert.True(rules.IsProtected(new ProcessFamilySnapshot("sonar", sonar.Name, null, new[] { sonar }), context));
        Assert.True(rules.IsProtected(new ProcessFamilySnapshot("engine", engine.Name, null, new[] { engine }), context));
        Assert.True(rules.IsProtected(new ProcessFamilySnapshot("prism", prism.Name, null, new[] { prism }), context));
        Assert.False(rules.IsProtected(new ProcessFamilySnapshot("other", otherProduct.Name, null, new[] { otherProduct }), context));
    }

    [Fact]
    public void WholeFamilyAndSelectedExecutableRulesCanCoexist()
    {
        var protectedRoot = Snapshot(40, "editor", @"F:\Apps\Editor\editor.exe", false, string.Empty);
        var protectedChild = Snapshot(41, "editor-helper", @"F:\Apps\Editor\editor-helper.exe", false, string.Empty);
        var selectedComponent = Snapshot(50, "capture", @"F:\Apps\Media\capture.exe", false, string.Empty);
        var unselectedComponent = Snapshot(51, "media-helper", @"F:\Apps\Media\helper.exe", false, string.Empty);
        var wholeFamily = new ProcessFamilySnapshot(
            "editor",
            "Editor",
            @"F:\Apps\Editor",
            new[] { protectedRoot, protectedChild });
        var partialFamily = new ProcessFamilySnapshot(
            "media",
            "Media",
            @"F:\Apps\Media",
            new[] { selectedComponent, unselectedComponent });
        var rules = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = protectedRoot.ExecutablePath!,
                ProtectEntireFamily = true
            },
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = @"F:\Apps\Media\media.exe",
                ProtectedExecutablePaths = new List<string> { selectedComponent.ExecutablePath! }
            }
        });
        var context = rules.CreateContext(wholeFamily.Processes.Concat(partialFamily.Processes));

        Assert.Null(rules.FilterUnprotectedProcesses(wholeFamily, context));
        var remaining = Assert.IsType<ProcessFamilySnapshot>(
            rules.FilterUnprotectedProcesses(partialFamily, context));
        Assert.Equal(51, Assert.Single(remaining.Processes).ProcessId);
    }

    [Fact]
    public void PartialRuleUsesApplicationPathOnlyForGrouping()
    {
        var application = Snapshot(60, "media", @"F:\Apps\Media\media.exe", false, string.Empty);
        var selected = Snapshot(61, "capture", @"F:\Apps\Media\capture.exe", false, string.Empty);
        var helper = Snapshot(62, "helper", @"F:\Apps\Media\helper.exe", false, string.Empty);
        var family = new ProcessFamilySnapshot(
            "media",
            "Media",
            @"F:\Apps\Media",
            new[] { application, selected, helper });
        var rules = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = application.ExecutablePath!,
                ProtectedExecutablePaths = new List<string>
                {
                    @"F:\Apps\Media\.\capture.exe",
                    @"f:\apps\media\CAPTURE.exe"
                }
            }
        });

        var remaining = Assert.IsType<ProcessFamilySnapshot>(
            rules.FilterUnprotectedProcesses(family, rules.CreateContext(family.Processes)));

        Assert.Equal(new[] { 60, 62 }, remaining.Processes.Select(process => process.ProcessId));
    }

    private static ProcessSnapshot Snapshot(
        int id,
        string name,
        string path,
        bool hasVisibleWindow,
        string mainWindowTitle) =>
        new(id, name, path, null, 100L * 1024 * 1024, 0, 0, false, hasVisibleWindow, true, 90, null, mainWindowTitle);
}
