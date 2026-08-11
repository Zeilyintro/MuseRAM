using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MuseRAM.App;

public static class TrayMemoryIconPolicy
{
    public static Color BackgroundColor => Color.FromArgb(255, 17, 17, 19);
    public static Color TrackColor => Color.FromArgb(255, 63, 63, 70);
    public static Color ProgressColor => Color.FromArgb(255, 124, 156, 235);
    public static Color NumberColor => Color.FromArgb(255, 250, 250, 250);

    public static int Normalize(int percent) => Math.Clamp(percent, 0, 100);

    public static bool ShouldRegenerate(int? displayedPercent, int nextPercent) =>
        displayedPercent != Normalize(nextPercent);

    public static float ProgressSweepAngle(int percent) => Normalize(percent) * 3.6f;
}

public sealed class TrayMemoryIconController : IDisposable
{
    private Icon? _dynamicIcon;

    public int? DisplayedPercent { get; private set; }

    public void Apply(Forms.NotifyIcon trayIcon, Icon fallbackIcon, bool enabled, int? percent, string tooltip)
    {
        trayIcon.Text = tooltip;
        if (!enabled || !percent.HasValue)
        {
            RestoreFallback(trayIcon, fallbackIcon);
            return;
        }

        var normalized = TrayMemoryIconPolicy.Normalize(percent.Value);
        if (!TrayMemoryIconPolicy.ShouldRegenerate(DisplayedPercent, normalized)) return;

        var nextIcon = CreateIcon(normalized);
        trayIcon.Icon = nextIcon;
        _dynamicIcon?.Dispose();
        _dynamicIcon = nextIcon;
        DisplayedPercent = normalized;
    }

    public void Dispose()
    {
        _dynamicIcon?.Dispose();
        _dynamicIcon = null;
        DisplayedPercent = null;
    }

    private void RestoreFallback(Forms.NotifyIcon trayIcon, Icon fallbackIcon)
    {
        trayIcon.Icon = fallbackIcon;
        Dispose();
    }

    private static Icon CreateIcon(int percent)
    {
        using var bitmap = RenderBitmap(percent);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    public static Bitmap RenderBitmap(int percent)
    {
        percent = TrayMemoryIconPolicy.Normalize(percent);
        var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var background = new SolidBrush(TrayMemoryIconPolicy.BackgroundColor);
            graphics.FillEllipse(background, new RectangleF(1, 1, 29, 29));

            var ringBounds = new RectangleF(2, 2, 27, 27);
            using var track = new Pen(TrayMemoryIconPolicy.TrackColor, 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawEllipse(track, ringBounds);

            if (percent > 0)
            {
                using var progress = new Pen(TrayMemoryIconPolicy.ProgressColor, 2.5f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                if (percent >= 100) graphics.DrawEllipse(progress, ringBounds);
                else graphics.DrawArc(progress, ringBounds, -90, TrayMemoryIconPolicy.ProgressSweepAngle(percent));
            }

            using var font = new Font("Segoe UI", percent == 100 ? 11f : 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            Forms.TextRenderer.DrawText(
                graphics,
                percent.ToString(),
                font,
                new Rectangle(0, 0, 32, 32),
                TrayMemoryIconPolicy.NumberColor,
                Forms.TextFormatFlags.NoPadding |
                Forms.TextFormatFlags.HorizontalCenter |
                Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.NoPrefix);
        }
        return bitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
