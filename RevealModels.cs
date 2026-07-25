namespace OledGuardSimple;

internal readonly record struct RevealRegion(
    System.Windows.Rect NormalizedBounds,
    double Opacity);

internal readonly record struct CursorHole(
    System.Windows.Point NormalizedPosition,
    double NormalizedRadiusX,
    double NormalizedRadiusY);

internal readonly record struct DetectionResult(
    IReadOnlyList<System.Drawing.Rectangle> Components,
    double ChangedFraction);
