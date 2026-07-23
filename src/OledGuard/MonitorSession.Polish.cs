using System.Runtime.InteropServices;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class RenderRegionState
    {
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;
        public int DimStep;
        public bool IsForegroundIntroduction;

        public int Width => MaximumColumn - MinimumColumn + 1;
        public int Height => MaximumRow - MinimumRow + 1;
        public int Area => Math.Max(0, Width) * Math.Max(0, Height);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeWindowRect rectangle);

    private bool IsMeaningfulOutputRegion(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        var bounds = _screen.Bounds;
        var widthPixels =
            (maximumColumn - minimumColumn + 1) *
            bounds.Width /
            (double)Math.Max(1, _columns);
        var heightPixels =
            (maximumRow - minimumRow + 1) *
            bounds.Height /
            (double)Math.Max(1, _rows);
        var areaPixels = widthPixels * heightPixels;

        if (areaPixels <
            _settings.MotionZoneMinimumOutputAreaPixels)
        {
            return false;
        }

        return widthPixels >=
                   _settings.MotionZoneMinimumOutputDimensionPixels ||
               heightPixels >=
                   _settings.MotionZoneMinimumOutputDimensionPixels;
    }

    private void AddForegroundIntroduction(
        IntPtr foregroundWindow,
        long now)
    {
        if (foregroundWindow == IntPtr.Zero ||
            !GetWindowRect(foregroundWindow, out var nativeRectangle))
        {
            return;
        }

        var screenBounds = _screen.Bounds;
        var windowBounds =
            System.Drawing.Rectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);
        var visibleBounds =
            System.Drawing.Rectangle.Intersect(
                screenBounds,
                windowBounds);

        if (visibleBounds.Width < 40 ||
            visibleBounds.Height < 30)
        {
            return;
        }

        var minimumColumn =
            Math.Clamp(
                (int)Math.Floor(
                    (visibleBounds.Left - screenBounds.Left) *
                    _columns /
                    (double)Math.Max(1, screenBounds.Width)),
                0,
                _columns - 1);
        var maximumColumn =
            Math.Clamp(
                (int)Math.Ceiling(
                    (visibleBounds.Right - screenBounds.Left) *
                    _columns /
                    (double)Math.Max(1, screenBounds.Width)) - 1,
                minimumColumn,
                _columns - 1);
        var minimumRow =
            Math.Clamp(
                (int)Math.Floor(
                    (visibleBounds.Top - screenBounds.Top) *
                    _rows /
                    (double)Math.Max(1, screenBounds.Height)),
                0,
                _rows - 1);
        var maximumRow =
            Math.Clamp(
                (int)Math.Ceiling(
                    (visibleBounds.Bottom - screenBounds.Top) *
                    _rows /
                    (double)Math.Max(1, screenBounds.Height)) - 1,
                minimumRow,
                _rows - 1);

        _trackedRegions.Add(
            new TrackedRegion
            {
                MinimumRow = minimumRow,
                MaximumRow = maximumRow,
                MinimumColumn = minimumColumn,
                MaximumColumn = maximumColumn,
                CreatedTicks = now,
                WindowStartTicks = now,
                LastMotionTicks = now,
                LastHitCaptureTicks = long.MinValue,
                MotionHits = 1,
                Recurring = false,
                DimStep = 0,
                IsForegroundIntroduction = true
            });
    }

    private List<RenderRegionState> BuildMergedRenderRegions()
    {
        var regions =
            new List<RenderRegionState>(_trackedRegions.Count);

        foreach (var tracked in _trackedRegions)
        {
            if (!tracked.IsForegroundIntroduction &&
                !IsMeaningfulOutputRegion(
                    tracked.MinimumRow,
                    tracked.MaximumRow,
                    tracked.MinimumColumn,
                    tracked.MaximumColumn))
            {
                continue;
            }

            regions.Add(
                new RenderRegionState
                {
                    MinimumRow = tracked.MinimumRow,
                    MaximumRow = tracked.MaximumRow,
                    MinimumColumn = tracked.MinimumColumn,
                    MaximumColumn = tracked.MaximumColumn,
                    DimStep = tracked.DimStep,
                    IsForegroundIntroduction =
                        tracked.IsForegroundIntroduction
                });
        }

        if (regions.Count < 2)
        {
            return regions;
        }

        var bounds = _screen.Bounds;
        var maximumColumnGap =
            Math.Max(
                0,
                (int)Math.Ceiling(
                    _settings.MotionZoneRenderJoinGapPixels *
                    _columns /
                    (double)Math.Max(1, bounds.Width)));
        var maximumRowGap =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    7.0 *
                    _rows /
                    Math.Max(1, bounds.Height)));

        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex < regions.Count;
                 firstIndex++)
            {
                for (var secondIndex = firstIndex + 1;
                     secondIndex < regions.Count;
                     secondIndex++)
                {
                    var first = regions[firstIndex];
                    var second = regions[secondIndex];

                    if (!ShouldJoinRenderRegions(
                            first,
                            second,
                            maximumColumnGap,
                            maximumRowGap))
                    {
                        continue;
                    }

                    regions[firstIndex] =
                        MergeRenderRegions(first, second);
                    regions.RemoveAt(secondIndex);
                    merged = true;
                    break;
                }

                if (merged)
                {
                    break;
                }
            }
        }

        return regions;
    }

    private static bool ShouldJoinRenderRegions(
        RenderRegionState first,
        RenderRegionState second,
        int maximumColumnGap,
        int maximumRowGap)
    {
        var dimStepDifference =
            Math.Abs(
                first.DimStep -
                second.DimStep);

        if (dimStepDifference > 1 ||
            first.IsForegroundIntroduction ||
            second.IsForegroundIntroduction)
        {
            return false;
        }

        var rowOverlap =
            OverlapLength(
                first.MinimumRow,
                first.MaximumRow,
                second.MinimumRow,
                second.MaximumRow);
        var columnOverlap =
            OverlapLength(
                first.MinimumColumn,
                first.MaximumColumn,
                second.MinimumColumn,
                second.MaximumColumn);
        var rowGap =
            AxisGap(
                first.MinimumRow,
                first.MaximumRow,
                second.MinimumRow,
                second.MaximumRow);
        var columnGap =
            AxisGap(
                first.MinimumColumn,
                first.MaximumColumn,
                second.MinimumColumn,
                second.MaximumColumn);

        if (dimStepDifference > 0 &&
            ((rowOverlap > 0 &&
              columnGap > 1) ||
             (columnOverlap > 0 &&
              rowGap > 1)))
        {
            return false;
        }

        var horizontalAlignment =
            rowOverlap /
            (double)Math.Max(
                1,
                Math.Min(first.Height, second.Height));
        var verticalAlignment =
            columnOverlap /
            (double)Math.Max(
                1,
                Math.Min(first.Width, second.Width));

        var horizontallyRelated =
            horizontalAlignment >= 0.55 &&
            columnGap <= maximumColumnGap;
        var verticallyRelated =
            verticalAlignment >= 0.72 &&
            rowGap <= maximumRowGap;

        if (!horizontallyRelated &&
            !verticallyRelated)
        {
            return false;
        }

        var minimumRow =
            Math.Min(first.MinimumRow, second.MinimumRow);
        var maximumRow =
            Math.Max(first.MaximumRow, second.MaximumRow);
        var minimumColumn =
            Math.Min(first.MinimumColumn, second.MinimumColumn);
        var maximumColumn =
            Math.Max(first.MaximumColumn, second.MaximumColumn);
        var unionArea =
            RectangleArea(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn);
        var occupiedArea =
            first.Area +
            second.Area -
            IntersectionArea(
                first.MinimumRow,
                first.MaximumRow,
                first.MinimumColumn,
                first.MaximumColumn,
                second.MinimumRow,
                second.MaximumRow,
                second.MinimumColumn,
                second.MaximumColumn);

        return unionArea <=
            Math.Max(1, occupiedArea) *
            1.65;
    }

    private static RenderRegionState MergeRenderRegions(
        RenderRegionState first,
        RenderRegionState second)
    {
        return new RenderRegionState
        {
            MinimumRow =
                Math.Min(first.MinimumRow, second.MinimumRow),
            MaximumRow =
                Math.Max(first.MaximumRow, second.MaximumRow),
            MinimumColumn =
                Math.Min(first.MinimumColumn, second.MinimumColumn),
            MaximumColumn =
                Math.Max(first.MaximumColumn, second.MaximumColumn),
            DimStep =
                Math.Min(
                    first.DimStep,
                    second.DimStep),
            IsForegroundIntroduction = false
        };
    }
}
