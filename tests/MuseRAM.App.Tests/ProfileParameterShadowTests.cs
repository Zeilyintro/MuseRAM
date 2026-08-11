using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuseRAM.App;
using MuseRAM.Core;

namespace MuseRAM.App.Tests;

public sealed class ProfileParameterShadowTests
{
    [Fact]
    public void RecomputedBaselineDriftIsNotAttributedToParameterVariants()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var formalFamily = Family("formal-family", 9001, 100L * 1024 * 1024, 70);
        var recomputedFamily = Family("recomputed-family", 9002, 120L * 1024 * 1024, 80);
        var formalPlan = Plan(
            new OptimizationCandidate(formalFamily, formalFamily.Processes, 70, formalFamily.WorkingSetBytes, "formal"),
            Evaluation(formalFamily, true, Array.Empty<CandidateExclusionReason>()));
        var recomputedPlan = Plan(
            new OptimizationCandidate(recomputedFamily, recomputedFamily.Processes, 80, recomputedFamily.WorkingSetBytes, "recomputed"),
            Evaluation(recomputedFamily, true, Array.Empty<CandidateExclusionReason>()));

        var shadows = CandidatePlanCalibrationPolicy.CreateProfileParameterShadows(
            new OptimizationRunContext("builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3"),
            DateTimeOffset.UtcNow,
            formalPlan,
            settings,
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { formalFamily, recomputedFamily },
            options: new ProfileParameterShadowPlanningOptions(_ => recomputedPlan));

        var baseline = Assert.Single(shadows, shadow => shadow.IsBaseline);
        Assert.Equal("formal-plan-drift", baseline.ComparisonKind);
        Assert.Equal(1, baseline.AddedCandidateCount);
        Assert.Equal(1, baseline.RemovedCandidateCount);
        Assert.All(shadows.Where(shadow => !shadow.IsBaseline), shadow =>
        {
            Assert.Equal("recomputed-baseline", shadow.ComparisonKind);
            Assert.Equal(0, shadow.AddedCandidateCount);
            Assert.Equal(0, shadow.RemovedCandidateCount);
        });
    }

    [Fact]
    public void CandidateMetricRunsCoreShadowsAgainstOneFormalBaseline()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var context = new OptimizationRunContext(
            "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3");
        var formalFamily = Family("formal-family", 1001, 100L * 1024 * 1024, 70);
        var shadowFamily = Family("shadow-family", 1002, 120L * 1024 * 1024, 80);
        var formalPlan = Plan(
            new OptimizationCandidate(
                formalFamily,
                formalFamily.Processes,
                formalFamily.IdleConfidenceScore,
                formalFamily.WorkingSetBytes,
                "formal"),
            Evaluation(formalFamily, isEligible: true, Array.Empty<CandidateExclusionReason>()));
        var shadowPlan = new OptimizationPlan(
            true,
            "shadow",
            new[]
            {
                new OptimizationCandidate(
                    shadowFamily,
                    shadowFamily.Processes,
                    shadowFamily.IdleConfidenceScore,
                    shadowFamily.WorkingSetBytes,
                    "shadow")
            },
            OptimizationPlanOutcome.CandidatesFound)
        {
            CandidateEvaluations = new[]
            {
                Evaluation(formalFamily, isEligible: false, new[] { CandidateExclusionReason.BelowIdleScore }),
                Evaluation(shadowFamily, isEligible: true, Array.Empty<CandidateExclusionReason>())
            }
        };
        var calls = new List<OptimizationSettings>();
        var options = new ProfileParameterShadowPlanningOptions(shadowSettings =>
        {
            calls.Add(shadowSettings);
            return shadowSettings == settings ? formalPlan : shadowPlan;
        });

        var metric = CandidatePlanCalibrationPolicy.Create(
            context,
            DateTimeOffset.UtcNow,
            formalPlan,
            settings,
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { formalFamily, shadowFamily },
            observedFamilyCount: 2,
            profileParameterShadows: options);

        Assert.Equal(
            ProfileParameterExperimentCatalog.For(OptimizationProfile.Turbo, settings).Count,
            metric.ProfileParameterShadows.Count);
        Assert.Single(metric.ProfileParameterShadows, shadow => shadow.IsBaseline);
        Assert.Equal(metric.ProfileParameterShadows.Count, calls.Count);
        var baseline = Assert.Single(metric.ProfileParameterShadows.Where(shadow => shadow.IsBaseline));
        Assert.All(metric.ProfileParameterShadows, shadow => Assert.Equal(baseline.Key, shadow.BaselineKey));
        Assert.All(metric.ProfileParameterShadows.Skip(1), shadow => Assert.False(shadow.IsBaseline));
        var sharedBaselineMetric = CandidatePlanCalibrationPolicy.AttachActivityThresholdShadows(
            metric,
            new[]
            {
                new ActivityThresholdShadowMetric(
                    baseline.Key, 8, 4 * 1024 * 1024, 1, 1, 1, 100, 0, 0, 0, 0)
                {
                    IsBaseline = true
                }
            });
        Assert.Single(sharedBaselineMetric.ActivityThresholdShadows, shadow => shadow.IsBaseline);
        Assert.DoesNotContain(sharedBaselineMetric.ProfileParameterShadows, shadow => shadow.IsBaseline);
        Assert.Equal(1, sharedBaselineMetric.ActivityThresholdShadows.Count(shadow => shadow.IsBaseline) +
                        sharedBaselineMetric.ProfileParameterShadows.Count(shadow => shadow.IsBaseline));

        var variant = metric.ProfileParameterShadows.First(shadow => !shadow.IsBaseline);
        Assert.Equal(1, variant.AddedCandidateCount);
        Assert.Equal(1, variant.RemovedCandidateCount);
        Assert.Equal(
            new[] { CalibrationFamilyId("shadow-family") },
            variant.AddedFamilyIds);
        Assert.Equal(
            new[] { CalibrationFamilyId("formal-family") },
            variant.RemovedFamilyIds);
        var added = Assert.Single(variant.Differences, difference => difference.ShadowEligible);
        Assert.Equal(80, added.ShadowIdleScore);
        Assert.Equal(0, added.FormalTargetWorkingSetBytes);
        Assert.Equal(120L * 1024 * 1024, added.ShadowTargetWorkingSetBytes);
        Assert.Equal(1, added.ShadowReliableTargetProcessCount);
        Assert.Empty(added.ShadowExclusionReasons);
        var removed = Assert.Single(variant.Differences, difference => difference.FormalEligible);
        Assert.Equal(70, removed.FormalIdleScore);
        Assert.Equal(100L * 1024 * 1024, removed.FormalTargetWorkingSetBytes);
        Assert.Equal(1, removed.FormalReliableTargetProcessCount);
        Assert.Equal(
            new[] { nameof(CandidateExclusionReason.BelowIdleScore) },
            removed.ShadowExclusionReasons);

        Assert.Single(formalPlan.Candidates);
        Assert.Equal("formal-family", formalPlan.Candidates[0].Family.Key);
        Assert.Single(formalPlan.CandidateEvaluations);
        Assert.Equal(
            ProfileParameterExperimentCatalog.For(OptimizationProfile.Turbo, settings)
                .Select(experiment => experiment.Settings),
            calls);
        Assert.All(calls, shadowSettings => Assert.Equal(settings.MaxApplications, shadowSettings.MaxApplications));
    }

    [Fact]
    public void DefaultCandidateMetricFlowUsesFormalPlannerWithoutPersistingShadowState()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            IgnoreMemoryPressureThreshold = true,
            MaxApplications = 0,
            MinimumFamilyWorkingSetBytes = 0,
            MinimumProcessWorkingSetBytes = 0,
            MinimumIdleScore = 0,
            QuickCandidateSelection = true
        };
        var family = Family("default-family", 2001, 100L * 1024 * 1024, 80);
        var planner = new OptimizationPlanner();
        var formalPlan = planner.CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { family },
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        var metric = CandidatePlanCalibrationPolicy.Create(
            new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Manual, "1.2.3"),
            DateTimeOffset.UtcNow,
            formalPlan,
            settings,
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { family },
            observedFamilyCount: 1);

        Assert.Equal(
            ProfileParameterExperimentCatalog.For(OptimizationProfile.Turbo, settings).Count,
            metric.ProfileParameterShadows.Count);
        Assert.Single(metric.ProfileParameterShadows, shadow => shadow.IsBaseline);
        var baseline = Assert.Single(metric.ProfileParameterShadows.Where(shadow => shadow.IsBaseline));
        Assert.Equal(formalPlan.CandidateEvaluations.Count(evaluation => evaluation.IsEligible), baseline.EligibleFamilyCount);
        Assert.Equal(formalPlan.Candidates.Sum(candidate => candidate.TargetProcesses.Count), baseline.TargetProcessCount);
        Assert.Single(formalPlan.Candidates);
    }

    [Fact]
    public void DisabledDiagnosticsDoNotInvokeTheShadowPlannerOrCreateShadowMetrics()
    {
        var calls = 0;
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var family = Family("diagnostics-off", 3001, 100L * 1024 * 1024, 80);
        var formalPlan = Plan(
            new OptimizationCandidate(
                family,
                family.Processes,
                family.IdleConfidenceScore,
                family.WorkingSetBytes,
                "formal"),
            Evaluation(family, isEligible: true, Array.Empty<CandidateExclusionReason>()));

        var metric = CandidatePlanCalibrationPolicy.Create(
            new OptimizationRunContext(
                "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3"),
            DateTimeOffset.UtcNow,
            formalPlan,
            settings,
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
            new[] { family },
            observedFamilyCount: 1,
            profileParameterShadows: new ProfileParameterShadowPlanningOptions(_ =>
            {
                calls++;
                return formalPlan;
            }, Enabled: false));

        Assert.Empty(metric.ProfileParameterShadows);
        Assert.Equal(0, calls);
        Assert.Single(formalPlan.Candidates);
    }

    [Fact]
    public void ShadowMetricSerializationKeepsSchemaAndAnonymizesFamilyIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"museram-profile-shadow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
            var family = Family(@"private-family:c:\Users\private\App", 4001, 100L * 1024 * 1024, 80);
            var plan = Plan(
                new OptimizationCandidate(
                    family,
                    family.Processes,
                    family.IdleConfidenceScore,
                    family.WorkingSetBytes,
                    "formal"),
                Evaluation(family, isEligible: true, Array.Empty<CandidateExclusionReason>()));
            var metric = CandidatePlanCalibrationPolicy.Create(
                new OptimizationRunContext(
                    "builtin:Turbo", OptimizationProfile.Turbo, OptimizationTriggerKind.Automatic, "1.2.3"),
                DateTimeOffset.UtcNow,
                plan,
                settings,
                new MemorySnapshot(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75),
                new[] { family },
                observedFamilyCount: 1,
                profileParameterShadows: new ProfileParameterShadowPlanningOptions(_ => plan));

            new CalibrationMetricsStore(path).AppendCandidatePlan(metric);
            var line = File.ReadAllText(path);
            using var document = JsonDocument.Parse(line);
            Assert.Equal(10, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.True(document.RootElement.GetProperty("Payload").TryGetProperty(
                "ProfileParameterShadows", out var shadows));
            Assert.Equal(6, shadows.GetArrayLength());
            Assert.DoesNotContain("private-family", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Users", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".previous")) File.Delete(path + ".previous");
        }
    }

    private static OptimizationPlan Plan(
        OptimizationCandidate candidate,
        CandidateEvaluation evaluation) => new(
        true,
        "formal",
        new[] { candidate },
        OptimizationPlanOutcome.CandidatesFound)
    {
        CandidateEvaluations = new[] { evaluation }
    };

    private static CandidateEvaluation Evaluation(
        ProcessFamilySnapshot family,
        bool isEligible,
        IReadOnlyList<CandidateExclusionReason> reasons) => new(
        family.Key,
        family.DisplayName,
        isEligible,
        family.Processes.Count,
        family.Processes.Count,
        reasons)
    {
        LegacyIdleScore = family.IdleScore,
        IdleConfidenceScore = family.IdleConfidenceScore,
        TargetWorkingSetBytes = family.WorkingSetBytes,
        TotalWorkingSetBytes = family.WorkingSetBytes,
        TargetProcessIds = family.Processes.Select(process => process.ProcessId).ToArray()
    };

    private static ProcessFamilySnapshot Family(
        string key,
        int processId,
        long workingSetBytes,
        double idleScore) => new(
        key,
        key,
        null,
        new[]
        {
            new ProcessSnapshot(
                processId,
                "sample.exe",
                $"C:\\private\\{key}.exe",
                null,
                workingSetBytes,
                0.1,
                0,
                false,
                false,
                true,
                idleScore,
                StartTimeFileTimeUtc: processId)
        });

    private static string CalibrationFamilyId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 12));
}
