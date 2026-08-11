using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace MuseRAM.App;

public sealed class MemoryHistorySeries
{
    private readonly double[] _values;
    private int _next;

    public MemoryHistorySeries(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new double[capacity];
    }

    public int Capacity => _values.Length;
    public int Count { get; private set; }

    public void Add(double value)
    {
        _values[_next] = Math.Clamp(value, 0, 100);
        _next = (_next + 1) % Capacity;
        if (Count < Capacity) Count++;
    }

    public double GetChronologicalValue(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        var start = Count == Capacity ? _next : 0;
        return _values[(start + index) % Capacity];
    }
}

public sealed class MemoryHistoryChart : FrameworkElement
{
    private const int SampleCapacity = 20;
    private static readonly DashStyle GridDashStyle = CreateGridDashStyle();
    private readonly MemoryHistorySeries _samples = new(SampleCapacity);

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(MemoryHistoryChart),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveBarBrushProperty = DependencyProperty.Register(
        nameof(ActiveBarBrush), typeof(Brush), typeof(MemoryHistoryChart),
        new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(MemoryHistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush ActiveBarBrush
    {
        get => (Brush)GetValue(ActiveBarBrushProperty);
        set => SetValue(ActiveBarBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public void AddSample(double loadPercent)
    {
        _samples.Add(loadPercent);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 1 || ActualHeight <= 1) return;

        var gridPen = new Pen(GridBrush, 1) { DashStyle = GridDashStyle };
        for (var index = 1; index < 4; index++)
        {
            var y = Math.Round(ActualHeight * index / 4) + 0.5;
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        const double gap = 5;
        var barWidth = Math.Max(3, (ActualWidth - gap * (SampleCapacity - 1)) / SampleCapacity);
        var totalWidth = barWidth * SampleCapacity + gap * (SampleCapacity - 1);
        var startX = Math.Max(0, ActualWidth - totalWidth);

        for (var slot = 0; slot < SampleCapacity; slot++)
        {
            var x = startX + slot * (barWidth + gap);
            drawingContext.PushOpacity(0.55);
            drawingContext.DrawRoundedRectangle(BarBrush, null, new Rect(x, 0, barWidth, ActualHeight), 3, 3);
            drawingContext.Pop();
        }

        var firstSlot = SampleCapacity - _samples.Count;
        for (var index = 0; index < _samples.Count; index++)
        {
            var value = _samples.GetChronologicalValue(index);
            var height = Math.Max(4, ActualHeight * value / 100d);
            var x = startX + (firstSlot + index) * (barWidth + gap);
            var opacity = index == _samples.Count - 1 ? 1 : 0.48;
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawRoundedRectangle(
                ActiveBarBrush,
                null,
                new Rect(x, ActualHeight - height, barWidth, height),
                3,
                3);
            drawingContext.Pop();
        }
    }

    private static DashStyle CreateGridDashStyle()
    {
        var style = new DashStyle(new DoubleCollection { 3, 5 }, 0);
        style.Freeze();
        return style;
    }
}
