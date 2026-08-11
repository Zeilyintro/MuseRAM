using MuseRAM.App;
using MuseRAM.Core;

namespace MuseRAM.App.Tests;

public sealed class ProtectedApplicationGroupTests
{
    [Fact]
    public void PartialProtectionShowsTotalAndProtectedWorkingSet()
    {
        var group = CreateGroup(
            ApplicationProtectionState.Partial,
            427L * 1024 * 1024,
            9L * 1024 * 1024);

        Assert.Equal("427 MB / 9 MB", group.Memory);
        Assert.Equal(9L * 1024 * 1024, group.ProtectedWorkingSetBytes);
    }

    [Fact]
    public void EntireFamilyProtectionShowsOnlyTotalWorkingSet()
    {
        var group = CreateGroup(
            ApplicationProtectionState.EntireFamily,
            427L * 1024 * 1024,
            427L * 1024 * 1024);

        Assert.Equal("427 MB", group.Memory);
    }

    private static ProtectedApplicationGroup CreateGroup(
        ApplicationProtectionState protectionState,
        long totalWorkingSetBytes,
        long protectedWorkingSetBytes)
    {
        var executables = new[]
        {
            new ProtectedExecutableEntry(
                "family",
                "protected",
                @"C:\App\protected.exe",
                1,
                protectedWorkingSetBytes,
                Array.Empty<ProtectedProcessEntry>(),
                false)
        };
        return new ProtectedApplicationGroup(
            "key",
            "family",
            "App",
            @"C:\App\app.exe",
            protectionState,
            new[] { @"C:\App\app.exe" },
            executables,
            6,
            totalWorkingSetBytes,
            false,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }
}
