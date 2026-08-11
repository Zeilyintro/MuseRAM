namespace MuseRAM.Core.Tests;

public sealed class WorkingSetTrimResultPolicyTests
{
    [Fact]
    public void OneSuccessfulRequestReportsSuccessAndKeepsPartialFailure()
    {
        var result = WorkingSetTrimResultPolicy.Create(
            101,
            workingSetBefore: 500,
            setProcessWorkingSetSucceeded: true,
            setProcessWorkingSetErrorCode: null,
            emptyWorkingSetSucceeded: false,
            emptyWorkingSetErrorCode: 5,
            workingSetAfterSamples: new long?[] { 100, 180, 220 });

        Assert.True(result.Success);
        Assert.True(result.SetProcessWorkingSetSucceeded);
        Assert.False(result.EmptyWorkingSetSucceeded);
        Assert.Equal(5, result.EmptyWorkingSetErrorCode);
        Assert.Null(result.Error);
        Assert.Contains("partially succeeded", result.Warning);
        Assert.True(result.HasReliableWorkingSetMeasurement);
        Assert.Equal(220, result.WorkingSetAfterBytes);
        Assert.Equal(280, result.WorkingSetReductionBytes);
    }

    [Fact]
    public void EmptyWorkingSetSuccessIsEnoughForARequestSuccess()
    {
        var result = WorkingSetTrimResultPolicy.Create(
            101,
            workingSetBefore: 500,
            setProcessWorkingSetSucceeded: false,
            setProcessWorkingSetErrorCode: 5,
            emptyWorkingSetSucceeded: true,
            emptyWorkingSetErrorCode: null,
            workingSetAfterSamples: new long?[] { 250 });

        Assert.True(result.Success);
        Assert.False(result.SetProcessWorkingSetSucceeded);
        Assert.True(result.EmptyWorkingSetSucceeded);
        Assert.Equal(250, result.WorkingSetReductionBytes);
    }

    [Fact]
    public void BothFailedRequestsReportFailure()
    {
        var result = WorkingSetTrimResultPolicy.Create(
            101,
            workingSetBefore: 500,
            setProcessWorkingSetSucceeded: false,
            setProcessWorkingSetErrorCode: 5,
            emptyWorkingSetSucceeded: false,
            emptyWorkingSetErrorCode: 87,
            workingSetAfterSamples: Array.Empty<long?>());

        Assert.False(result.Success);
        Assert.False(result.HasReliableWorkingSetMeasurement);
        Assert.Equal(0, result.WorkingSetReductionBytes);
        Assert.Contains("requests failed", result.Error);
    }

    [Fact]
    public void InvalidReadsAreIgnoredAndLastValidSampleWins()
    {
        var result = WorkingSetTrimResultPolicy.Create(
            101,
            workingSetBefore: 500,
            setProcessWorkingSetSucceeded: true,
            setProcessWorkingSetErrorCode: null,
            emptyWorkingSetSucceeded: true,
            emptyWorkingSetErrorCode: null,
            workingSetAfterSamples: new long?[] { 80, null, 190, null });

        Assert.True(result.HasReliableWorkingSetMeasurement);
        Assert.Equal(190, result.WorkingSetAfterBytes);
        Assert.Equal(310, result.WorkingSetReductionBytes);
    }

    [Fact]
    public void MissingBeforeOrAfterMeasurementDoesNotInventARelease()
    {
        var missingBefore = WorkingSetTrimResultPolicy.Create(
            101, null, true, null, true, null, new long?[] { 100 });
        var missingAfter = WorkingSetTrimResultPolicy.Create(
            102, 500, true, null, true, null, new long?[] { null, null });

        Assert.False(missingBefore.HasReliableWorkingSetMeasurement);
        Assert.Equal(0, missingBefore.WorkingSetReductionBytes);
        Assert.False(missingAfter.HasReliableWorkingSetMeasurement);
        Assert.Equal(0, missingAfter.WorkingSetReductionBytes);
    }

    [Fact]
    public void PageFaultDeltaRequiresMonotonicCounters()
    {
        var increased = new TrimResult(101, true, 500, 200, null)
        {
            PageFaultCountBefore = 100,
            PageFaultCountAfter = 112
        };
        var reset = increased with { PageFaultCountAfter = 90 };

        Assert.Equal((uint)12, increased.PageFaultCountDelta);
        Assert.Null(reset.PageFaultCountDelta);
    }
}
