using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal static class ProtectionArea
{
    private const int AutoHideActivationEdgePixels = 4;

    public static DrawingRectangle GetBounds(
        FormsScreen screen)
    {
        var screenBounds =
            screen.Bounds;
        var workingArea =
            screen.WorkingArea;

        if (workingArea.Width > 0 &&
            workingArea.Height > 0 &&
            workingArea !=
            screenBounds)
        {
            return workingArea;
        }

        var left =
            screenBounds.Left +
            AutoHideActivationEdgePixels;
        var top =
            screenBounds.Top +
            AutoHideActivationEdgePixels;
        var right =
            screenBounds.Right -
            AutoHideActivationEdgePixels;
        var bottom =
            screenBounds.Bottom -
            AutoHideActivationEdgePixels;

        if (right <= left ||
            bottom <= top)
        {
            return screenBounds;
        }

        return DrawingRectangle.FromLTRB(
            left,
            top,
            right,
            bottom);
    }
}
