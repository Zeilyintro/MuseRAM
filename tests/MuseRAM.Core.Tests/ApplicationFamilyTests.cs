namespace MuseRAM.Core.Tests;

public sealed class ApplicationFamilyTests
{
    [Fact]
    public void GroupCombinesDifferentExecutablesFromSameApplicationDirectory()
    {
        var processes = new[]
        {
            Process(1, "editor", @"F:\Apps\Editor\editor.exe"),
            Process(2, "editor-agent", @"F:\Apps\Editor\agent.exe"),
            Process(3, "editor-host", @"F:\Apps\Editor\host.exe")
        };

        var family = Assert.Single(ApplicationFamilyGrouper.Group(processes));

        Assert.Equal(3, family.Processes.Count);
    }

    [Fact]
    public void GroupKeepsSameProcessNameSeparateAcrossDirectories()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(10, "helper", @"F:\Apps\One\helper.exe"),
            Process(11, "helper", @"F:\Apps\Two\helper.exe")
        });

        Assert.Equal(2, families.Count);
    }

    [Fact]
    public void GroupKeepsWindowsPackageIdentityStableAcrossVersionDirectories()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(100, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.40816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
            Process(101, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.727.4816.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe")
        });

        var family = Assert.Single(families);
        Assert.Equal("package:openai.codex_2p2nqsd0c76g0", family.Key);
        Assert.Equal(2, family.Processes.Count);
    }

    [Fact]
    public void GroupKeepsSameWindowsPackageNameSeparateAcrossPublishers()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(102, "Editor", @"C:\Program Files\WindowsApps\Contoso.Editor_1.0.0.0_x64__publisherone\app\Editor.exe"),
            Process(103, "Editor", @"C:\Program Files\WindowsApps\Contoso.Editor_1.0.0.0_x64__publishertwo\app\Editor.exe")
        });

        Assert.Equal(2, families.Count);
    }

    [Fact]
    public void GroupKeepsVersionedAppDirectoryIdentityStableAcrossUpgrades()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(104, "KOOK", @"C:\Users\User\AppData\Local\KOOK\app-0.109.0\KOOK.exe"),
            Process(105, "KOOK", @"C:\Users\User\AppData\Local\KOOK\app-0.109.1\KOOK.exe")
        });

        var family = Assert.Single(families);
        Assert.Equal(@"directory:c:\users\user\appdata\local\kook", family.Key);
        Assert.Equal(2, family.Processes.Count);
        Assert.Single(ApplicationComponentIdentity.GroupProcesses(family));
    }

    [Fact]
    public void GroupDoesNotNormalizeDirectoriesThatOnlyStartWithApp()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(106, "Editor", @"F:\Apps\Editor\app-stable\Editor.exe"),
            Process(107, "Editor", @"F:\Apps\Editor\app-preview\Editor.exe")
        });

        Assert.Equal(2, families.Count);
    }

    [Fact]
    public void GroupMergesParentWithChildFromNestedApplicationDirectory()
    {
        var processes = new[]
        {
            Process(12, "suite", @"F:\Apps\Suite\suite.exe", workingSetBytes: 63L * 1024 * 1024),
            Process(13, "engine", @"F:\Apps\Suite\bin\engine.exe", parentProcessId: 12, workingSetBytes: 65L * 1024 * 1024)
        };

        var family = Assert.Single(ApplicationFamilyGrouper.Group(processes));

        Assert.Equal(128L * 1024 * 1024, family.WorkingSetBytes);
        Assert.Equal(new[] { 12, 13 }, family.Processes.Select(process => process.ProcessId).OrderBy(id => id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupKeepsApplicationRootNameWhenHiddenOrMinimized(bool hasMinimizedWindow)
    {
        var family = Assert.Single(ApplicationFamilyGrouper.Group(new[]
        {
            Process(
                60,
                "ChatGPT",
                @"F:\Apps\ChatGPT\app\ChatGPT.exe",
                workingSetBytes: 128L * 1024 * 1024,
                hasMinimizedWindow: hasMinimizedWindow),
            Process(61, "codex", @"F:\Apps\ChatGPT\app\resources\codex.exe", parentProcessId: 60, workingSetBytes: 1024L * 1024 * 1024)
        }));

        Assert.Equal("ChatGPT", family.DisplayName);
        Assert.Equal(2, family.Processes.Count);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void GroupUsesUserFacingGameProcessInsteadOfWindowlessBootstrapper(
        bool isForeground,
        bool hasVisibleWindow,
        bool hasMinimizedWindow)
    {
        var family = Assert.Single(ApplicationFamilyGrouper.Group(new[]
        {
            Process(70, "bootstrapper", @"F:\Games\Call of Duty HQ\bootstrapper.exe", workingSetBytes: 64L * 1024 * 1024),
            Process(
                71,
                "cod",
                @"F:\Games\Call of Duty HQ\cod.exe",
                parentProcessId: 70,
                workingSetBytes: 5L * 1024 * 1024 * 1024,
                isForeground: isForeground,
                hasVisibleWindow: hasVisibleWindow,
                hasMinimizedWindow: hasMinimizedWindow)
        }));

        Assert.Equal("cod", family.DisplayName);
    }

    [Fact]
    public void NestedParentChildProcessesMeetTurboFamilyMinimumTogether()
    {
        var family = Assert.Single(ApplicationFamilyGrouper.Group(new[]
        {
            Process(16, "suite", @"F:\Apps\Suite\suite.exe", workingSetBytes: 63L * 1024 * 1024),
            Process(17, "engine", @"F:\Apps\Suite\bin\engine.exe", parentProcessId: 16, workingSetBytes: 65L * 1024 * 1024)
        }));

        var plan = new OptimizationPlanner().CreatePlan(
            new MemorySnapshot(16UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024, 94),
            new[] { family },
            OptimizationSettings.For(OptimizationProfile.Turbo),
            new ProtectionRules(),
            new Dictionary<int, DateTimeOffset>(),
            DateTimeOffset.UtcNow,
            manual: false);

        Assert.Equal(new[] { 16, 17 }, Assert.Single(plan.Candidates).TargetProcesses
            .Select(process => process.ProcessId)
            .OrderBy(id => id));
    }

    [Fact]
    public void GroupDoesNotMergeParentWithChildFromUnrelatedDirectory()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(14, "launcher", @"F:\Apps\Launcher\launcher.exe"),
            Process(15, "editor", @"F:\Apps\Editor\editor.exe", parentProcessId: 14)
        });

        Assert.Equal(2, families.Count);
    }

    [Fact]
    public void GroupUsesParentIdentityForGenericChildWithoutReadablePath()
    {
        var processes = new[]
        {
            Process(20, "editor", @"F:\Apps\Editor\editor.exe"),
            Process(21, "worker", null, parentProcessId: 20)
        };

        var family = Assert.Single(ApplicationFamilyGrouper.Group(processes));

        Assert.Equal(2, family.Processes.Count);
    }

    [Fact]
    public void GroupDoesNotMergeUnrelatedChildrenOfSameLauncher()
    {
        var processes = new[]
        {
            Process(30, "launcher", null),
            Process(31, "notes", null, parentProcessId: 30),
            Process(32, "chat", null, parentProcessId: 30)
        };

        var families = ApplicationFamilyGrouper.Group(processes);

        Assert.Equal(3, families.Count);
    }

    [Fact]
    public void GroupKeepsUnparentedGenericProcessesSeparateWithoutPaths()
    {
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(40, "worker", null),
            Process(41, "worker", null)
        });

        Assert.Equal(2, families.Count);
    }

    [Fact]
    public void GroupDoesNotMergeDifferentExecutablesInsideWindowsDirectory()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var families = ApplicationFamilyGrouper.Group(new[]
        {
            Process(50, "first", Path.Combine(windows, "System32", "first.exe")),
            Process(51, "second", Path.Combine(windows, "System32", "second.exe"))
        });

        Assert.Equal(2, families.Count);
    }

    private static ProcessSnapshot Process(
        int id,
        string name,
        string? path,
        int? parentProcessId = null,
        long workingSetBytes = 100L * 1024 * 1024,
        bool isForeground = false,
        bool hasVisibleWindow = false,
        bool hasMinimizedWindow = false) =>
        new(
            id,
            name,
            path,
            parentProcessId,
            workingSetBytes,
            0,
            0,
            isForeground,
            hasVisibleWindow,
            true,
            90,
            HasMinimizedWindow: hasMinimizedWindow);
}
