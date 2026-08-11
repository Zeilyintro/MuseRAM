using MuseRAM.App;

namespace MuseRAM.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void UpdateCompletionRunsBeforeSingleInstanceGuard()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml.cs"));
        var updateIndex = source.IndexOf("if (UpdateCompletionService.IsRequested(e.Args))", StringComparison.Ordinal);
        var guardIndex = source.IndexOf("new SingleInstanceGuard", StringComparison.Ordinal);

        Assert.True(updateIndex >= 0);
        Assert.True(guardIndex > updateIndex);
    }

    [Fact]
    public void PrimaryInstanceFallbackChecksOnlyProcessesThatStartedEarlier()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml.cs"));

        Assert.Contains("SingleInstanceGuard.HasOlderProcess(", source);
        Assert.Contains("currentProcess.StartTime.ToUniversalTime()", source);
        Assert.DoesNotContain("SingleInstanceGuard.HasOtherProcess(", source);
    }

    [Fact]
    public void OnlyFirstGuardWithSameNameIsPrimary()
    {
        var name = $@"Local\MuseRAM.Tests.{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void SecondaryInstanceCanSignalThePrimaryInstance()
    {
        var name = $@"Local\MuseRAM.Tests.Activation.{Guid.NewGuid():N}";
        using var signaled = new ManualResetEventSlim();
        using var activation = new SingleInstanceActivation(name, signaled.Set);

        Assert.True(SingleInstanceActivation.TrySignal(name));
        Assert.True(signaled.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DuplicateInstanceSignalsPrimaryBeforeFallingBackToADialog()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml.cs"));
        var duplicate = source.IndexOf("if (!_singleInstance.IsPrimary", StringComparison.Ordinal);
        var signal = source.IndexOf("SingleInstanceActivation.TrySignal", duplicate, StringComparison.Ordinal);
        var dialog = source.IndexOf("StartupThemedDialog.Show(", signal, StringComparison.Ordinal);

        Assert.True(duplicate >= 0);
        Assert.True(signal > duplicate);
        Assert.True(dialog > signal);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified)]
    [InlineData(UiLanguage.English)]
    public void AlreadyRunningMessageExistsInEveryLanguage(UiLanguage language)
    {
        var text = UiTextCatalog.For(language);

        Assert.False(string.IsNullOrWhiteSpace(text["AlreadyRunningTitle"]));
        Assert.Contains("MuseRAM", text["AlreadyRunningMessage"]);
    }

    [Fact]
    public void ProcessFallbackIgnoresConcurrentNewerInstancesAndDetectsOlderOnes()
    {
        var currentStartedAt = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(SingleInstanceGuard.IsOlderProcessCandidate(
            candidateProcessId: 20,
            candidateStartedAtUtc: currentStartedAt.AddMilliseconds(1),
            currentProcessId: 10,
            currentProcessStartedAtUtc: currentStartedAt));
        Assert.True(SingleInstanceGuard.IsOlderProcessCandidate(
            candidateProcessId: 20,
            candidateStartedAtUtc: currentStartedAt.AddSeconds(-1),
            currentProcessId: 10,
            currentProcessStartedAtUtc: currentStartedAt));
        Assert.False(SingleInstanceGuard.IsOlderProcessCandidate(
            candidateProcessId: 10,
            candidateStartedAtUtc: currentStartedAt.AddSeconds(-1),
            currentProcessId: 10,
            currentProcessStartedAtUtc: currentStartedAt));
    }
}
