using System.Windows;

namespace OledGuard;

internal readonly record struct MaskRegion(
    Rect NormalizedBounds,
    double Opacity);

internal readonly record struct MouseReveal(
    Point NormalizedPosition,
    double NormalizedRadiusX,
    double NormalizedRadiusY);
