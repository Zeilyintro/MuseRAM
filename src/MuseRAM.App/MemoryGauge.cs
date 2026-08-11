using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using MediaFlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace MuseRAM.App;

public sealed class MemoryGauge : FrameworkElement
{
    private const int SegmentCount = 28;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MemoryGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MemoryGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = RegisterBrush(nameof(AccentBrush));
    public static readonly DependencyProperty SuccessBrushProperty = RegisterBrush(nameof(SuccessBrush));
    public static readonly DependencyProperty WarningBrushProperty = RegisterBrush(nameof(WarningBrush));
    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush));
    public static readonly DependencyProperty MutedBrushProperty = RegisterBrush(nameof(MutedBrush));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush SuccessBrush { get => (Brush)GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }
    public Brush WarningBrush { get => (Brush)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 1 || ActualHeight <= 1) return;

        var value = Math.Clamp(Value, 0, 100);
        var activeSegments = (int)Math.Round(value / 100d * SegmentCount);
        var activeBrush = value >= 85 ? WarningBrush : value >= 70 ? SuccessBrush : AccentBrush;
        var activePen = CreatePen(activeBrush);
        var trackPen = CreatePen(TrackBrush);
        var centerX = ActualWidth / 2;
        var centerY = ActualHeight * 0.82;
        var radius = Math.Min(ActualWidth * 0.46, ActualHeight * 0.72);
        var inner = radius - 15;

        for (var index = 0; index < SegmentCount; index++)
        {
            var angle = Math.PI + index * Math.PI / (SegmentCount - 1);
            drawingContext.DrawLine(
                index < activeSegments ? activePen : trackPen,
                new Point(centerX + Math.Cos(angle) * inner, centerY + Math.Sin(angle) * inner),
                new Point(centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius));
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawCenteredText(drawingContext, $"{value:0}%", 30, FontWeights.SemiBold, TextBrush, centerX, centerY - radius * 0.58, pixelsPerDip);
        DrawCenteredText(drawingContext, Label, 12, FontWeights.Normal, MutedBrush, centerX, centerY - radius * 0.24, pixelsPerDip);
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(MemoryGauge),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Pen CreatePen(Brush brush) => new(brush, 8)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };

    private static void DrawCenteredText(
        DrawingContext drawingContext,
        string text,
        double fontSize,
        FontWeight fontWeight,
        Brush brush,
        double centerX,
        double top,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            MediaFlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            pixelsPerDip);
        drawingContext.DrawText(formatted, new Point(centerX - formatted.Width / 2, top));
    }
}
