using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace MuseRAM.App;

public sealed class MotionPolicy : INotifyPropertyChanged
{
    public static MotionPolicy Current { get; } = new();

    public bool IsEnabled => SystemParameters.ClientAreaAnimation;
    public double ReviewSpinnerAngle => IsEnabled
        ? (_reviewClock.Elapsed.TotalMilliseconds / 900d * 360d) % 360d
        : 0d;

    private readonly Stopwatch _reviewClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _reviewTimer;

    private MotionPolicy()
    {
        _reviewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _reviewTimer.Tick += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewSpinnerAngle)));
        if (IsEnabled) _reviewTimer.Start();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            if (IsEnabled) _reviewTimer.Start();
            else _reviewTimer.Stop();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewSpinnerAngle)));
        }
    }
}
