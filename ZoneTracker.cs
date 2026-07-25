using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class ZoneTracker
{
    public const int TransientHoldMilliseconds = 3_000;
    public const int ValidatedHoldMilliseconds = 30_000;
    public const int FadeMilliseconds = 300;

    private const int ValidationOneMilliseconds = 1_000;
    private const int ValidationThreeMilliseconds = 3_000;
    private const int ValidationFiveMilliseconds = 5_000;
    private const int ResizeMilliseconds = 500;
    private const int ContinuousGapMilliseconds = 700;
    private const int MaximumZoneCount = 48;

    private sealed class ZoneState
    {
        public DrawingRectangle Bounds;
        public DrawingRectangle RecentBounds;
        public bool HasRecentBounds;
        public long ContinuousStartTicks;
        public long LastActivityTicks;
        public long LastResizeTicks;
        public bool ConfirmedAtOneSecond;
        public bool ConfirmedAtThreeSeconds;
        public bool Validated;
    }

    private readonly int _localWidth;
    private readonly int _localHeight;
    private readonly List<ZoneState> _zones = new();

    public ZoneTracker(
        int localWidth,
        int localHeight)
    {
        _localWidth = localWidth;
        _localHeight = localHeight;
    }

    public void Clear()
    {
        _zones.Clear();
    }

    public bool Update(
        IReadOnlyList<DrawingRectangle> components,
        long now)
    {
        var changed = false;
        var usedZones = new HashSet<ZoneState>();

        foreach (var component in components)
        {
            var zone = FindMatchingZone(
                component,
                usedZones);

            if (zone is null)
            {
                zone = new ZoneState
                {
                    Bounds = component,
                    RecentBounds = component,
                    HasRecentBounds = true,
                    ContinuousStartTicks = now,
                    LastActivityTicks = now,
                    LastResizeTicks = now
                };

                _zones.Add(zone);
                usedZones.Add(zone);
                changed = true;

                if (_zones.Count > MaximumZoneCount)
                {
                    var oldest = _zones
                        .OrderBy(candidate => candidate.LastActivityTicks)
                        .First();

                    _zones.Remove(oldest);
                }

                continue;
            }

            usedZones.Add(zone);

            if (!zone.Validated &&
                now - zone.LastActivityTicks >
                StopwatchTicks(ContinuousGapMilliseconds))
            {
                zone.ContinuousStartTicks = now;
                zone.ConfirmedAtOneSecond = false;
                zone.ConfirmedAtThreeSeconds = false;
            }

            zone.LastActivityTicks = now;
            zone.RecentBounds = zone.HasRecentBounds
                ? DrawingRectangle.Union(
                    zone.RecentBounds,
                    component)
                : component;
            zone.HasRecentBounds = true;

            var grown = DrawingRectangle.Union(
                zone.Bounds,
                component);

            if (grown != zone.Bounds)
            {
                zone.Bounds = Clamp(grown);
                changed = true;
            }

            if (!zone.Validated)
            {
                var continuousElapsed =
                    now - zone.ContinuousStartTicks;

                if (continuousElapsed >=
                    StopwatchTicks(ValidationOneMilliseconds))
                {
                    zone.ConfirmedAtOneSecond = true;
                }

                if (continuousElapsed >=
                    StopwatchTicks(ValidationThreeMilliseconds))
                {
                    zone.ConfirmedAtThreeSeconds = true;
                }

                if (continuousElapsed >=
                        StopwatchTicks(ValidationFiveMilliseconds) &&
                    zone.ConfirmedAtOneSecond &&
                    zone.ConfirmedAtThreeSeconds)
                {
                    zone.Validated = true;
                    changed = true;
                }
            }
        }

        foreach (var zone in _zones)
        {
            if (now - zone.LastResizeTicks <
                StopwatchTicks(ResizeMilliseconds))
            {
                continue;
            }

            zone.LastResizeTicks = now;

            if (!zone.HasRecentBounds)
            {
                continue;
            }

            var cleanBounds = zone.RecentBounds;
            cleanBounds.Inflate(
                zone.Validated ? 8 : 4,
                zone.Validated ? 8 : 4);
            cleanBounds = Clamp(cleanBounds);

            if (cleanBounds.Width > 0 &&
                cleanBounds.Height > 0 &&
                cleanBounds != zone.Bounds)
            {
                zone.Bounds = cleanBounds;
                changed = true;
            }

            zone.RecentBounds = DrawingRectangle.Empty;
            zone.HasRecentBounds = false;
        }

        return changed;
    }

    public DrawingRectangle? RevealUnderCursor(
        DrawingPoint cursor,
        long now)
    {
        foreach (var zone in _zones)
        {
            if (!zone.Bounds.Contains(cursor))
            {
                continue;
            }

            zone.LastActivityTicks = now;
            return zone.Bounds;
        }

        return null;
    }

    public IReadOnlyList<RevealRegion> BuildRegions(
        long now,
        double maximumOpacity,
        out bool animationActive,
        out bool stateChanged)
    {
        var result = new List<RevealRegion>(_zones.Count);
        animationActive = false;
        stateChanged = false;

        for (var index = _zones.Count - 1;
             index >= 0;
             index--)
        {
            var zone = _zones[index];
            var hold = zone.Validated
                ? ValidatedHoldMilliseconds
                : TransientHoldMilliseconds;

            var elapsed = now - zone.LastActivityTicks;
            var expires = StopwatchTicks(hold + FadeMilliseconds);

            if (elapsed >= expires)
            {
                _zones.RemoveAt(index);
                stateChanged = true;
                continue;
            }

            if (elapsed >= StopwatchTicks(hold))
            {
                animationActive = true;
            }

            result.Add(
                new RevealRegion(
                    ToNormalized(zone.Bounds),
                    ComputeOpacity(
                        elapsed,
                        hold,
                        FadeMilliseconds,
                        maximumOpacity)));
        }

        return result;
    }

    private ZoneState? FindMatchingZone(
        DrawingRectangle component,
        ISet<ZoneState> usedZones)
    {
        ZoneState? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var zone in _zones)
        {
            if (usedZones.Contains(zone) ||
                !ConnectedAcrossFrames(
                    zone.Bounds,
                    component))
            {
                continue;
            }

            var intersection = DrawingRectangle.Intersect(
                zone.Bounds,
                component);

            var intersectionArea = Math.Max(0, intersection.Width) *
                                   Math.Max(0, intersection.Height);

            var centerDistance =
                Math.Abs(
                    zone.Bounds.Left + zone.Bounds.Width / 2.0 -
                    component.Left - component.Width / 2.0) +
                Math.Abs(
                    zone.Bounds.Top + zone.Bounds.Height / 2.0 -
                    component.Top - component.Height / 2.0);

            var score = intersectionArea * 1000.0 - centerDistance;

            if (score <= bestScore)
            {
                continue;
            }

            best = zone;
            bestScore = score;
        }

        return best;
    }

    private static bool ConnectedAcrossFrames(
        DrawingRectangle first,
        DrawingRectangle second)
    {
        if (first.IntersectsWith(second))
        {
            return true;
        }

        var expanded = first;
        expanded.Inflate(4, 4);

        if (!expanded.IntersectsWith(second))
        {
            return false;
        }

        var verticalOverlap = Math.Max(
            0,
            Math.Min(first.Bottom, second.Bottom) -
            Math.Max(first.Top, second.Top));

        var horizontalOverlap = Math.Max(
            0,
            Math.Min(first.Right, second.Right) -
            Math.Max(first.Left, second.Left));

        return verticalOverlap >=
                   Math.Min(first.Height, second.Height) * 0.5 ||
               horizontalOverlap >=
                   Math.Min(first.Width, second.Width) * 0.5;
    }

    private DrawingRectangle Clamp(
        DrawingRectangle rectangle)
    {
        return DrawingRectangle.Intersect(
            rectangle,
            new DrawingRectangle(
                0,
                0,
                _localWidth,
                _localHeight));
    }

    private System.Windows.Rect ToNormalized(
        DrawingRectangle rectangle)
    {
        return new System.Windows.Rect(
            rectangle.Left /
            (double)Math.Max(1, _localWidth),
            rectangle.Top /
            (double)Math.Max(1, _localHeight),
            rectangle.Width /
            (double)Math.Max(1, _localWidth),
            rectangle.Height /
            (double)Math.Max(1, _localHeight));
    }

    private static double ComputeOpacity(
        long elapsedTicks,
        int holdMilliseconds,
        int fadeMilliseconds,
        double maximumOpacity)
    {
        var holdTicks = StopwatchTicks(holdMilliseconds);

        if (elapsedTicks <= holdTicks)
        {
            return 0.0;
        }

        var fadeTicks = Math.Max(
            1L,
            StopwatchTicks(fadeMilliseconds));

        var progress = Math.Clamp(
            (elapsedTicks - holdTicks) /
            (double)fadeTicks,
            0.0,
            1.0);

        return maximumOpacity * progress;
    }

    private static long StopwatchTicks(
        double milliseconds)
    {
        return (long)(
            milliseconds *
            System.Diagnostics.Stopwatch.Frequency /
            1000.0);
    }
}
