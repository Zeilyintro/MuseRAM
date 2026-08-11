using MuseRAM.App;

namespace MuseRAM.App.Tests;

public sealed class ReboundHistoryTests
{
    [Fact]
    public void SaveAndLoadPreservesEveryRunInTheEffectiveSession()
    {
        var path = TestPath();
        var savedAt = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var runs = Enumerable.Range(1, 12).Select(sequence => Run(sequence, savedAt)).ToArray();
            var store = new ReboundHistoryStore(path);

            store.Save(runs, savedAt);
            var result = store.LoadWithStatus(savedAt + TimeSpan.FromMinutes(10));

            Assert.Null(result.ErrorMessage);
            var history = Assert.IsType<ReboundHistoryDocument>(result.History);
            Assert.Equal(12, history.Runs.Count);
            Assert.Equal(12, history.Runs[0].Sequence);
            Assert.Equal(1, history.Runs[^1].Sequence);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void SnapshotExpiresAfterFifteenMinutes()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var store = new ReboundHistoryStore(path);
            store.Save(new[] { Run(1, savedAt) }, savedAt);

            Assert.NotNull(store.LoadWithStatus(savedAt + ReboundHistoryStore.MaximumSnapshotAge).History);
            var expired = store.LoadWithStatus(
                savedAt + ReboundHistoryStore.MaximumSnapshotAge + TimeSpan.FromSeconds(1));

            Assert.Null(expired.History);
            Assert.Null(expired.ErrorMessage);
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void LoadNormalizesInvalidDetailsWithoutDroppingValidRuns()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var run = Run(1, savedAt) with
            {
                Details = new[]
                {
                    new ReboundHistoryDetail(" Editor ", 100, 150),
                    new ReboundHistoryDetail(" ", 100, 10),
                    new ReboundHistoryDetail("Invalid", 0, 0)
                }
            };
            var store = new ReboundHistoryStore(path);

            store.Save(new[] { run }, savedAt);
            var history = Assert.IsType<ReboundHistoryDocument>(
                store.LoadWithStatus(savedAt).History);

            var detail = Assert.Single(Assert.Single(history.Runs).Details);
            Assert.Equal("Editor", detail.DisplayName);
            Assert.Equal(100, detail.ReleasedBytes);
            Assert.Equal(100, detail.RegainedBytes);
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    private static ReboundHistoryRun Run(int sequence, DateTimeOffset startedAt) => new(
        sequence,
        OptimizationRunKind.Automatic,
        startedAt + TimeSpan.FromMinutes(sequence),
        startedAt + TimeSpan.FromMinutes(sequence) + TimeSpan.FromMinutes(2),
        ReboundObservationState.Completed,
        new[] { new ReboundHistoryDetail($"App {sequence}", 1024, 256) });

    private static string TestPath() =>
        Path.Combine(Path.GetTempPath(), $"museram-rebound-{Guid.NewGuid():N}.json");

    private static void DeleteFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
