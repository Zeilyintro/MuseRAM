using MuseRAM.Core;

namespace MuseRAM.Core.Tests;

public sealed class ApplicationOptimizationRuleTests
{
    [Fact]
    public void NormalizeRulesKeepsTargetTypeInIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "MuseRAM", "Demo", "demo.exe");
        var rules = ApplicationOptimizationRulePolicy.NormalizeRules(new[]
        {
            new ApplicationOptimizationRule
            {
                Id = "same",
                Targets = new()
                {
                    new() { TargetType = ApplicationOptimizationTargetType.ApplicationFamily, Path = path },
                    new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = path }
                }
            }
        });

        Assert.Single(rules);
        Assert.Equal(2, rules[0].Targets.Count);
        Assert.Equal(512L * 1024 * 1024, rules[0].WorkingSetThresholdBytes);
    }

    [Fact]
    public void LegacyRuleBypassMigratesOnlyExplicitlyMissingFlags()
    {
        var path = Path.Combine(Path.GetTempPath(), "MuseRAM", "Demo", "demo.exe");
        var siblingPath = Path.Combine(Path.GetTempPath(), "MuseRAM", "Demo", "sibling.exe");
        var migrated = ApplicationOptimizationRulePolicy.NormalizeRules(new[]
        {
            new ApplicationOptimizationRule
            {
                Id = "legacy-bypass",
                BypassProtection = true,
                Targets = new()
                {
                    new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = path }
                }
            }
        });
        var current = ApplicationOptimizationRulePolicy.NormalizeRules(new[]
        {
            new ApplicationOptimizationRule
            {
                Id = "current-bypass",
                BypassProtection = true,
                Targets = new()
                {
                    new()
                    {
                        TargetType = ApplicationOptimizationTargetType.Executable,
                        Path = path,
                        BypassProtectionConfirmed = true
                    },
                    new()
                    {
                        TargetType = ApplicationOptimizationTargetType.Executable,
                        Path = siblingPath,
                        BypassProtectionConfirmed = false
                    }
                }
            }
        });

        Assert.True(migrated[0].Targets[0].BypassProtectionConfirmed);
        Assert.True(current[0].Targets[0].BypassProtectionConfirmed);
        Assert.False(current[0].Targets[1].BypassProtectionConfirmed);
    }

    [Fact]
    public void WorkingSetTriggerRequiresTwoReliableSamplesForSameIdentity()
    {
        var process = Process("demo.exe", 10, 200L * 1024 * 1024, reliable: true);
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family });
        Assert.False(runtime.IsWorkingSetReady(rule, process));
        runtime.ObserveWorkingSet(rule, new[] { family });
        Assert.True(runtime.IsWorkingSetReady(rule, process));
    }

    [Fact]
    public void WorkingSetTriggerDoesNotReusePidAfterCreationTimeChanges()
    {
        var first = Process("demo.exe", 10, 200L * 1024 * 1024, startTime: 100);
        var replacement = first with { StartTimeFileTimeUtc = 200 };
        var rule = Rule(ApplicationOptimizationTargetType.Executable, first.ExecutablePath!);
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { Family(first) });
        runtime.ObserveWorkingSet(rule, new[] { Family(first) });
        Assert.True(runtime.IsWorkingSetReady(rule, first));

        runtime.ObserveWorkingSet(rule, new[] { Family(replacement) });
        Assert.False(runtime.IsWorkingSetReady(rule, replacement));
    }

    [Fact]
    public void WorkingSetGateCannotMakeRuleDueBeforeDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process("demo.exe", 10, 200L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = false;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { Family(process) });
        runtime.ObserveWorkingSet(rule, new[] { Family(process) });
        var decision = Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { Family(process) }, now.AddHours(-1), now));

        Assert.True(decision.WorkingSetThresholdSatisfied);
        Assert.False(decision.DelayDue);
        Assert.False(decision.IsDue);
    }

    [Fact]
    public void DelayDueWithoutWorkingSetGateMakesRuleDue()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process("demo.exe", 10, 20L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;

        var decision = Assert.Single(new ApplicationOptimizationRuleRuntime().GetTargetDecisions(
            rule, new[] { Family(process) }, now.AddHours(-1), now));

        Assert.True(decision.DelayDue);
        Assert.True(decision.WorkingSetThresholdSatisfied);
        Assert.True(decision.IsDue);
    }

    [Fact]
    public void WorkingSetGateUsesApplicationFamilyTotalAcrossReliableSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var main = Process("demo.exe", 10, 60L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var helper = Process("helper.exe", 11, 60L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime(), executablePath: "C:\\Demo\\helper.exe");
        var family = Family("family", main, helper);
        var rule = Rule(ApplicationOptimizationTargetType.ApplicationFamily, main.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family });
        Assert.False(Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now)).IsDue);
        runtime.ObserveWorkingSet(rule, new[] { family });
        var decision = Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now));

        Assert.True(decision.WorkingSetThresholdSatisfied);
        Assert.True(decision.IsDue);
        Assert.Equal(2, decision.WorkingSetDueProcesses.Count);
    }

    [Fact]
    public void WorkingSetGateCanFollowCurrentProfileAndResetsWhenItChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process("demo.exe", 10, 150L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdFollowsProfile = true;
        rule.WorkingSetThresholdBytes = 500L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family }, 100L * 1024 * 1024);
        runtime.ObserveWorkingSet(rule, new[] { family }, 100L * 1024 * 1024);
        Assert.True(Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now)).IsDue);

        runtime.ObserveWorkingSet(rule, new[] { family }, 200L * 1024 * 1024);
        var afterProfileChange = Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now));
        Assert.False(afterProfileChange.WorkingSetThresholdSatisfied);
        Assert.False(afterProfileChange.IsDue);
    }

    [Fact]
    public void LegacyWorkingSetGateKeepsItsFixedThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process("demo.exe", 10, 150L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdFollowsProfile = null;
        rule.WorkingSetThresholdBytes = 200L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family }, 100L * 1024 * 1024);
        runtime.ObserveWorkingSet(rule, new[] { family }, 100L * 1024 * 1024);
        var decision = Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now));

        Assert.False(decision.WorkingSetThresholdSatisfied);
        Assert.False(decision.IsDue);
    }

    [Fact]
    public void CandidateCreationCannotBypassAnUnmetWorkingSetGate()
    {
        var process = Process("demo.exe", 10, 150L * 1024 * 1024);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 200L * 1024 * 1024;

        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            new[] { Family(process) },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);

        Assert.Empty(candidates);
    }

    [Fact]
    public void WorkingSetTriggerRejectsMissingOrInvalidCreationTime()
    {
        var invalid = Process("demo.exe", 10, 200L * 1024 * 1024, startTime: 0);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, invalid.ExecutablePath!);
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { Family(invalid) });
        runtime.ObserveWorkingSet(rule, new[] { Family(invalid) });

        Assert.False(runtime.IsWorkingSetReady(rule, invalid));
    }

    [Fact]
    public void WorkingSetSamplesResetWhenRuleIsDisabledOrThresholdChanges()
    {
        var process = Process("demo.exe", 10, 200L * 1024 * 1024, reliable: true);
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family });
        rule.WorkingSetTriggerEnabled = false;
        runtime.ObserveWorkingSet(rule, new[] { family });
        rule.WorkingSetTriggerEnabled = true;
        runtime.ObserveWorkingSet(rule, new[] { family });
        Assert.False(runtime.IsWorkingSetReady(rule, process));

        rule.WorkingSetThresholdBytes = 150L * 1024 * 1024;
        runtime.ObserveWorkingSet(rule, new[] { family });
        Assert.False(runtime.IsWorkingSetReady(rule, process));
    }

    [Fact]
    public void ConfigurationRevisionDoesNotResetAnUnaffectedTarget()
    {
        var processA = Process("a.exe", 10, 200L * 1024 * 1024, executablePath: "C:\\Demo\\a.exe");
        var processB = Process("b.exe", 11, 200L * 1024 * 1024, executablePath: "C:\\Demo\\b.exe");
        var rule = new ApplicationOptimizationRule
        {
            Id = "revision-target",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            },
            WorkingSetTriggerEnabled = true,
            WorkingSetThresholdBytes = 100L * 1024 * 1024
        };
        var runtime = new ApplicationOptimizationRuleRuntime();
        var families = new[] { Family("a", processA), Family("b", processB) };

        runtime.ObserveWorkingSet(rule, families);
        runtime.ObserveWorkingSet(rule, families);
        Assert.True(runtime.IsWorkingSetReady(rule, processA));
        Assert.True(runtime.IsWorkingSetReady(rule, processB));

        rule.ConfigurationRevision++;
        runtime.ObserveWorkingSet(rule, families);

        Assert.True(runtime.IsWorkingSetReady(rule, processA));
        Assert.True(runtime.IsWorkingSetReady(rule, processB));
    }

    [Fact]
    public void DelayTriggerWaitsForTargetStartupAndConfiguredDelay()
    {
        var process = Process("demo.exe", 10, 200L * 1024 * 1024, startTime: DateTimeOffset.UtcNow.AddMinutes(-5).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;
        var now = DateTimeOffset.UtcNow;
        var match = Assert.Single(ApplicationOptimizationRulePolicy.ResolveMatches(rule, new[] { family }));

        Assert.True(ApplicationOptimizationRulePolicy.IsDelayDue(
            rule, match, now.AddHours(-1), now, null, 0));
    }

    [Fact]
    public void DelayTriggerCanExecuteTheSameProcessThreeTimesAtConfiguredIntervals()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process(
            "demo.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayAnchor = ApplicationOptimizationDelayAnchor.MuseRamStartup;
        rule.DelayMinutes = 5;
        rule.ExecutionCount = 3;
        rule.ExecutionIntervalMinutes = 10;
        var runtime = new ApplicationOptimizationRuleRuntime();
        var museRamStartedAt = now.AddMinutes(-5);

        var first = runtime.GetTargetDecisions(rule, new[] { family }, museRamStartedAt, now);
        Assert.True(Assert.Single(first).DelayDue);
        runtime.RecordSuccessfulExecution(rule, first, new[] { process }, now, now);

        var beforeSecondInterval = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            museRamStartedAt,
            now.AddMinutes(9));
        Assert.False(Assert.Single(beforeSecondInterval).DelayDue);

        var second = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            museRamStartedAt,
            now.AddMinutes(10));
        Assert.True(Assert.Single(second).DelayDue);
        runtime.RecordSuccessfulExecution(
            rule,
            second,
            new[] { process },
            now.AddMinutes(10),
            now.AddMinutes(10));

        var third = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            museRamStartedAt,
            now.AddMinutes(20));
        Assert.True(Assert.Single(third).DelayDue);
        runtime.RecordSuccessfulExecution(
            rule,
            third,
            new[] { process },
            now.AddMinutes(20),
            now.AddMinutes(20));

        var afterConfiguredCount = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            museRamStartedAt,
            now.AddMinutes(30));
        Assert.False(Assert.Single(afterConfiguredCount).DelayDue);
        Assert.Equal(3, runtime.GetTargetState(rule, rule.Targets[0]).DelayExecutionsCompleted);
    }

    [Fact]
    public void WorkingSetTriggerCanRunAgainAfterCooldownWithFreshSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process(
            "demo.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        rule.CooldownMinutes = 10;
        rule.ExecutionCount = 1;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family });
        runtime.ObserveWorkingSet(rule, new[] { family });
        var first = runtime.GetTargetDecisions(rule, new[] { family }, now, now);
        Assert.Single(Assert.Single(first).WorkingSetDueProcesses);
        runtime.RecordSuccessfulExecution(rule, first, new[] { process }, now, now);

        var cooling = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            now,
            now.AddMinutes(9));
        Assert.Empty(Assert.Single(cooling).WorkingSetDueProcesses);

        runtime.ObserveWorkingSet(rule, new[] { family });
        runtime.ObserveWorkingSet(rule, new[] { family });
        var second = runtime.GetTargetDecisions(
            rule,
            new[] { family },
            now,
            now.AddMinutes(10));
        Assert.Single(Assert.Single(second).WorkingSetDueProcesses);
        runtime.RecordSuccessfulExecution(
            rule,
            second,
            new[] { process },
            now.AddMinutes(10),
            now.AddMinutes(10));

        Assert.Equal(0, runtime.GetTargetState(rule, rule.Targets[0]).DelayExecutionsCompleted);
    }

    [Fact]
    public void MultipleTargetsAreEvaluatedIndependently()
    {
        var now = DateTimeOffset.UtcNow;
        var processA = Process(
            "a.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\a.exe");
        var processB = Process(
            "b.exe",
            11,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-1).ToFileTime(),
            executablePath: "C:\\Demo\\b.exe");
        var familyA = Family("family-a", processA);
        var familyB = Family("family-b", processB);
        var rule = new ApplicationOptimizationRule
        {
            Id = "multi-target",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            },
            DelayTriggerEnabled = true,
            DelayMinutes = 5,
            ExecutionCount = 1
        };
        var runtime = new ApplicationOptimizationRuleRuntime();
        var families = new[] { familyA, familyB };
        var decisions = runtime.GetTargetDecisions(rule, families, now, now);

        Assert.Equal(2, decisions.Count);
        Assert.True(decisions.Single(decision => decision.Target.Path == processA.ExecutablePath).DelayDue);
        Assert.False(decisions.Single(decision => decision.Target.Path == processB.ExecutablePath).DelayDue);

        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            families,
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            runtime,
            now,
            delayDue: true,
            delayDueTargetIdentities: decisions
                .Where(decision => decision.DelayDue)
                .Select(decision => decision.TargetIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

        Assert.Single(candidates);
        Assert.Equal(10, Assert.Single(candidates).TargetProcesses[0].ProcessId);
    }

    [Fact]
    public void RestartingOneTargetResetsOnlyThatTargetDelayState()
    {
        var now = DateTimeOffset.UtcNow;
        var processA = Process(
            "a.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\a.exe");
        var processB = Process(
            "b.exe",
            11,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\b.exe");
        var replacementA = processA with { StartTimeFileTimeUtc = now.ToFileTime() };
        var rule = new ApplicationOptimizationRule
        {
            Id = "restart-target",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            },
            DelayTriggerEnabled = true,
            DelayAnchor = ApplicationOptimizationDelayAnchor.TargetApplicationStartup,
            DelayMinutes = 5,
            ExecutionCount = 3,
            RestartWithApplication = true
        };
        var runtime = new ApplicationOptimizationRuleRuntime();
        var initialFamilies = new[] { Family("family-a", processA), Family("family-b", processB) };

        runtime.ObserveWorkingSet(rule, initialFamilies);
        var decisions = runtime.GetTargetDecisions(rule, initialFamilies, now, now);
        runtime.RecordSuccessfulExecution(rule, decisions, new[] { processA, processB }, now, now);

        runtime.ObserveWorkingSet(
            rule,
            new[] { Family("family-a", replacementA), Family("family-b", processB) });

        var stateA = runtime.GetTargetState(rule, rule.Targets[0]);
        var stateB = runtime.GetTargetState(rule, rule.Targets[1]);
        Assert.Equal(0, stateA.DelayExecutionsCompleted);
        Assert.Null(stateA.LastDelayExecutionAt);
        Assert.Equal(1, stateB.DelayExecutionsCompleted);
        Assert.Equal(now, stateB.LastDelayExecutionAt);
    }

    [Fact]
    public void SuccessfulExecutionUpdatesOnlyTheTargetWhoseProcessSucceeded()
    {
        var now = DateTimeOffset.UtcNow;
        var processA = Process(
            "a.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\a.exe");
        var processB = Process(
            "b.exe",
            11,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\b.exe");
        var families = new[] { Family("family-a", processA), Family("family-b", processB) };
        var rule = new ApplicationOptimizationRule
        {
            Id = "partial-success",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            },
            DelayTriggerEnabled = true,
            DelayMinutes = 5,
            ExecutionCount = 3,
            WorkingSetTriggerEnabled = true,
            WorkingSetThresholdBytes = 100L * 1024 * 1024
        };
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, families);
        runtime.ObserveWorkingSet(rule, families);
        var decisions = runtime.GetTargetDecisions(rule, families, now, now);
        runtime.RecordSuccessfulExecution(rule, decisions, new[] { processA }, now, now);

        var stateA = runtime.GetTargetState(rule, rule.Targets[0]);
        var stateB = runtime.GetTargetState(rule, rule.Targets[1]);
        Assert.Equal(1, stateA.DelayExecutionsCompleted);
        Assert.Equal(now, stateA.LastDelayExecutionAt);
        Assert.Equal(0, stateB.DelayExecutionsCompleted);
        Assert.Null(stateB.LastDelayExecutionAt);
        Assert.Equal(0, stateA.ProcessStates[$"10|{processA.StartTimeFileTimeUtc}"].ConsecutiveReliableWorkingSetSamples);
        Assert.Equal(2, stateB.ProcessStates[$"11|{processB.StartTimeFileTimeUtc}"].ConsecutiveReliableWorkingSetSamples);
        Assert.NotNull(stateA.ProcessStates[$"10|{processA.StartTimeFileTimeUtc}"].LastWorkingSetExecutionAt);
        Assert.Null(stateB.ProcessStates[$"11|{processB.StartTimeFileTimeUtc}"].LastWorkingSetExecutionAt);
    }

    [Fact]
    public void ApplicationRuleCandidatesIgnoreNormalMaxApplicationsLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var processA = Process(
            "a.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\a.exe");
        var processB = Process(
            "b.exe",
            11,
            200L * 1024 * 1024,
            startTime: now.AddMinutes(-10).ToFileTime(),
            executablePath: "C:\\Demo\\b.exe");
        var rule = new ApplicationOptimizationRule
        {
            Id = "max-applications",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            }
        };
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with { MaxApplications = 1 };

        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            new[] { Family("family-a", processA), Family("family-b", processB) },
            settings,
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            new ApplicationOptimizationRuleRuntime(),
            now,
            delayDue: true);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(new[] { 10, 11 }, candidates.SelectMany(candidate => candidate.TargetProcesses)
            .Select(process => process.ProcessId)
            .OrderBy(processId => processId));
    }

    [Fact]
    public void ExplicitRuleIgnoresNormalWorkingSetFloors()
    {
        var process = Process("small.exe", 10, 1L * 1024 * 1024);
        var settings = OptimizationSettings.For(OptimizationProfile.Lite);

        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!),
            new[] { Family(process) },
            settings,
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);

        Assert.True(process.WorkingSetBytes < settings.MinimumProcessWorkingSetBytes);
        Assert.True(process.WorkingSetBytes < settings.MinimumFamilyWorkingSetBytes);
        Assert.Single(candidates);
    }

    [Fact]
    public void FamilyHelperChurnDoesNotRestartDelaySchedule()
    {
        var now = DateTimeOffset.UtcNow;
        var main = Process(
            "demo.exe", 10, 200L * 1024 * 1024,
            startTime: now.AddHours(-1).ToFileTime(),
            executablePath: "C:\\Demo\\demo.exe");
        var helper = Process(
            "helper.exe", 11, 50L * 1024 * 1024,
            startTime: now.ToFileTime(),
            executablePath: "C:\\Demo\\helper.exe");
        var restartedMain = main with { ProcessId = 12, StartTimeFileTimeUtc = now.AddMinutes(1).ToFileTime() };
        var rule = Rule(ApplicationOptimizationTargetType.ApplicationFamily, main.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayAnchor = ApplicationOptimizationDelayAnchor.TargetApplicationStartup;
        rule.DelayMinutes = 5;
        rule.ExecutionCount = 3;
        rule.RestartWithApplication = true;
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { Family("family", main) });
        var decision = runtime.GetTargetDecisions(rule, new[] { Family("family", main) }, now, now);
        runtime.RecordSuccessfulExecution(rule, decision, new[] { main }, now, now);

        runtime.ObserveWorkingSet(rule, new[] { Family("family", main, helper) });
        Assert.Equal(1, runtime.GetTargetState(rule, rule.Targets[0]).DelayExecutionsCompleted);

        runtime.ObserveWorkingSet(rule, new[] { Family("family", restartedMain, helper) });
        Assert.Equal(0, runtime.GetTargetState(rule, rule.Targets[0]).DelayExecutionsCompleted);
    }

    [Fact]
    public void RuntimeProgressRestoresDelayAndCooldownButRequiresFreshWorkingSetSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process(
            "demo.exe", 10, 200L * 1024 * 1024,
            startTime: now.AddHours(-1).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 5;
        rule.ExecutionCount = 3;
        rule.WorkingSetTriggerEnabled = true;
        rule.WorkingSetThresholdBytes = 100L * 1024 * 1024;
        rule.CooldownMinutes = 10;
        var runtime = new ApplicationOptimizationRuleRuntime();
        runtime.ObserveWorkingSet(rule, new[] { family });
        runtime.ObserveWorkingSet(rule, new[] { family });
        var decision = runtime.GetTargetDecisions(rule, new[] { family }, now.AddHours(-2), now);
        runtime.RecordSuccessfulExecution(rule, decision, new[] { process }, now, now);
        var progress = runtime.CaptureProgress(new[] { rule }, now.AddMinutes(1));

        var restored = new ApplicationOptimizationRuleRuntime();
        var restoredAt = now.AddMinutes(2);
        restored.RestoreProgress(progress, new[] { rule }, new[] { family }, restoredAt);
        var state = restored.GetTargetState(rule, rule.Targets[0]);

        Assert.Equal(1, state.DelayExecutionsCompleted);
        Assert.True(restored.IsWorkingSetCooling(rule, rule.Targets[0], process, restoredAt, 10));
        Assert.Equal(0, state.ProcessStates[ApplicationOptimizationRuleRuntime.ProcessIdentity(process)]
            .ConsecutiveReliableWorkingSetSamples);
        Assert.Empty(Assert.Single(restored.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-2), restoredAt)).WorkingSetDueProcesses);
    }

    [Fact]
    public void SuccessfulExecutionAttributesMeasuredReleaseToEachTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var processA = Process("a.exe", 10, 200L * 1024 * 1024, executablePath: "C:\\Demo\\a.exe");
        var processB = Process("b.exe", 11, 200L * 1024 * 1024, executablePath: "C:\\Demo\\b.exe");
        var rule = new ApplicationOptimizationRule
        {
            Id = "release-attribution",
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processA.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = processB.ExecutablePath! }
            },
            DelayTriggerEnabled = true,
            DelayMinutes = 1
        };
        var families = new[] { Family("a", processA), Family("b", processB) };
        var runtime = new ApplicationOptimizationRuleRuntime();
        runtime.ObserveWorkingSet(rule, families);
        var decisions = runtime.GetTargetDecisions(rule, families, now.AddHours(-1), now);

        runtime.RecordSuccessfulExecution(
            rule,
            decisions,
            new[] { processA, processB },
            now,
            now,
            releasedBytes: 300,
            releasedBytesByProcessIdentity: new Dictionary<string, long>
            {
                [ApplicationOptimizationRuleRuntime.ProcessIdentity(processA)] = 100,
                [ApplicationOptimizationRuleRuntime.ProcessIdentity(processB)] = 200
            });

        Assert.Equal(100, runtime.GetTargetState(rule, rule.Targets[0]).LastReleasedBytes);
        Assert.Equal(200, runtime.GetTargetState(rule, rule.Targets[1]).LastReleasedBytes);
        runtime.RecordRetainedOutcome(rule.Id, decisions[0].TargetIdentity, now, 60);
        Assert.Equal(60, runtime.GetTargetState(rule, rule.Targets[0]).LastRetainedBytes);
    }

    [Fact]
    public void ExecutableRuleDoesNotIncludeSiblingExecutableFromTheSameFamily()
    {
        var target = Process(
            "target.exe",
            10,
            200L * 1024 * 1024,
            executablePath: "C:\\Demo\\target.exe");
        var sibling = Process(
            "sibling.exe",
            11,
            200L * 1024 * 1024,
            executablePath: "C:\\Demo\\sibling.exe");
        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            Rule(ApplicationOptimizationTargetType.Executable, target.ExecutablePath!),
            new[] { Family("same-family", target, sibling) },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);

        var candidate = Assert.Single(candidates);
        Assert.Equal(10, Assert.Single(candidate.TargetProcesses).ProcessId);
    }

    [Fact]
    public void ApplicationRuleExcludesComponentsBlockedByLearningState()
    {
        var process = Process(
            "target.exe",
            10,
            200L * 1024 * 1024,
            executablePath: "C:\\Demo\\target.exe");
        var family = Family("same-family", process);
        var componentKey = ApplicationComponentIdentity.ForProcess(family.Key, process);

        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true,
            coolingComponentKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { componentKey });

        Assert.Empty(candidates);
    }

    [Fact]
    public void ApplicationRuleStillRejectsCurrentCpuOrIoActivity()
    {
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo);
        var activeCpu = Process("cpu.exe", 10, 200L * 1024 * 1024) with
        {
            CpuPercent = settings.ActiveCpuThresholdPercent
        };
        var activeIo = Process("io.exe", 11, 200L * 1024 * 1024) with
        {
            IoBytesPerSecond = settings.ActiveIoThresholdBytesPerSecond
        };
        var protection = new ProtectionRules(Array.Empty<ApplicationProtectionRule>());

        var cpuCandidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            Rule(ApplicationOptimizationTargetType.Executable, activeCpu.ExecutablePath!),
            new[] { Family(activeCpu) },
            settings,
            protection,
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);
        var ioCandidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            Rule(ApplicationOptimizationTargetType.Executable, activeIo.ExecutablePath!),
            new[] { Family(activeIo) },
            settings,
            protection,
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);

        Assert.Empty(cpuCandidates);
        Assert.Empty(ioCandidates);
    }

    [Fact]
    public void ApplicationRuleMayTrimForegroundOnlyWhenEnhancedSafetyIsOff()
    {
        var foreground = Process("foreground.exe", 12, 200L * 1024 * 1024) with
        {
            IsForeground = true
        };
        var rule = Rule(ApplicationOptimizationTargetType.Executable, foreground.ExecutablePath!);
        var protection = new ProtectionRules(Array.Empty<ApplicationProtectionRule>());

        var allowed = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            new[] { Family(foreground) },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            protection,
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);
        var blocked = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            new[] { Family(foreground) },
            OptimizationSettings.For(OptimizationProfile.Turbo) with { EnhancedSafety = true },
            protection,
            new ApplicationOptimizationRuleRuntime(),
            DateTimeOffset.UtcNow,
            delayDue: true);

        Assert.Single(allowed);
        Assert.Empty(blocked);
    }

    [Fact]
    public void FollowAutomaticRulesStopWithGlobalAutomaticOptimizationAndKeepTargetsIndependent()
    {
        var first = Process("first.exe", 10, 200L * 1024 * 1024, executablePath: "C:\\Demo\\first.exe");
        var second = Process("second.exe", 11, 300L * 1024 * 1024, executablePath: "C:\\Other\\second.exe");
        var rule = new ApplicationOptimizationRule
        {
            Id = "follow-auto",
            TriggerMode = ApplicationOptimizationRuleTriggerMode.FollowAutomatic,
            WorkingSetThresholdBytes = 256L * 1024 * 1024,
            Targets = new()
            {
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = first.ExecutablePath! },
                new() { TargetType = ApplicationOptimizationTargetType.Executable, Path = second.ExecutablePath! }
            }
        };
        var families = new[] { Family("first", first), Family("second", second) };

        Assert.Empty(ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
            new[] { rule }, families, automaticOptimizationEnabled: false));
        var overrides = ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
            new[] { rule }, families, automaticOptimizationEnabled: true);

        Assert.Equal(2, overrides.Count);
        Assert.Contains(overrides, item => item.FamilyKey == "first" && item.ProcessIds.SetEquals(new[] { 10 }));
        Assert.Contains(overrides, item => item.FamilyKey == "second" && item.ProcessIds.SetEquals(new[] { 11 }));
        Assert.All(overrides, item => Assert.Equal(256L * 1024 * 1024, item.ThresholdBytes));
    }

    [Fact]
    public void FollowAutomaticThresholdReplacesWorkingSetMinimumOnlyForItsTarget()
    {
        var target = Process("target.exe", 10, 96L * 1024 * 1024, executablePath: "C:\\Demo\\target.exe");
        var other = Process("other.exe", 11, 96L * 1024 * 1024, executablePath: "C:\\Other\\other.exe");
        var families = new[] { Family("target", target), Family("other", other) };
        var settings = OptimizationSettings.For(OptimizationProfile.Turbo) with
        {
            MinimumProcessWorkingSetBytes = 128L * 1024 * 1024,
            MinimumFamilyWorkingSetBytes = 256L * 1024 * 1024,
            QuickCandidateSelection = true
        };
        var readiness = new CandidateIdleTracker().Observe(families, settings);
        var planner = new OptimizationPlanner();
        var memory = new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94);
        var eligibleOverride = new AutomaticOptimizationThresholdOverride(
            "rule|target", "target", new HashSet<int> { 10 }, 64L * 1024 * 1024);

        var eligible = planner.CreatePlan(
            memory,
            families,
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            candidateIdleReadiness: readiness,
            automaticThresholdOverrides: new[] { eligibleOverride });
        var blocked = planner.CreatePlan(
            memory,
            families,
            settings,
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false,
            candidateIdleReadiness: readiness,
            automaticThresholdOverrides: new[]
            {
                eligibleOverride with { ThresholdBytes = 128L * 1024 * 1024 }
            });

        var candidate = Assert.Single(eligible.Candidates);
        Assert.Equal(10, Assert.Single(candidate.TargetProcesses).ProcessId);
        Assert.Empty(blocked.Candidates);
    }

    [Fact]
    public void FixedExecutableGroupCombinesOnlyItsSelectedExecutableWorkingSets()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Process("first.exe", 10, 60L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime(), executablePath: "C:\\Demo\\first.exe");
        var second = Process("second.exe", 11, 60L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime(), executablePath: "C:\\Demo\\second.exe");
        var unselected = Process("other.exe", 12, 500L * 1024 * 1024,
            startTime: now.AddMinutes(-20).ToFileTime(), executablePath: "C:\\Demo\\other.exe");
        var family = Family("demo", first, second, unselected);
        var target = new ApplicationOptimizationRuleTarget
        {
            TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
            Path = first.ExecutablePath!,
            ExecutablePaths = new() { first.ExecutablePath!, second.ExecutablePath! },
            BypassProtectionConfirmed = true
        };
        var rule = new ApplicationOptimizationRule
        {
            Id = "fixed-group",
            Targets = new() { target },
            DelayTriggerEnabled = true,
            DelayMinutes = 1,
            WorkingSetTriggerEnabled = true,
            WorkingSetThresholdBytes = 100L * 1024 * 1024
        };
        var runtime = new ApplicationOptimizationRuleRuntime();

        runtime.ObserveWorkingSet(rule, new[] { family });
        runtime.ObserveWorkingSet(rule, new[] { family });
        var decision = Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now));
        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
            rule,
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            runtime,
            now,
            delayDue: true,
            workingSetDueTargetIdentities: new HashSet<string> { decision.TargetIdentity });

        Assert.True(decision.IsDue);
        Assert.Equal(new[] { 10, 11 }, decision.Matches.SelectMany(match => match.Processes)
            .Select(process => process.ProcessId).OrderBy(id => id));
        Assert.Equal(new[] { 10, 11 }, Assert.Single(candidates).TargetProcesses
            .Select(process => process.ProcessId).OrderBy(id => id));
    }

    [Fact]
    public void FixedExecutableGroupOverlapsAnyExactExecutableInsideItsSnapshot()
    {
        var first = new ApplicationOptimizationRuleTarget
        {
            TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
            Path = "C:\\Demo\\first.exe",
            ExecutablePaths = new() { "C:\\Demo\\first.exe", "C:\\Demo\\second.exe" }
        };
        var member = new ApplicationOptimizationRuleTarget
        {
            TargetType = ApplicationOptimizationTargetType.Executable,
            Path = "C:\\Demo\\second.exe"
        };
        var outside = new ApplicationOptimizationRuleTarget
        {
            TargetType = ApplicationOptimizationTargetType.Executable,
            Path = "C:\\Demo\\outside.exe"
        };

        Assert.True(ApplicationOptimizationRulePolicy.TargetsOverlap(first, member));
        Assert.True(ApplicationOptimizationRulePolicy.TargetsOverlap(member, first));
        Assert.False(ApplicationOptimizationRulePolicy.TargetsOverlap(first, outside));
    }

    [Fact]
    public void FollowAutomaticFixedExecutableGroupCreatesOneCombinedOverride()
    {
        var first = Process("first.exe", 10, 60L * 1024 * 1024, executablePath: "C:\\Demo\\first.exe");
        var second = Process("second.exe", 11, 60L * 1024 * 1024, executablePath: "C:\\Demo\\second.exe");
        var other = Process("other.exe", 12, 500L * 1024 * 1024, executablePath: "C:\\Demo\\other.exe");
        var target = new ApplicationOptimizationRuleTarget
        {
            TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
            Path = first.ExecutablePath!,
            ExecutablePaths = new() { first.ExecutablePath!, second.ExecutablePath! },
            BypassProtectionConfirmed = true
        };
        var rule = new ApplicationOptimizationRule
        {
            Id = "fixed-group-auto",
            TriggerMode = ApplicationOptimizationRuleTriggerMode.FollowAutomatic,
            WorkingSetThresholdBytes = 100L * 1024 * 1024,
            Targets = new() { target }
        };

        var item = Assert.Single(ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
            new[] { rule }, new[] { Family("demo", first, second, other) }, automaticOptimizationEnabled: true));

        Assert.True(item.ProcessIds.SetEquals(new[] { 10, 11 }));
        Assert.Equal(1, item.ThresholdBytes);
        Assert.True(item.BypassProtection);
    }

    [Fact]
    public void FollowAutomaticFixedGroupCombinesAcrossFamiliesAndBypassesOnlyItsSnapshot()
    {
        var first = Process("first.exe", 10, 60L * 1024 * 1024, executablePath: "C:\\First\\first.exe");
        var second = Process("second.exe", 11, 60L * 1024 * 1024, executablePath: "C:\\Second\\second.exe");
        var other = Process("other.exe", 12, 1L * 1024 * 1024, executablePath: "C:\\Other\\other.exe");
        var families = new[] { Family("first", first), Family("second", second), Family("other", other) };
        var rule = new ApplicationOptimizationRule
        {
            Id = "fixed-group-cross-family",
            TriggerMode = ApplicationOptimizationRuleTriggerMode.FollowAutomatic,
            WorkingSetThresholdBytes = 100L * 1024 * 1024,
            Targets = new()
            {
                new()
                {
                    TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
                    Path = first.ExecutablePath!,
                    ExecutablePaths = new() { first.ExecutablePath!, second.ExecutablePath! },
                    BypassProtectionConfirmed = true
                }
            }
        };
        var overrides = ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
            new[] { rule }, families, automaticOptimizationEnabled: true);
        var protection = new ProtectionRules(new[]
        {
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = first.ExecutablePath!,
                ProtectedExecutablePaths = new() { first.ExecutablePath! }
            },
            new ApplicationProtectionRule
            {
                ApplicationExecutablePath = second.ExecutablePath!,
                ProtectedExecutablePaths = new() { second.ExecutablePath! }
            }
        });
        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            families,
            OptimizationSettings.For(OptimizationProfile.Turbo),
            protection,
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: true,
            automaticThresholdOverrides: overrides);

        Assert.Equal(2, overrides.Count);
        Assert.Equal(new[] { 10, 11 }, plan.Candidates.SelectMany(candidate => candidate.TargetProcesses)
            .Select(process => process.ProcessId).OrderBy(id => id));
    }

    [Fact]
    public void RepeatingDelayRuleIgnoresFiniteExecutionCountAndUsesInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Process(
            "loop.exe",
            10,
            200L * 1024 * 1024,
            startTime: now.AddHours(-1).ToFileTime());
        var family = Family(process);
        var rule = Rule(ApplicationOptimizationTargetType.Executable, process.ExecutablePath!);
        rule.TriggerMode = ApplicationOptimizationRuleTriggerMode.Delayed;
        rule.DelayTriggerEnabled = true;
        rule.DelayMinutes = 1;
        rule.ExecutionCount = 1;
        rule.ExecutionIntervalMinutes = 5;
        rule.RepeatIndefinitely = true;
        var runtime = new ApplicationOptimizationRuleRuntime();
        var first = runtime.GetTargetDecisions(rule, new[] { family }, now.AddHours(-1), now);
        runtime.RecordSuccessfulExecution(rule, first, new[] { process }, now, now);

        Assert.False(Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now.AddMinutes(4))).IsDue);
        Assert.True(Assert.Single(runtime.GetTargetDecisions(
            rule, new[] { family }, now.AddHours(-1), now.AddMinutes(5))).IsDue);
    }

    private static ApplicationOptimizationRule Rule(
        ApplicationOptimizationTargetType targetType,
        string path) => new()
        {
            Id = "rule-1",
            Targets = new() { new() { TargetType = targetType, Path = path } }
        };

    private static ProcessFamilySnapshot Family(ProcessSnapshot process) =>
        new("directory:c:\\demo", "Demo", "C:\\Demo", new[] { process });

    private static ProcessFamilySnapshot Family(string key, params ProcessSnapshot[] processes) =>
        new(key, key, "C:\\Demo", processes);

    private static ProcessSnapshot Process(
        string name,
        int processId,
        long workingSetBytes,
        bool reliable = true,
        long startTime = 1,
        string executablePath = "C:\\Demo\\demo.exe") => new(
            processId,
            name,
            executablePath,
            null,
        workingSetBytes,
        0,
        0,
        false,
        false,
        reliable,
        90,
        startTime);
}
