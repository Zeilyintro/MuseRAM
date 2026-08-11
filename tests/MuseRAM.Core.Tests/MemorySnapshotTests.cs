using MuseRAM.Core;

namespace MuseRAM.Core.Tests;

public sealed class MemorySnapshotTests
{
    [Fact]
    public void CalculatesCommittedMemoryAndPercentageFromCommitLimit()
    {
        var snapshot = new MemorySnapshot(16, 8, 50)
        {
            CommitLimitBytes = 40,
            AvailableCommitBytes = 10
        };

        Assert.Equal(30UL, snapshot.CommittedBytes);
        Assert.Equal(75, snapshot.CommitLoadPercent);
    }

    [Fact]
    public void InvalidOrMissingCommitValuesStayAtZero()
    {
        var missing = new MemorySnapshot(16, 8, 50);
        var inconsistent = missing with
        {
            CommitLimitBytes = 10,
            AvailableCommitBytes = 12
        };

        Assert.Equal(0UL, missing.CommittedBytes);
        Assert.Equal(0, missing.CommitLoadPercent);
        Assert.Equal(0UL, inconsistent.CommittedBytes);
        Assert.Equal(0, inconsistent.CommitLoadPercent);
    }
}
