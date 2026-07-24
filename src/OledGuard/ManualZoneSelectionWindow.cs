using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;
using WpfCursors = System.Windows.Input.Cursors;
using WpfKey = System.Windows.Input.Key;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OledGuard;

internal sealed class ManualZoneSelectionWindow : Window
{
    private readonly FormsScreen _screen;
    private readonly Canvas _canvas;
    private readonly Border _selection;

    private NativeMethods.Point _start;
    private bool _dragging;
    private IntPtr _handle;

    public ManualZoneSelectionWindow(
        FormsScreen screen)
    {
        _screen = screen;

        WindowStyle =
            WindowStyle.None;
        ResizeMode =
            ResizeMode.NoResize;
        AllowsTransparency = true;
        Background =
            new SolidColorBrush(
                Color.FromArgb(
                    34,
                    0,
                    0,
                    0));
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = WpfCursors.Cross;
        Title =
            "OledGuard — Dessiner une zone visible";

        _canvas =
            new Canvas();

        _selection =
            new Border
            {
                BorderBrush =
                    Brushes.DeepSkyBlue,
                BorderThickness =
                    new Thickness(
                        2),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            42,
                            0,
                            191,
                            255)),
                Visibility =
                    Visibility.Collapsed,
                IsHitTestVisible = false
            };

        var instruction =
            new Border
            {
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            220,
                            20,
                            20,
                            20)),
                CornerRadius =
                    new CornerRadius(
                        8),
                Padding =
                    new Thickness(
                        14,
                        9,
                        14,
                        9),
                Child =
                    new TextBlock
                    {
                        Text =
                            "Glissez pour garder une zone visible — Échap ou clic droit pour annuler",
                        Foreground =
                            Brushes.White,
                        FontSize = 14,
                        FontWeight =
                            FontWeights.SemiBold
                    }
            };

        Canvas.SetLeft(
            instruction,
            24);
        Canvas.SetTop(
            instruction,
            24);

        _canvas.Children.Add(
            _selection);
        _canvas.Children.Add(
            instruction);
        Content =
            _canvas;

        SourceInitialized +=
            OnSourceInitialized;
        PreviewMouseLeftButtonDown +=
            OnMouseLeftButtonDown;
        PreviewMouseMove +=
            OnMouseMove;
        PreviewMouseLeftButtonUp +=
            OnMouseLeftButtonUp;
        PreviewMouseRightButtonDown +=
            (_, _) => CancelSelection();
        PreviewKeyDown +=
            (_, eventArgs) =>
            {
                if (eventArgs.Key ==
                    WpfKey.Escape)
                {
                    eventArgs.Handled = true;
                    CancelSelection();
                }
            };
    }

    public Rect? SelectedNormalizedBounds
    {
        get;
        private set;
    }

    private void OnSourceInitialized(
        object? sender,
        EventArgs e)
    {
        _handle =
            new WindowInteropHelper(
                this).Handle;
        var bounds =
            _screen.Bounds;

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpShowWindow);

        Activate();
        Focus();
    }

    private void OnMouseLeftButtonDown(
        object sender,
        WpfMouseButtonEventArgs eventArgs)
    {
        if (!NativeMethods.GetCursorPos(
                out _start))
        {
            return;
        }

        _dragging = true;
        _selection.Visibility =
            Visibility.Visible;
        CaptureMouse();
        UpdateSelection(
            _start);
        eventArgs.Handled = true;
    }

    private void OnMouseMove(
        object sender,
        WpfMouseEventArgs eventArgs)
    {
        if (!_dragging ||
            !NativeMethods.GetCursorPos(
                out var current))
        {
            return;
        }

        UpdateSelection(
            current);
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonUp(
        object sender,
        WpfMouseButtonEventArgs eventArgs)
    {
        if (!_dragging ||
            !NativeMethods.GetCursorPos(
                out var current))
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        eventArgs.Handled = true;

        var bounds =
            _screen.Bounds;
        var left =
            Math.Clamp(
                Math.Min(
                    _start.X,
                    current.X),
                bounds.Left,
                bounds.Right);
        var top =
            Math.Clamp(
                Math.Min(
                    _start.Y,
                    current.Y),
                bounds.Top,
                bounds.Bottom);
        var right =
            Math.Clamp(
                Math.Max(
                    _start.X,
                    current.X),
                bounds.Left,
                bounds.Right);
        var bottom =
            Math.Clamp(
                Math.Max(
                    _start.Y,
                    current.Y),
                bounds.Top,
                bounds.Bottom);

        if (right - left < 12 ||
            bottom - top < 12)
        {
            CancelSelection();
            return;
        }

        SelectedNormalizedBounds =
            new Rect(
                (left -
                 bounds.Left) /
                (double)Math.Max(
                    1,
                    bounds.Width),
                (top -
                 bounds.Top) /
                (double)Math.Max(
                    1,
                    bounds.Height),
                (right - left) /
                (double)Math.Max(
                    1,
                    bounds.Width),
                (bottom - top) /
                (double)Math.Max(
                    1,
                    bounds.Height));
        DialogResult = true;
        Close();
    }

    private void UpdateSelection(
        NativeMethods.Point current)
    {
        var bounds =
            _screen.Bounds;
        var left =
            Math.Clamp(
                Math.Min(
                    _start.X,
                    current.X),
                bounds.Left,
                bounds.Right);
        var top =
            Math.Clamp(
                Math.Min(
                    _start.Y,
                    current.Y),
                bounds.Top,
                bounds.Bottom);
        var right =
            Math.Clamp(
                Math.Max(
                    _start.X,
                    current.X),
                bounds.Left,
                bounds.Right);
        var bottom =
            Math.Clamp(
                Math.Max(
                    _start.Y,
                    current.Y),
                bounds.Top,
                bounds.Bottom);
        var scaleX =
            ActualWidth /
            Math.Max(
                1.0,
                bounds.Width);
        var scaleY =
            ActualHeight /
            Math.Max(
                1.0,
                bounds.Height);

        Canvas.SetLeft(
            _selection,
            (left -
             bounds.Left) *
            scaleX);
        Canvas.SetTop(
            _selection,
            (top -
             bounds.Top) *
            scaleY);
        _selection.Width =
            Math.Max(
                1.0,
                (right - left) *
                scaleX);
        _selection.Height =
            Math.Max(
                1.0,
                (bottom - top) *
                scaleY);
    }

    private void CancelSelection()
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        DialogResult = false;
        Close();
    }
}