namespace OledGuard;

internal sealed partial class MonitorSession
{
    private readonly record struct RegionObservation(
        int MinimumRow,
        int MaximumRow,
        int MinimumColumn,
        int MaximumColumn,
        long Timestamp);

    private void SuppressCursorInducedMotion()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return;
        }

        var localX = cursor.X - bounds.Left;
        var localY = cursor.Y - bounds.Top;
        var radius = Math.Max(
            _settings.MotionZoneCursorSuppressionPixels,
            _settings.MouseVisualRadiusPixels + 18);

        if (!_hasCursor)
        {
            SuppressMotionAroundPoint(localX, localY, radius);
            return;
        }

        var deltaX = localX - _cursorX;
        var deltaY = localY - _cursorY;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var stepLength = Math.Max(8.0, radius * 0.55);
        var steps = Math.Clamp(
            (int)Math.Ceiling(distance / stepLength),
            1,
            16);

        for (var step = 0; step <= steps; step++)
        {
            var ratio = step / (double)steps;
            SuppressMotionAroundPoint(
                _cursorX + deltaX * ratio,
                _cursorY + deltaY * ratio,
                radius);
        }
    }

    private void SuppressMotionAroundPoint(
        double localX,
        double localY,
        double radiusPixels)
    {
        var bounds = _screen.Bounds;
        var cellWidth = bounds.Width / (double)Math.Max(1, _columns);
        var cellHeight = bounds.Height / (double)Math.Max(1, _rows);
        var centerColumn = Math.Clamp(
            (int)Math.Floor(localX / Math.Max(0.001, cellWidth)),
            0,
            _columns - 1);
        var centerRow = Math.Clamp(
            (int)Math.Floor(localY / Math.Max(0.001, cellHeight)),
            0,
            _rows - 1);
        var columnRadius = Math.Max(
            1,
            (int)Math.Ceiling(radiusPixels / Math.Max(0.001, cellWidth)));
        var rowRadius = Math.Max(
            1,
            (int)Math.Ceiling(radiusPixels / Math.Max(0.001, cellHeight)));
        var radiusSquared = radiusPixels * radiusPixels;

        for (var row = Math.Max(0, centerRow - rowRadius);
             row <= Math.Min(_rows - 1, centerRow + rowRadius);
             row++)
        {
            var cellCenterY = (row + 0.5) * cellHeight;
            var distanceY = cellCenterY - localY;

            for (var column = Math.Max(0, centerColumn - columnRadius);
                 column <= Math.Min(_columns - 1, centerColumn + columnRadius);
                 column++)
            {
                var cellCenterX = (column + 0.5) * cellWidth;
                var distanceX = cellCenterX - localX;

                if (distanceX * distanceX + distanceY * distanceY <=
                    radiusSquared)
                {
                    _rawMotion[row * _columns + column] = false;
                }
            }
        }
    }

    private bool IsLikelyCursorArtifact(DetectedRegion detected)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        var detectedArea = Math.Max(1, detected.Area);

        foreach (var tracked in _trackedRegions)
        {
            if (tracked.IsForegroundIntroduction)
            {
                continue;
            }

            var intersection = IntersectionArea(
                tracked.MinimumRow,
                tracked.MaximumRow,
                tracked.MinimumColumn,
                tracked.MaximumColumn,
                detected.MinimumRow,
                detected.MaximumRow,
                detected.MinimumColumn,
                detected.MaximumColumn);

            if (intersection / (double)detectedArea >= 0.45)
            {
                return false;
            }
        }

        var left = detected.MinimumColumn *
            bounds.Width / (double)Math.Max(1, _columns);
        var right = (detected.MaximumColumn + 1) *
            bounds.Width / (double)Math.Max(1, _columns);
        var top = detected.MinimumRow *
            bounds.Height / (double)Math.Max(1, _rows);
        var bottom = (detected.MaximumRow + 1) *
            bounds.Height / (double)Math.Max(1, _rows);
        var localX = cursor.X - bounds.Left;
        var localY = cursor.Y - bounds.Top;
        var distanceX = localX < left
            ? left - localX
            : localX > right
                ? localX - right
                : 0.0;
        var distanceY = localY < top
            ? top - localY
            : localY > bottom
                ? localY - bottom
                : 0.0;
        var distance = Math.Sqrt(
            distanceX * distanceX +
            distanceY * distanceY);
        var width = Math.Max(1.0, right - left);
        var height = Math.Max(1.0, bottom - top);
        var areaPixels = width * height;
        var nearRadius = Math.Max(
            _settings.MotionZoneCursorSuppressionPixels * 2.2,
            56.0);

        return distance <= nearRadius &&
               areaPixels <=
                   _settings.MotionZoneCursorArtifactAreaPixels;
    }

    private void UpdateStableBoundsFromHistory(
        TrackedRegion tracked,
        DetectedRegion detected,
        long now)
    {
        if (tracked.Observations.Count == 0)
        {
            tracked.Observations.Add(
                new RegionObservation(
                    tracked.MinimumRow,
                    tracked.MaximumRow,
                    tracked.MinimumColumn,
                    tracked.MaximumColumn,
                    tracked.CreatedTicks));
        }

        tracked.Observations.Add(
            new RegionObservation(
                detected.MinimumRow,
                detected.MaximumRow,
                detected.MinimumColumn,
                detected.MaximumColumn,
                now));

        if (tracked.Observations.Count > 96)
        {
            tracked.Observations.RemoveRange(
                0,
                tracked.Observations.Count - 96);
        }

        RefreshTrackedBoundsFromHistory(
            tracked,
            now,
            tracked.Observations.Count <= 3);
    }

    private bool RefreshTrackedBoundsFromHistory(
        TrackedRegion tracked,
        long now,
        bool force = false)
    {
        var memoryTicks = ToStopwatchTicks(
            _settings.MotionZoneBoundsMemoryMilliseconds);
        var cutoff = now - memoryTicks;

        for (var index = tracked.Observations.Count - 1;
             index >= 0;
             index--)
        {
            if (tracked.Observations[index].Timestamp < cutoff)
            {
                tracked.Observations.RemoveAt(index);
            }
        }

        if (tracked.Observations.Count < 2)
        {
            return false;
        }

        var updateTicks = ToStopwatchTicks(
            _settings.MotionZoneBoundsUpdateMilliseconds);

        if (!force &&
            tracked.LastBoundsUpdateTicks != 0 &&
            now - tracked.LastBoundsUpdateTicks < updateTicks)
        {
            return false;
        }

        tracked.LastBoundsUpdateTicks = now;

        var observations = tracked.Observations;
        var count = observations.Count;
        int targetMinimumRow;
        int targetMaximumRow;
        int targetMinimumColumn;
        int targetMaximumColumn;
        var learning =
            now - tracked.CreatedTicks <
            ToStopwatchTicks(750);

        if (learning)
        {
            targetMinimumRow = observations[0].MinimumRow;
            targetMaximumRow = observations[0].MaximumRow;
            targetMinimumColumn = observations[0].MinimumColumn;
            targetMaximumColumn = observations[0].MaximumColumn;

            for (var index = 1; index < count; index++)
            {
                var observation = observations[index];
                targetMinimumRow = Math.Min(
                    targetMinimumRow,
                    observation.MinimumRow);
                targetMaximumRow = Math.Max(
                    targetMaximumRow,
                    observation.MaximumRow);
                targetMinimumColumn = Math.Min(
                    targetMinimumColumn,
                    observation.MinimumColumn);
                targetMaximumColumn = Math.Max(
                    targetMaximumColumn,
                    observation.MaximumColumn);
            }
        }
        else
        {
            var minimumRows = new int[count];
            var maximumRows = new int[count];
            var minimumColumns = new int[count];
            var maximumColumns = new int[count];

            for (var index = 0; index < count; index++)
            {
                var observation = observations[index];
                minimumRows[index] = observation.MinimumRow;
                maximumRows[index] = observation.MaximumRow;
                minimumColumns[index] = observation.MinimumColumn;
                maximumColumns[index] = observation.MaximumColumn;
            }

            Array.Sort(minimumRows);
            Array.Sort(maximumRows);
            Array.Sort(minimumColumns);
            Array.Sort(maximumColumns);

            var requiredSupport = Math.Clamp(
                (int)Math.Ceiling(
                    count *
                    _settings.MotionZoneBoundsOutlierFraction),
                2,
                Math.Min(8, count));
            var lowerIndex = requiredSupport - 1;
            var upperIndex = count - requiredSupport;

            targetMinimumRow = minimumRows[lowerIndex];
            targetMaximumRow = maximumRows[upperIndex];
            targetMinimumColumn = minimumColumns[lowerIndex];
            targetMaximumColumn = maximumColumns[upperIndex];
        }

        if (targetMinimumRow > targetMaximumRow ||
            targetMinimumColumn > targetMaximumColumn ||
            !IsMeaningfulOutputRegion(
                targetMinimumRow,
                targetMaximumRow,
                targetMinimumColumn,
                targetMaximumColumn))
        {
            return false;
        }

        var currentArea = Math.Max(
            1,
            RectangleArea(
                tracked.MinimumRow,
                tracked.MaximumRow,
                tracked.MinimumColumn,
                tracked.MaximumColumn));
        var targetArea = Math.Max(
            1,
            RectangleArea(
                targetMinimumRow,
                targetMaximumRow,
                targetMinimumColumn,
                targetMaximumColumn));
        var intersection = IntersectionArea(
            tracked.MinimumRow,
            tracked.MaximumRow,
            tracked.MinimumColumn,
            tracked.MaximumColumn,
            targetMinimumRow,
            targetMaximumRow,
            targetMinimumColumn,
            targetMaximumColumn);
        var distance =
            AxisGap(
                tracked.MinimumRow,
                tracked.MaximumRow,
                targetMinimumRow,
                targetMaximumRow) +
            AxisGap(
                tracked.MinimumColumn,
                tracked.MaximumColumn,
                targetMinimumColumn,
                targetMaximumColumn);

        if (intersection == 0 &&
            distance > Math.Max(
                1,
                _settings.MotionZoneTrackingGapCells))
        {
            return false;
        }

        var maximumGrowth = learning ? 2.25 : 1.65;

        if (targetArea > currentArea * maximumGrowth)
        {
            return false;
        }

        if (!learning &&
            targetArea < currentArea * 0.20 &&
            count < 8)
        {
            return false;
        }

        var changed =
            tracked.MinimumRow != targetMinimumRow ||
            tracked.MaximumRow != targetMaximumRow ||
            tracked.MinimumColumn != targetMinimumColumn ||
            tracked.MaximumColumn != targetMaximumColumn;

        if (!changed)
        {
            return false;
        }

        tracked.MinimumRow = targetMinimumRow;
        tracked.MaximumRow = targetMaximumRow;
        tracked.MinimumColumn = targetMinimumColumn;
        tracked.MaximumColumn = targetMaximumColumn;
        return true;
    }
}