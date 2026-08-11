using MuseRAM.App;

namespace MuseRAM.App.Tests;

public sealed class MemoryHistorySeriesTests
{
    [Fact]
    public void KeepsOnlyNewestSamplesInChronologicalOrder()
    {
        var series = new MemoryHistorySeries(3);

        series.Add(10);
        series.Add(20);
        series.Add(30);
        series.Add(40);

        Assert.Equal(3, series.Count);
        Assert.Equal(20, series.GetChronologicalValue(0));
        Assert.Equal(30, series.GetChronologicalValue(1));
        Assert.Equal(40, series.GetChronologicalValue(2));
    }

    [Fact]
    public void ClampsSamplesToPercentRange()
    {
        var series = new MemoryHistorySeries(2);

        series.Add(-5);
        series.Add(120);

        Assert.Equal(0, series.GetChronologicalValue(0));
        Assert.Equal(100, series.GetChronologicalValue(1));
    }
}
