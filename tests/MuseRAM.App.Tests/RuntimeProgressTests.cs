using MuseRAM.App;
using MuseRAM.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuseRAM.App.Tests;

public sealed class RuntimeProgressTests
{
    [Fact]
    public void RuntimeProgressSchemaIsVersionFive()
    {
        Assert.Equal(5, RuntimeProgressStore.CurrentSchemaVersion);
    }

    [Fact]
    public void SaveAndLoadPreservesSafeProgressWithoutCountingClosedTime()
    {
        var path = TestPath();
        var savedAt = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new RuntimeProgressStore(path);
            store.Save(Document(savedAt));

            var result = store.LoadWithStatus(savedAt + TimeSpan.FromMinutes(10));

            Assert.Null(result.ErrorMessage);
            var progress = Assert.IsType<RuntimeProgressDocument>(result.Progress);
            Assert.Equal(120, progress.ScheduledOptimizationElapsedSeconds);
            Assert.Equal(512, progress.RecentTrimBytes);
            Assert.Equal(-128, progress.RecentNetGainBytes);
            Assert.Equal(savedAt + TimeSpan.FromMinutes(8),
                RuntimeProgressPolicy.RestoreAnchor(progress.ScheduledOptimizationElapsedSeconds, savedAt + TimeSpan.FromMinutes(10)));
            Assert.Equal(90, Assert.Single(progress.Activities).IdleSeconds);
            Assert.Equal(30, Assert.Single(progress.TrimHistory).ElapsedSeconds);
            Assert.Equal(45, Assert.Single(progress.Backoffs).RemainingBlockSeconds);
            Assert.Equal(3600, progress.SessionUptimeSeconds);
            Assert.Single(progress.NaturalStableObservations!);
            var reviewSession = Assert.Single(progress.HistoricalReviewSessions!);
            Assert.Equal("launch-1", reviewSession.LaunchSignature);
            Assert.Equal(2, reviewSession.CompletedReviewCount);
            Assert.Equal(TimeSpan.FromHours(1), RuntimeProgressPolicy.RestoreDuration(progress.SessionUptimeSeconds));
            var ruleTarget = Assert.Single(progress.ApplicationRuleTargets!);
            Assert.Equal("rule-1", ruleTarget.RuleId);
            Assert.Equal(1, ruleTarget.DelayExecutionsCompleted);
            Assert.Single(ruleTarget.Processes);
            Assert.Equal(TimeSpan.Zero, RuntimeProgressPolicy.RestoreDuration(double.NaN));
            Assert.Equal(RuntimeProgressPolicy.MaximumRestorableDuration,
                RuntimeProgressPolicy.RestoreDuration(double.MaxValue));
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void SaveClampsEveryRestorableDurationToASafeRange()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        var maximum = RuntimeProgressPolicy.MaximumRestorableDuration.TotalSeconds;
        try
        {
            var source = Document(savedAt) with
            {
                ScheduledOptimizationElapsedSeconds = double.MaxValue,
                LastSuccessfulOptimizationElapsedSeconds = double.MaxValue,
                AutomaticSafetyElapsedSeconds = double.MaxValue,
                SessionUptimeSeconds = double.MaxValue,
                Activities = new[]
                {
                    new RuntimeActivityProgress("edge", 42, 1001, double.MaxValue, double.MaxValue, 8)
                },
                TrimHistory = new[] { new RuntimeTrimProgress(42, 1001, double.MaxValue) },
                Backoffs = new[]
                {
                    new ApplicationBackoffProgress("edge", 1, double.MaxValue, double.MaxValue, false, false)
                }
            };
            var store = new RuntimeProgressStore(path);
            store.Save(source);

            var progress = Assert.IsType<RuntimeProgressDocument>(store.LoadWithStatus(savedAt).Progress);

            Assert.Equal(maximum, progress.ScheduledOptimizationElapsedSeconds);
            Assert.Equal(maximum, progress.LastSuccessfulOptimizationElapsedSeconds);
            Assert.Equal(maximum, progress.AutomaticSafetyElapsedSeconds);
            Assert.Equal(maximum, progress.SessionUptimeSeconds);
            Assert.Equal(maximum, Assert.Single(progress.Activities).ObservedSeconds);
            Assert.Equal(maximum, Assert.Single(progress.Activities).IdleSeconds);
            Assert.Equal(maximum, Assert.Single(progress.TrimHistory).ElapsedSeconds);
            Assert.Equal(maximum, Assert.Single(progress.Backoffs).RemainingBlockSeconds);
            Assert.Equal(maximum, Assert.Single(progress.Backoffs).LongTermObservedSeconds);
            Assert.Equal(savedAt - RuntimeProgressPolicy.MaximumRestorableDuration,
                RuntimeProgressPolicy.RestoreAnchor(double.MaxValue, savedAt));
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void SnapshotOlderThanMaximumAgeDiscardsAllProgressIncludingSessionUptime()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var store = new RuntimeProgressStore(path);
            store.Save(Document(savedAt));

            var result = store.LoadWithStatus(savedAt + RuntimeProgressPolicy.MaximumSnapshotAge + TimeSpan.FromSeconds(1));

            Assert.Null(result.Progress);
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void VersionOneSnapshotWithoutSessionUptimeLoadsWithZeroUptime()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var legacy = JsonSerializer.SerializeToNode(Document(savedAt) with { SchemaVersion = 1 })!.AsObject();
            legacy.Remove(nameof(RuntimeProgressDocument.SessionUptimeSeconds));
            legacy.Remove(nameof(RuntimeProgressDocument.ApplicationRuleTargets));
            legacy.Remove(nameof(RuntimeProgressDocument.NaturalStableObservations));
            File.WriteAllText(path, legacy.ToJsonString());

            var result = new RuntimeProgressStore(path).LoadWithStatus(savedAt);

            Assert.Null(result.ErrorMessage);
            var progress = Assert.IsType<RuntimeProgressDocument>(result.Progress);
            Assert.Equal(RuntimeProgressStore.CurrentSchemaVersion, progress.SchemaVersion);
            Assert.Equal(0, progress.SessionUptimeSeconds);
            Assert.Empty(progress.ApplicationRuleTargets!);
            Assert.Empty(progress.NaturalStableObservations!);
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void VersionFourSnapshotWithoutRecentMetricsLoadsWithNoRecentValues()
    {
        var path = TestPath();
        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            var legacy = JsonSerializer.SerializeToNode(Document(savedAt) with { SchemaVersion = 4 })!.AsObject();
            legacy.Remove(nameof(RuntimeProgressDocument.RecentTrimBytes));
            legacy.Remove(nameof(RuntimeProgressDocument.RecentNetGainBytes));
            File.WriteAllText(path, legacy.ToJsonString());

            var progress = Assert.IsType<RuntimeProgressDocument>(
                new RuntimeProgressStore(path).LoadWithStatus(savedAt).Progress);

            Assert.Equal(RuntimeProgressStore.CurrentSchemaVersion, progress.SchemaVersion);
            Assert.Null(progress.RecentTrimBytes);
            Assert.Null(progress.RecentNetGainBytes);
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void MalformedSnapshotIsReportedWithoutOverwritingIt()
    {
        var path = TestPath();
        try
        {
            const string malformed = "{ not-json";
            File.WriteAllText(path, malformed);

            var result = new RuntimeProgressStore(path).LoadWithStatus(DateTimeOffset.UtcNow);

            Assert.Null(result.Progress);
            Assert.NotNull(result.ErrorMessage);
            Assert.Equal(malformed, File.ReadAllText(path));
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    [Fact]
    public void DeleteRemovesSnapshotAndInterruptedTemporaryFile()
    {
        var path = TestPath();
        File.WriteAllText(path, "snapshot");
        File.WriteAllText(path + ".tmp", "temporary");

        new RuntimeProgressStore(path).Delete();

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static RuntimeProgressDocument Document(DateTimeOffset savedAt) => new(
        RuntimeProgressStore.CurrentSchemaVersion,
        savedAt,
        ScheduledOptimizationElapsedSeconds: 120,
        LastSuccessfulOptimizationElapsedSeconds: 60,
        AutomaticSafetyElapsedSeconds: 15,
        CumulativeTrimBytes: 1024,
        CumulativeNetGainBytes: -256,
        RecentTrimBytes: 512,
        RecentNetGainBytes: -128,
        Activities: new[] { new RuntimeActivityProgress("edge", 42, 1001, 120, 90, 8) },
        TrimHistory: new[] { new RuntimeTrimProgress(42, 1001, 30) },
        Backoffs: new[] { new ApplicationBackoffProgress("edge", 1, 45, null, false, false) },
        SessionUptimeSeconds: 3600,
        ApplicationRuleTargets: new[]
        {
            new ApplicationOptimizationRuleTargetProgress(
                "rule-1", "Executable|C:\\Demo\\demo.exe", 1, "config", "42|1001", 1,
                30, 30, 1024, null, null,
                new[] { new ApplicationOptimizationRuleProcessProgress("42|1001", 30, 30) })
        },
        NaturalStableObservations: new[]
        {
            new NaturalStableObservationProgress(
                "edge", "edge|scope:main", "launch-1",
                savedAt - TimeSpan.FromMinutes(3), DateTimeOffset.MaxValue,
                new[] { "edge|component:main" }, 200, 204, 202, 3,
                new[] { new NaturalStableTimedSampleProgress(savedAt, 202, true) },
                TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), savedAt,
                true, false, NaturalStableObservationOrigin.PostTrim)
        },
        HistoricalReviewSessions: new[]
        {
            new HistoricalReviewSessionProgress("edge|scope:main", "launch-1", 2)
        });

    private static string TestPath() =>
        Path.Combine(Path.GetTempPath(), $"museram-runtime-{Guid.NewGuid():N}.json");

    private static void DeleteFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
