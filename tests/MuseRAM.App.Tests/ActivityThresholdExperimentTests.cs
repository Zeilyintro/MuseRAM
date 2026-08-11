using System.Globalization;
using MuseRAM.App;
using MuseRAM.Core;

namespace MuseRAM.App.Tests;

public sealed class ActivityThresholdExperimentTests
{
    [Theory]
    [InlineData(OptimizationProfile.Lite, 2, 8, 1, 4)]
    [InlineData(OptimizationProfile.Turbo, 7.5, 8.5, 3.6, 4.4)]
    [InlineData(OptimizationProfile.Ultimate, 6, 20, 4, 16)]
    public void ThresholdExperimentsUseTheProfileSpecificRanges(
        OptimizationProfile profile,
        double expectedCpuLow,
        double expectedCpuHigh,
        double expectedIoLow,
        double expectedIoHigh)
    {
        var baseline = OptimizationSettings.For(profile);
        var experiments = ActivityThresholdExperimentCatalog.For(profile, baseline);

        Assert.Equal(5, experiments.Count);
        Assert.Single(experiments, experiment => experiment.IsBaseline);
        Assert.Equal(expectedCpuLow, experiments.Single(experiment => experiment.Key.Contains("cpu-") && experiment.CpuThresholdPercent < baseline.ActiveCpuThresholdPercent).CpuThresholdPercent);
        Assert.Equal(expectedCpuHigh, experiments.Single(experiment => experiment.Key.Contains("cpu-") && experiment.CpuThresholdPercent > baseline.ActiveCpuThresholdPercent).CpuThresholdPercent);
        Assert.Equal(
            ActivityThresholdExperimentCatalog.IoBytesPerSecond(expectedIoLow),
            experiments.Single(experiment => experiment.Key.Contains("io-") && experiment.IoThresholdBytesPerSecond < baseline.ActiveIoThresholdBytesPerSecond).IoThresholdBytesPerSecond);
        Assert.Equal(
            ActivityThresholdExperimentCatalog.IoBytesPerSecond(expectedIoHigh),
            experiments.Single(experiment => experiment.Key.Contains("io-") && experiment.IoThresholdBytesPerSecond > baseline.ActiveIoThresholdBytesPerSecond).IoThresholdBytesPerSecond);

        foreach (var experiment in experiments.Where(experiment => !experiment.IsBaseline))
        {
            Assert.Equal(
                experiment.ParameterName == "cpu" ? experiment.ShadowValue : baseline.ActiveCpuThresholdPercent,
                experiment.CpuThresholdPercent);
            Assert.Equal(
                ActivityThresholdExperimentCatalog.IoBytesPerSecond(
                    experiment.ParameterName == "io"
                        ? experiment.ShadowValue
                        : baseline.ActiveIoThresholdBytesPerSecond / (1024d * 1024d)),
                experiment.IoThresholdBytesPerSecond);
        }
    }

    [Fact]
    public void ThresholdKeysUseInvariantDecimalSeparators()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");

            var experiments = ActivityThresholdExperimentCatalog.For(OptimizationProfile.Turbo);

            Assert.Contains("turbo-cpu-7.5", experiments.Select(experiment => experiment.Key));
            Assert.Contains("turbo-cpu-8.5", experiments.Select(experiment => experiment.Key));
            Assert.Contains("turbo-io-3.6mib", experiments.Select(experiment => experiment.Key));
            Assert.Contains("turbo-io-4.4mib", experiments.Select(experiment => experiment.Key));
            Assert.DoesNotContain(experiments, experiment => experiment.Key.Contains(','));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite, 9)]
    [InlineData(OptimizationProfile.Turbo, 6)]
    [InlineData(OptimizationProfile.Ultimate, 7)]
    public void CoreParameterCatalogUsesTheDocumentedValues(
        OptimizationProfile profile,
        int expectedCount)
    {
        var baseline = OptimizationSettings.For(profile);
        var experiments = ProfileParameterExperimentCatalog.For(profile, baseline);

        Assert.Equal(expectedCount, experiments.Count);
        Assert.Single(experiments, experiment => experiment.IsBaseline);
        Assert.All(experiments.Where(experiment => !experiment.IsBaseline), experiment =>
        {
            Assert.False(experiment.IsBaseline);
            Assert.NotEqual(baseline, experiment.Settings);
            AssertOnlyTargetParameterDiffers(baseline, experiment.Settings, experiment.ParameterName);
        });

        var expectedFamily = profile == OptimizationProfile.Lite
            ? new[] { 192d, 384d }
            : profile == OptimizationProfile.Turbo
                ? new[] { 96d }
                : new[] { 32d, 64d };
        Assert.Equal(
            expectedFamily,
            experiments.Where(experiment => experiment.ParameterName == "family-ws").Select(experiment => experiment.ShadowValue).OrderBy(value => value));
        var expectedProcess = profile == OptimizationProfile.Lite
            ? new[] { 12d, 40d }
            : profile == OptimizationProfile.Turbo
                ? Array.Empty<double>()
                : new[] { 2d, 8d };
        Assert.Equal(
            expectedProcess,
            experiments.Where(experiment => experiment.ParameterName == "process-ws").Select(experiment => experiment.ShadowValue).OrderBy(value => value));

        var expectedIdle = profile == OptimizationProfile.Lite
            ? new[] { 50d, 80d }
            : profile == OptimizationProfile.Turbo
                ? new[] { 35d, 55d }
                : new[] { 15d, 45d };
        Assert.Equal(
            expectedIdle,
            experiments.Where(experiment => experiment.ParameterName == "idle-score").Select(experiment => experiment.ShadowValue).OrderBy(value => value));

        var visible = experiments.Where(experiment => experiment.ParameterName == "visible-window").ToArray();
        if (profile == OptimizationProfile.Ultimate)
            Assert.Empty(visible);
        else
            Assert.Equal(
                profile == OptimizationProfile.Lite ? new[] { 5d, 15d } : new[] { 3d, 8d },
                visible.Select(experiment => experiment.ShadowValue).OrderBy(value => value));
    }

    [Fact]
    public void CoreParameterKeysUseStableUnitsAndInvariantCulture()
    {
        var experiments = ProfileParameterExperimentCatalog.For(OptimizationProfile.Turbo);

        Assert.Contains("turbo-family-ws-96mib", experiments.Select(experiment => experiment.Key));
        Assert.Contains("turbo-idle-score-35.0", experiments.Select(experiment => experiment.Key));
        Assert.Contains("turbo-visible-window-3.0min", experiments.Select(experiment => experiment.Key));
        Assert.DoesNotContain(experiments, experiment => experiment.Key.Contains(','));
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite)]
    [InlineData(OptimizationProfile.Turbo)]
    [InlineData(OptimizationProfile.Ultimate)]
    public void CustomSettingsRemainTheBaselineAndUseTheExplicitBaseProfile(
        OptimizationProfile baseProfile)
    {
        var builtIn = OptimizationSettings.For(baseProfile);
        var custom = builtIn with
        {
            ActiveCpuThresholdPercent = 13.1,
            ActiveIoThresholdBytesPerSecond = 6.7 * 1024 * 1024,
            MinimumFamilyWorkingSetBytes = builtIn.MinimumFamilyWorkingSetBytes + 8L * 1024 * 1024,
            MinimumIdleScore = builtIn.MinimumIdleScore + 1.5
        };

        var thresholdExperiments = ActivityThresholdExperimentCatalog.For(
            OptimizationProfile.Turbo,
            baseProfile,
            custom);
        var thresholdBaseline = thresholdExperiments.Single(experiment => experiment.IsBaseline);
        Assert.Equal(13.1, thresholdBaseline.CpuThresholdPercent);
        Assert.Equal(
            ActivityThresholdExperimentCatalog.IoBytesPerSecond(6.7),
            thresholdBaseline.IoThresholdBytesPerSecond);

        var coreExperiments = ProfileParameterExperimentCatalog.For(baseProfile, custom);
        var coreBaseline = coreExperiments.Single(experiment => experiment.IsBaseline);
        Assert.Equal(custom, coreBaseline.Settings);
        Assert.Equal(custom.MinimumFamilyWorkingSetBytes / (1024d * 1024d), coreBaseline.Settings.MinimumFamilyWorkingSetBytes / (1024d * 1024d));
        Assert.Contains(
            coreExperiments,
            experiment => experiment.ParameterName == "family-ws" && experiment.Key.StartsWith(
                baseProfile == OptimizationProfile.Lite ? "lite-" :
                baseProfile == OptimizationProfile.Turbo ? "turbo-" : "ultimate-",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CustomCoreShadowsOmitNonPositiveShiftedValues()
    {
        var custom = OptimizationSettings.For(OptimizationProfile.Ultimate) with
        {
            MinimumFamilyWorkingSetBytes = 2L * 1024 * 1024,
            MinimumProcessWorkingSetBytes = 1L * 1024 * 1024,
            MinimumIdleScore = 1
        };

        var experiments = ProfileParameterExperimentCatalog.For(
            OptimizationProfile.Ultimate,
            custom);

        Assert.All(experiments.Where(experiment => !experiment.IsBaseline), experiment =>
            Assert.True(experiment.ShadowValue > 0));
        Assert.DoesNotContain(experiments, experiment => experiment.Key.Contains("--", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityShadowsDeduplicateValuesClampedToTheBaseline()
    {
        var custom = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            ActiveCpuThresholdPercent = 0.1,
            ActiveIoThresholdBytesPerSecond = ActivityThresholdExperimentCatalog.IoBytesPerSecond(0.1)
        };

        var experiments = ActivityThresholdExperimentCatalog.For(
            OptimizationProfile.Turbo,
            custom);
        var baseline = Assert.Single(experiments, experiment => experiment.IsBaseline);

        Assert.DoesNotContain(experiments.Where(experiment => !experiment.IsBaseline), experiment =>
            experiment.CpuThresholdPercent == baseline.CpuThresholdPercent &&
            experiment.IoThresholdBytesPerSecond == baseline.IoThresholdBytesPerSecond);
        Assert.Equal(
            experiments.Count,
            experiments.Select(experiment => (
                experiment.CpuThresholdPercent,
                experiment.IoThresholdBytesPerSecond)).Distinct().Count());
    }

    private static void AssertOnlyTargetParameterDiffers(
        OptimizationSettings baseline,
        OptimizationSettings shadow,
        string parameterName)
    {
        if (parameterName != "family-ws")
            Assert.Equal(baseline.MinimumFamilyWorkingSetBytes, shadow.MinimumFamilyWorkingSetBytes);
        if (parameterName != "process-ws")
            Assert.Equal(baseline.MinimumProcessWorkingSetBytes, shadow.MinimumProcessWorkingSetBytes);
        if (parameterName != "idle-score")
            Assert.Equal(baseline.MinimumIdleScore, shadow.MinimumIdleScore);
        if (parameterName != "visible-window")
            Assert.Equal(baseline.VisibleWindowIdleDelay, shadow.VisibleWindowIdleDelay);
        Assert.Equal(baseline.ActiveCpuThresholdPercent, shadow.ActiveCpuThresholdPercent);
        Assert.Equal(baseline.ActiveIoThresholdBytesPerSecond, shadow.ActiveIoThresholdBytesPerSecond);
        Assert.Equal(baseline.MaxApplications, shadow.MaxApplications);
        Assert.Equal(baseline.TriggerAvailableBytes, shadow.TriggerAvailableBytes);
        Assert.Equal(baseline.TriggerAvailablePercent, shadow.TriggerAvailablePercent);
        Assert.Equal(baseline.IgnoreMemoryPressureThreshold, shadow.IgnoreMemoryPressureThreshold);
        Assert.Equal(baseline.AllowForegroundProcessTrim, shadow.AllowForegroundProcessTrim);
        Assert.Equal(baseline.ProcessCooldown, shadow.ProcessCooldown);
        Assert.Equal(baseline.AutoCooldown, shadow.AutoCooldown);
        Assert.Equal(baseline.ProtectGamingProcesses, shadow.ProtectGamingProcesses);
        Assert.Equal(baseline.EnhancedSafety, shadow.EnhancedSafety);
        Assert.Equal(baseline.IntelligentCandidateSelection, shadow.IntelligentCandidateSelection);
        Assert.Equal(baseline.QuickCandidateSelection, shadow.QuickCandidateSelection);
        Assert.Equal(baseline.AllowIndependentBackgroundProcessTrim, shadow.AllowIndependentBackgroundProcessTrim);
        Assert.Equal(baseline.StableStateSuppressionMode, shadow.StableStateSuppressionMode);
    }
}
