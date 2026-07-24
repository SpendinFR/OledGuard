using System.Windows;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    private readonly List<Rect>
        _manualRevealZones = new();

    public void SetManualRevealZones(
        IReadOnlyList<Rect> zones)
    {
        lock (_sync)
        {
            _manualRevealZones.Clear();

            foreach (var zone in zones)
            {
                var left =
                    Math.Clamp(
                        zone.Left,
                        0.0,
                        1.0);
                var top =
                    Math.Clamp(
                        zone.Top,
                        0.0,
                        1.0);
                var right =
                    Math.Clamp(
                        zone.Right,
                        0.0,
                        1.0);
                var bottom =
                    Math.Clamp(
                        zone.Bottom,
                        0.0,
                        1.0);

                if (right - left < 0.002 ||
                    bottom - top < 0.002)
                {
                    continue;
                }

                _manualRevealZones.Add(
                    new Rect(
                        left,
                        top,
                        right - left,
                        bottom - top));
            }

            _maskDirty = true;
        }
    }

    private void AppendManualRevealZones(
        List<MaskRegion> result)
    {
        foreach (var zone in
                 _manualRevealZones)
        {
            result.Add(
                new MaskRegion(
                    zone,
                    0.0));
        }
    }
}
