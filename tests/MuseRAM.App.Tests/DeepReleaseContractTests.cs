using MuseRAM.App;
using MuseRAM.Core;

namespace MuseRAM.App.Tests;

public sealed class DeepReleaseContractTests
{
    [Fact]
    public void CandidateIsCheckedOnlyWhenSuggested()
    {
        Assert.True(DeepReleaseSelectionPolicy.IsCheckedByDefault(Candidate("idle", true)));
        Assert.False(DeepReleaseSelectionPolicy.IsCheckedByDefault(Candidate("observing", false)));
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "已选择 2 个应用 | 涉及工作集约 1.1 GB")]
    [InlineData(UiLanguage.English, "Selected 2 apps | Working set involved about 1.1 GB")]
    public void SelectionSummaryIsLocalized(UiLanguage language, string expected)
    {
        var candidates = new[]
        {
            Candidate("browser", true, 800),
            Candidate("chat", true, 300)
        };

        Assert.Equal(expected, DeepReleasePresentation.FormatSelection(candidates, language));
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "尚未选择应用")]
    [InlineData(UiLanguage.English, "No app selected")]
    public void EmptySelectionSummaryIsLocalized(UiLanguage language, string expected)
    {
        Assert.Equal(expected, DeepReleasePresentation.FormatSelection(Array.Empty<DeepReleaseCandidate>(), language));
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "闲置后台")]
    [InlineData(UiLanguage.English, "Idle in background")]
    public void CandidateStateIsLocalized(UiLanguage language, string expectedState)
    {
        var text = DeepReleasePresentation.FormatCandidate(Candidate("editor", true), language);

        Assert.Contains(expectedState, text);
        Assert.Contains("500 MB", text);
    }

    [Fact]
    public void DeepReleaseHasNoEditionOrActivationMarker()
    {
        foreach (var option in UiLanguageCatalog.Options)
        {
            var label = UiTextCatalog.For(option.Language)["DeepRelease"];
            Assert.DoesNotContain("PRO", label, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MixedApplicationFamilyIsNotRemovedForOneServiceProcess()
    {
        var application = Candidate("suite", false, 200, 20, 21);
        var services = new[]
        {
            new ServiceSuggestion(
                new WindowsServiceDescriptor("suite-service", "Suite Service", null, true, false, 20),
                "suite",
                false,
                "service")
        };

        Assert.Single(DeepReleaseCandidateDeduplicator.RemoveServiceDuplicates(
            new[] { application },
            services));
    }

    private static DeepReleaseCandidate Candidate(
        string name,
        bool suggested,
        long workingSetMiB = 500,
        params int[] processIds)
    {
        if (processIds.Length == 0) processIds = new[] { 10 };
        var processes = processIds.Select(processId => new ProcessSnapshot(
            processId,
            name,
            $@"F:\Apps\{name}\{name}.exe",
            null,
            workingSetMiB * 1024 * 1024 / processIds.Length,
            0,
            0,
            false,
            false,
            true,
            100)).ToArray();
        var family = new ProcessFamilySnapshot(name, name, $@"F:\Apps\{name}", processes);
        return new DeepReleaseCandidate(
            family,
            new BackgroundActivity(name, BackgroundActivityState.Idle, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10),
            suggested);
    }
}
