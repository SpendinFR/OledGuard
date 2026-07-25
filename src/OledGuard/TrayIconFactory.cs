using System.Drawing;
using System.Drawing.Drawing2D;

namespace OledGuard;

internal static class TrayIconFactory
{
    public static Icon Create(bool enabled)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var background = new SolidBrush(Color.FromArgb(235, 12, 15, 20));
        graphics.FillEllipse(background, 2, 2, 28, 28);

        var ringColor = enabled
            ? Color.FromArgb(255, 55, 220, 245)
            : Color.FromArgb(255, 145, 150, 158);
        using var ring = new Pen(ringColor, 4.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawEllipse(ring, 7, 7, 18, 18);

        using var center = new SolidBrush(enabled
            ? Color.FromArgb(255, 235, 255, 255)
            : Color.FromArgb(255, 90, 95, 103));
        graphics.FillEllipse(center, 13, 13, 6, 6);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
