using System.Reflection;
using MuseRAM.App;

namespace MuseRAM.App.Tests;

public class MotionPolicyTests
{
    [Fact]
    public void SpinnerTimerStopsWhenHiddenAndResumesAccordingToSystemMotionSetting()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var policy = MotionPolicy.Current;
            try
            {
                var timer = typeof(MotionPolicy).GetField("_reviewTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(policy)!;
                bool IsRunning() => (bool)timer.GetType().GetProperty("IsEnabled")!.GetValue(timer)!;
                Assert.False(IsRunning());
                policy.SetWindowVisible(true);
                Assert.Equal(policy.IsEnabled, IsRunning());
                policy.SetWindowVisible(false);
                Assert.False(IsRunning());
                policy.SetWindowVisible(true);
                Assert.Equal(policy.IsEnabled, IsRunning());
                policy.SetWindowVisible(false);
                Assert.False(IsRunning());
            }
            catch (Exception exception) { failure = exception; }
            finally { policy.SetWindowVisible(false); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
