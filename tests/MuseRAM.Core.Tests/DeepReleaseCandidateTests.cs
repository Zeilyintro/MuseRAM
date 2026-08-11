namespace MuseRAM.Core.Tests;

public sealed class DeepReleaseCandidateTests
{
    [Fact]
    public void UsesAggregatedFamilyWorkingSetForBackgroundThreshold()
    {
        var family = Family("tiny", Process(1, "tiny", 7), Process(2, "tiny-helper", 7));

        var candidate = Assert.Single(Candidates(new[] { family }));

        Assert.Equal(14L * 1024 * 1024, candidate.Family.WorkingSetBytes);
        Assert.False(candidate.IsSuggested);
    }

    [Fact]
    public void ExcludesBackgroundFamilyBelowTwelveMiB()
    {
        Assert.Empty(Candidates(new[] { Family("small", Process(3, "small", 11)) }));
    }

    [Fact]
    public void DoesNotSuggestForegroundOrActiveFamilies()
    {
        var foreground = Family("foreground", Process(4, "foreground", 300, isForeground: true));
        var active = Family("active", Process(5, "active", 300, cpuPercent: 20));
        var activity = new Dictionary<string, BackgroundActivity>
        {
            [foreground.Key] = Idle(foreground.Key),
            [active.Key] = Idle(active.Key)
        };

        var candidates = Candidates(new[] { foreground, active }, activity);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.False(candidate.IsSuggested));
    }

    [Fact]
    public void UnreliableActivityNeverAgesIntoIdleOrSuggested()
    {
        var tracker = new BackgroundActivityTracker();
        var family = Family("unreliable", Process(14, "unreliable", 300, hasReliableActivitySample: false));
        var startedAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z");
        IReadOnlyDictionary<string, BackgroundActivity> activity = new Dictionary<string, BackgroundActivity>();

        for (var sample = 0; sample < 8; sample++)
        {
            activity = tracker.Observe(new[] { family }, startedAt.AddSeconds(sample * 15));
        }

        Assert.Equal(BackgroundActivityState.Observing, activity[family.Key].State);
        Assert.Equal(0, activity[family.Key].SampleCount);
        Assert.False(Assert.Single(Candidates(new[] { family }, activity)).IsSuggested);
    }

    [Fact]
    public void UnreliableSampleResetsPreviouslyIdleHistory()
    {
        var tracker = new BackgroundActivityTracker();
        var reliable = Family("editor", Process(15, "editor", 300));
        var unreliable = Family("editor", Process(15, "editor", 300, hasReliableActivitySample: false));
        var startedAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z");

        for (var sample = 0; sample < 5; sample++)
        {
            _ = tracker.Observe(new[] { reliable }, startedAt.AddSeconds(sample * 15));
        }
        Assert.Equal(
            BackgroundActivityState.Idle,
            tracker.Observe(new[] { reliable }, startedAt.AddSeconds(61))[reliable.Key].State);

        Assert.Equal(
            BackgroundActivityState.Observing,
            tracker.Observe(new[] { unreliable }, startedAt.AddSeconds(75))[reliable.Key].State);
        var recovered = tracker.Observe(new[] { reliable }, startedAt.AddSeconds(90))[reliable.Key];

        Assert.Equal(BackgroundActivityState.Observing, recovered.State);
        Assert.Equal(1, recovered.SampleCount);
    }

    [Fact]
    public void NewUnreliableChildDoesNotResetReliableFamilyIdleHistory()
    {
        var tracker = new BackgroundActivityTracker();
        var reliable = Process(18, "browser", 300, hasVisibleWindow: true);
        var family = Family("browser", reliable);
        var startedAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z");

        for (var sample = 0; sample < BackgroundActivityTracker.MinimumSamples; sample++)
        {
            _ = tracker.Observe(new[] { family }, startedAt.AddSeconds(sample * 15));
        }

        var withNewChild = Family(
            "browser",
            reliable,
            Process(19, "browser-helper", 20, hasReliableActivitySample: false));
        var activity = tracker.Observe(new[] { withNewChild }, startedAt.AddSeconds(75))[family.Key];

        Assert.Equal(BackgroundActivityState.Idle, activity.State);
        Assert.Equal(TimeSpan.FromSeconds(75), activity.IdleFor);
    }

    [Fact]
    public void UnreliableForegroundChildStillMarksReliableFamilyWorking()
    {
        var tracker = new BackgroundActivityTracker();
        var family = Family(
            "browser",
            Process(20, "browser", 300),
            Process(21, "browser-helper", 20, isForeground: true, hasReliableActivitySample: false));

        var activity = tracker.Observe(
            new[] { family },
            DateTimeOffset.Parse("2026-07-27T12:00:00Z"))[family.Key];

        Assert.Equal(BackgroundActivityState.Working, activity.State);
    }

    [Fact]
    public void ExcludesProtectedAndSystemFamilies()
    {
        const string protectedPath = @"F:\Apps\Protected\protected.exe";
        var protectedFamily = Family("protected", Process(6, "protected", 200, path: protectedPath));
        var systemFamily = Family("dwm", Process(7, "dwm", 200));
        var normalFamily = Family("notes", Process(8, "notes", 200));

        var candidate = Assert.Single(BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { protectedFamily, systemFamily, normalFamily },
            new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules(new[] { protectedPath })));

        Assert.Equal("notes", candidate.Family.Key);
    }

    [Fact]
    public void PartialProtectionKeepsUnselectedExecutableEligible()
    {
        const string applicationPath = @"F:\Apps\Media\media.exe";
        const string protectedPath = @"F:\Apps\Media\capture.exe";
        const string helperPath = @"F:\Apps\Media\helper.exe";
        var family = Family(
            "media",
            Process(16, "capture", 100, path: protectedPath),
            Process(17, "helper", 80, path: helperPath));
        var rules = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = applicationPath,
                ProtectedExecutablePaths = new List<string> { protectedPath }
            }
        });

        var candidate = Assert.Single(BackgroundActivityTracker.CreateDeepReleaseCandidates(
            new[] { family },
            new Dictionary<string, BackgroundActivity>(),
            rules));

        Assert.Equal(17, Assert.Single(candidate.Family.Processes).ProcessId);
        Assert.Equal(80L * 1024 * 1024, candidate.Family.WorkingSetBytes);
    }

    [Fact]
    public void OrdersByActivityStateBeforeWorkingSet()
    {
        var idle = Family("idle-app", Process(10, "idle-app", 100));
        var observing = Family("observing", Process(11, "observing", 200));
        var working = Family("working", Process(12, "working", 300));
        var visible = Family("visible", Process(13, "visible", 400, hasVisibleWindow: true));
        var activity = new Dictionary<string, BackgroundActivity>
        {
            [idle.Key] = Idle(idle.Key),
            [observing.Key] = Activity(observing.Key, BackgroundActivityState.Observing),
            [working.Key] = Activity(working.Key, BackgroundActivityState.Working),
            [visible.Key] = Activity(visible.Key, BackgroundActivityState.Visible)
        };

        var candidates = Candidates(new[] { visible, working, observing, idle }, activity);

        Assert.Equal(new[] { "idle-app", "observing", "working", "visible" },
            candidates.Select(candidate => candidate.Family.Key));
    }

    [Fact]
    public void CapsCandidateListAtForty()
    {
        var families = Enumerable.Range(1, 45)
            .Select(index => Family($"app-{index:00}", Process(100 + index, $"app{index:00}", 20)))
            .ToArray();

        Assert.Equal(40, Candidates(families).Count);
    }

    private static IReadOnlyList<DeepReleaseCandidate> Candidates(
        IReadOnlyList<ProcessFamilySnapshot> families,
        IReadOnlyDictionary<string, BackgroundActivity>? activity = null) =>
        BackgroundActivityTracker.CreateDeepReleaseCandidates(
            families,
            activity ?? new Dictionary<string, BackgroundActivity>(),
            new ProtectionRules());

    private static ProcessFamilySnapshot Family(string key, params ProcessSnapshot[] processes) =>
        new(key, key, null, processes);

    private static ProcessSnapshot Process(
        int processId,
        string name,
        long workingSetMiB,
        bool isForeground = false,
        bool hasVisibleWindow = false,
        double cpuPercent = 0,
        string? path = null,
        bool hasReliableActivitySample = true,
        long? startTimeFileTimeUtc = 100) =>
        new(processId, name, path, null, workingSetMiB * 1024 * 1024, cpuPercent, 0,
            isForeground, hasVisibleWindow, hasReliableActivitySample, 100,
            StartTimeFileTimeUtc: startTimeFileTimeUtc);

    private static BackgroundActivity Idle(string key) =>
        new(key, BackgroundActivityState.Idle, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10);

    private static BackgroundActivity Activity(string key, BackgroundActivityState state) =>
        new(key, state, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10);
}

public sealed class DeepReleaseExecutionSafetyPolicyTests
{
    [Fact]
    public void KeepsOnlyOriginalProcessesWhoseIdentityStillMatches()
    {
        var selected = Candidate(Family(
            "suite",
            Process(20, startTimeFileTimeUtc: 100),
            Process(21, startTimeFileTimeUtc: 200)));
        var current = Family(
            "suite",
            Process(20, startTimeFileTimeUtc: 100),
            Process(21, startTimeFileTimeUtc: 999),
            Process(22, startTimeFileTimeUtc: 300));

        var safe = Assert.Single(DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates(
            new[] { selected },
            new[] { current },
            new ProtectionRules()));

        Assert.Equal(20, Assert.Single(safe.Family.Processes).ProcessId);
    }

    [Theory]
    [InlineData(true, true, 0, 0)]
    [InlineData(false, false, 20, 0)]
    [InlineData(false, false, 0, 16777216)]
    [InlineData(false, false, 0, 0)]
    public void RejectsForegroundActiveOrUnreliableCurrentFamily(
        bool isForeground,
        bool hasReliableActivitySample,
        double cpuPercent,
        double ioBytesPerSecond)
    {
        var selected = Candidate(Family("app", Process(30, startTimeFileTimeUtc: 100)));
        var current = Family("app", Process(
            30,
            startTimeFileTimeUtc: 100,
            isForeground: isForeground,
            hasReliableActivitySample: hasReliableActivitySample,
            cpuPercent: cpuPercent,
            ioBytesPerSecond: ioBytesPerSecond));

        Assert.Empty(DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates(
            new[] { selected },
            new[] { current },
            new ProtectionRules()));
    }

    [Fact]
    public void RuntimeSafetyRejectsReusedPidAndForegroundFamily()
    {
        var relatedIds = new HashSet<int> { 40, 41 };

        Assert.False(DeepReleaseProcessSafetyPolicy.Evaluate(100, 999, relatedIds, null).CanTrim);
        Assert.False(DeepReleaseProcessSafetyPolicy.Evaluate(100, 100, relatedIds, 41).CanTrim);
        Assert.True(DeepReleaseProcessSafetyPolicy.Evaluate(100, 100, relatedIds, null).CanTrim);
    }

    [Fact]
    public void ExecutionRecheckReappliesProtectionRules()
    {
        const string path = @"F:\Apps\Protected\protected.exe";
        var selected = Candidate(Family("protected", Process(50, startTimeFileTimeUtc: 100, path: path)));
        var current = Family("protected", Process(50, startTimeFileTimeUtc: 100, path: path));

        Assert.Empty(DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates(
            new[] { selected },
            new[] { current },
            new ProtectionRules(new[] { path })));
    }

    [Fact]
    public void ExecutionRecheckRemovesOnlyTheSelectedProtectedExecutable()
    {
        const string applicationPath = @"F:\Apps\Media\media.exe";
        const string protectedPath = @"F:\Apps\Media\capture.exe";
        const string helperPath = @"F:\Apps\Media\helper.exe";
        var selected = Candidate(Family(
            "media",
            Process(60, startTimeFileTimeUtc: 100, path: protectedPath),
            Process(61, startTimeFileTimeUtc: 200, path: helperPath)));
        var current = Family(
            "media",
            Process(60, startTimeFileTimeUtc: 100, path: protectedPath),
            Process(61, startTimeFileTimeUtc: 200, path: helperPath));
        var rules = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = applicationPath,
                ProtectedExecutablePaths = new List<string> { protectedPath }
            }
        });

        var safe = Assert.Single(DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates(
            new[] { selected },
            new[] { current },
            rules));

        Assert.Equal(61, Assert.Single(safe.Family.Processes).ProcessId);
    }

    private static DeepReleaseCandidate Candidate(ProcessFamilySnapshot family) => new(
        family,
        new BackgroundActivity(family.Key, BackgroundActivityState.Idle, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), 10),
        true);

    private static ProcessFamilySnapshot Family(string key, params ProcessSnapshot[] processes) =>
        new(key, key, null, processes);

    private static ProcessSnapshot Process(
        int processId,
        long? startTimeFileTimeUtc,
        bool isForeground = false,
        bool hasReliableActivitySample = true,
        double cpuPercent = 0,
        double ioBytesPerSecond = 0,
        string? path = null) =>
        new(
            processId,
            "app",
            path,
            null,
            300L * 1024 * 1024,
            cpuPercent,
            ioBytesPerSecond,
            isForeground,
            false,
            hasReliableActivitySample,
            100,
            StartTimeFileTimeUtc: startTimeFileTimeUtc);
}
