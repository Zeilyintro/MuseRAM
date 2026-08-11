using MuseRAM.App;

namespace MuseRAM.App.Tests;

public sealed class DeepReleaseDialogFlowTests
{
    [Fact]
    public void CancelingApplicationSelectionStopsBeforeServiceSelection()
    {
        Assert.False(DeepReleaseDialogFlow.ShouldContinueToServices(
            applicationDialogWasShown: true,
            applicationDialogConfirmed: false));
    }

    [Fact]
    public void ConfirmingApplicationSelectionContinuesToServiceSelection()
    {
        Assert.True(DeepReleaseDialogFlow.ShouldContinueToServices(
            applicationDialogWasShown: true,
            applicationDialogConfirmed: true));
    }

    [Fact]
    public void ServiceOnlyCandidatesDoNotRequireApplicationConfirmation()
    {
        Assert.True(DeepReleaseDialogFlow.ShouldContinueToServices(
            applicationDialogWasShown: false,
            applicationDialogConfirmed: false));
    }
}
