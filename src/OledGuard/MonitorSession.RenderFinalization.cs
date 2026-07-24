using System;
using System.Collections.Generic;
using System.Windows;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    private void PolishRenderMaskRegions(List<MaskRegion> regions)
    {
        if (regions.Count < 2)
        {
            return;
        }

        var xTolerance = 2.0 / Math.Max(1, _columns);
        var yTolerance = 2.0 / Math.Max(1, _rows);
        var index = 0;
        while (index < regions.Count)
        {
            var mergedAny = false;
            var current = regions[index];
            for (var candidateIndex = index + 1;
                 candidateIndex < regions.Count;
                 candidateIndex++)
            {
                var candidate = regions[candidateIndex];
                if (!CanPolishMergeMaskRegions(
                        current,
                        candidate,
                        xTolerance,
                        yTolerance))
                {
                    continue;
                }

                current = MergeMaskRegions(
                    current,
                    candidate);
                regions[index] = current;
                regions.RemoveAt(candidateIndex);
                candidateIndex--;
                mergedAny = true;
            }

            if (!mergedAny)
            {
                index++;
            }
        }
    }

    private static bool CanPolishMergeMaskRegions(
        MaskRegion first,
        MaskRegion second,
        double xTolerance,
        double yTolerance)
    {
        if (Math.Abs(first.Opacity - second.Opacity) > 0.002)
        {
            return false;
        }

        var firstBounds = first.NormalizedBounds;
        var secondBounds = second.NormalizedBounds;
        if (firstBounds.Width <= 0.0 ||
            firstBounds.Height <= 0.0 ||
            secondBounds.Width <= 0.0 ||
            secondBounds.Height <= 0.0)
        {
            return false;
        }

        var overlapWidth = Math.Max(
            0.0,
            Math.Min(firstBounds.Right, secondBounds.Right) -
            Math.Max(firstBounds.Left, secondBounds.Left));
        var overlapHeight = Math.Max(
            0.0,
            Math.Min(firstBounds.Bottom, secondBounds.Bottom) -
            Math.Max(firstBounds.Top, secondBounds.Top));
        var gapX = Math.Max(
            0.0,
            Math.Max(firstBounds.Left, secondBounds.Left) -
            Math.Min(firstBounds.Right, secondBounds.Right));
        var gapY = Math.Max(
            0.0,
            Math.Max(firstBounds.Top, secondBounds.Top) -
            Math.Min(firstBounds.Bottom, secondBounds.Bottom));

        var overlapping = gapX <= 0.0 && gapY <= 0.0;
        var sameHorizontalBand =
            gapX <= xTolerance &&
            overlapHeight >= Math.Min(firstBounds.Height, secondBounds.Height) * 0.55;
        var sameVerticalBand =
            gapY <= yTolerance &&
            overlapWidth >= Math.Min(firstBounds.Width, secondBounds.Width) * 0.55;

        return overlapping || sameHorizontalBand || sameVerticalBand;
    }

    private static MaskRegion MergeMaskRegions(
        MaskRegion first,
        MaskRegion second)
    {
        var firstBounds = first.NormalizedBounds;
        var secondBounds = second.NormalizedBounds;
        var merged = new Rect(
            Math.Min(firstBounds.Left, secondBounds.Left),
            Math.Min(firstBounds.Top, secondBounds.Top),
            Math.Max(firstBounds.Right, secondBounds.Right) -
            Math.Min(firstBounds.Left, secondBounds.Left),
            Math.Max(firstBounds.Bottom, secondBounds.Bottom) -
            Math.Min(firstBounds.Top, secondBounds.Top));

        return new MaskRegion(
            merged,
            Math.Min(first.Opacity, second.Opacity));
    }
}
