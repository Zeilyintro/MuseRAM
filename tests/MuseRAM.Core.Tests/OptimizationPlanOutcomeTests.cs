namespace MuseRAM.Core.Tests;

public sealed class OptimizationPlanOutcomeTests
{
    [Fact]
    public void AutomaticPlanReportsLowPressureOutcome()
    {
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            Array.Empty<ProcessFamilySnapshot>(),
            OptimizationSettings.For(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(OptimizationPlanOutcome.LowMemoryPressure, plan.Outcome);
    }

    [Fact]
    public void ManualPlanReportsNoCandidatesOutcome()
    {
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 25),
            Array.Empty<ProcessFamilySnapshot>(),
            OptimizationSettings.ForManual(OptimizationProfile.Lite),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true);

        Assert.Equal(OptimizationPlanOutcome.NoCandidates, plan.Outcome);
    }
}
