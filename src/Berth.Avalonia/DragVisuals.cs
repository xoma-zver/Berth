using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Berth.Controls;

/// <summary>
/// Coordinate space of a drag gesture (spec TW-5.17, task 6.2). On a platform with real
/// windows the gesture lives in screen coordinates and these helpers convert between a
/// window's local space and the shared screen space; on the overlay platform the gesture
/// space is the workspace itself — one TopLevel, no conversion. Rectangles map both corners,
/// staying consistent under any render scaling; per-monitor DPI subtleties are out of v1
/// scope, like elsewhere at the pixel boundary (<see cref="FloatingBoundsValidation"/>).
/// </summary>
internal static class GestureSpace
{
    /// <summary>
    /// Test seam: the headless platform ignores <see cref="Window.Position"/> in
    /// PointToScreen/PointToClient (probe, task 6.2), which would collapse every window of a
    /// multi-window test onto one origin. The fallback composes the window position with the
    /// local point instead; real platforms keep PointToScreen, which also accounts for
    /// decorations and scaling.
    /// </summary>
    internal static bool UseWindowPositionFallback { get; set; }

    /// <summary>A local point of the given TopLevel in gesture (screen) coordinates.</summary>
    public static Point FromTopLevel(TopLevel root, Point local)
    {
        if (UseWindowPositionFallback && root is Window window)
        {
            return new Point(window.Position.X + local.X, window.Position.Y + local.Y);
        }

        var screen = root.PointToScreen(local);
        return new Point(screen.X, screen.Y);
    }

    /// <summary>A gesture (screen) point in the local coordinates of the given TopLevel.</summary>
    public static Point ToTopLevel(TopLevel root, Point gesture)
    {
        if (UseWindowPositionFallback && root is Window window)
        {
            return new Point(gesture.X - window.Position.X, gesture.Y - window.Position.Y);
        }

        return root.PointToClient(new PixelPoint(
            (int)Math.Round(gesture.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(gesture.Y, MidpointRounding.AwayFromZero)));
    }

    /// <summary>A local rectangle of the given TopLevel in gesture coordinates.</summary>
    public static Rect FromTopLevel(TopLevel root, Rect local) =>
        new(FromTopLevel(root, local.TopLeft), FromTopLevel(root, local.BottomRight));

    /// <summary>A gesture rectangle in the local coordinates of the given TopLevel.</summary>
    public static Rect ToTopLevel(TopLevel root, Rect gesture) =>
        new(ToTopLevel(root, gesture.TopLeft), ToTopLevel(root, gesture.BottomRight));
}

/// <summary>
/// Visualization of one drag gesture behind the narrow contract of spec TW-5.17: the ghost
/// chip at the pointer and the marker over the active target, both addressed in gesture
/// coordinates. One instance lives per gesture; <see cref="HideAll"/> is also the teardown.
/// The overlay platform draws both in the workspace <see cref="DragLayer"/>; the windowed
/// platform paints the ghost as an OS window above every window of the workspace (= the
/// reference's DialogDragImageView) and routes the marker into the overlay of the window
/// containing the target.
/// </summary>
internal interface IDragVisual
{
    /// <summary>Whether the ghost is currently shown — the test observation point.</summary>
    public bool GhostVisible { get; }

    /// <summary>Shows the ghost chip with the dragged subject's title.</summary>
    public void ShowGhost(string title);

    /// <summary>Moves the ghost chip next to the pointer, in gesture coordinates.</summary>
    public void MoveGhost(Point gesturePoint);

    /// <summary>Shows the marker of the active drop target, in the window containing it.</summary>
    public void ShowMarker(DropTarget target);

    /// <summary>Hides the marker — the pointer is over no target.</summary>
    public void HideMarker();

    /// <summary>Hides every visual and releases gesture resources (the ghost window).</summary>
    public void HideAll();
}

/// <summary>
/// The overlay-platform visual (spec TW-7.7): gesture space is the workspace, so the existing
/// <see cref="DragLayer"/> — which paints above the pseudo-window canvas — hosts both the
/// ghost and the marker directly.
/// </summary>
internal sealed class OverlayDragVisual : IDragVisual
{
    private readonly DragLayer _layer;

    public OverlayDragVisual(DragLayer layer) => _layer = layer;

    public bool GhostVisible => _layer.GhostVisible;

    public void ShowGhost(string title) => _layer.ShowGhost(title);

    public void MoveGhost(Point gesturePoint) => _layer.MoveGhost(gesturePoint);

    public void ShowMarker(DropTarget target) => _layer.ShowMarker(target.MarkerRect, target.AreaMarker);

    public void HideMarker() => _layer.HideMarker();

    public void HideAll() => _layer.HideAll();
}

/// <summary>
/// The windowed-platform visual (task 6.2): the ghost is an undecorated always-on-top OS
/// window following the pointer across every window of the workspace (= the reference's
/// DialogDragImageView; the in-layer ghost cannot leave its window), and the marker paints in
/// the overlay of the window containing the target — the workspace <see cref="DragLayer"/>
/// for the main window, the per-window <see cref="MarkerOverlay"/> for a floating one.
/// </summary>
internal sealed class WindowedDragVisual : IDragVisual
{
    private readonly BerthWorkspace _workspace;
    private readonly DragLayer _mainLayer;
    private GhostWindow? _ghost;
    private string? _title;
    private MarkerOverlay? _markedOverlay;

    public WindowedDragVisual(BerthWorkspace workspace, DragLayer mainLayer)
    {
        _workspace = workspace;
        _mainLayer = mainLayer;
    }

    public bool GhostVisible => _ghost?.IsVisible == true;

    public void ShowGhost(string title) => _title = title;

    public void MoveGhost(Point gesturePoint)
    {
        if (_title is null)
        {
            return;
        }

        if (_ghost is null)
        {
            _ghost = new GhostWindow(_title);
            _ghost.MoveTo(gesturePoint);
            _ghost.Show(); // positioned first: no flash at the origin
        }
        else
        {
            _ghost.MoveTo(gesturePoint);
        }
    }

    public void ShowMarker(DropTarget target)
    {
        switch (target.WindowKey)
        {
            case FloatingWindowLayer.FloatingWindowBase floating:
                HideMarker();
                floating.Markers.Show(
                    GestureSpace.ToTopLevel(floating, target.MarkerRect), target.AreaMarker);
                _markedOverlay = floating.Markers;
                break;
            case TopLevel main:
                // The main-window marker lives in the workspace DragLayer, whose coordinates
                // are the workspace's own.
                var local = GestureSpace.ToTopLevel(main, target.MarkerRect);
                if (main.TranslatePoint(local.TopLeft, _workspace) is { } origin)
                {
                    if (_markedOverlay is { } previous)
                    {
                        previous.Hide();
                        _markedOverlay = null;
                    }

                    _mainLayer.ShowMarker(new Rect(origin, local.Size), target.AreaMarker);
                }

                break;
        }
    }

    public void HideMarker()
    {
        _mainLayer.HideMarker();
        if (_markedOverlay is { } overlay)
        {
            overlay.Hide();
            _markedOverlay = null;
        }
    }

    public void HideAll()
    {
        HideMarker();
        _ghost?.Close();
        _ghost = null;
    }
}

/// <summary>
/// The OS-window ghost of a windowed-platform drag (spec TW-5.17; = the reference's
/// DialogDragImageView, opacity 0.85): an undecorated, non-activating, topmost chip window
/// positioned next to the pointer in screen coordinates. Never hit-testable for the gesture —
/// pointer events stay with the capture owner regardless.
/// </summary>
internal sealed class GhostWindow : Window
{
    public GhostWindow(string title)
    {
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Opacity = 0.85; // the reference's THUMB_OPACITY
        var chip = new Border
        {
            Child = new TextBlock { Text = title },
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
        };
        Content = chip;
        ActualThemeVariantChanged += (_, _) => UpdateSurface(chip);
        UpdateSurface(chip);
    }

    /// <summary>Moves the ghost next to the pointer, in gesture (screen) coordinates.</summary>
    public void MoveTo(Point gesturePoint) => Position = new PixelPoint(
        (int)Math.Round(gesturePoint.X + 12, MidpointRounding.AwayFromZero),
        (int)Math.Round(gesturePoint.Y + 12, MidpointRounding.AwayFromZero));

    private void UpdateSurface(Border chip) => chip.Background = ActualThemeVariant == ThemeVariant.Dark
        ? BerthBrushes.DarkOverlaySurface
        : BerthBrushes.LightOverlaySurface;
}

/// <summary>
/// Marker overlay of one floating window (spec TW-5.17, task 6.2): a thin non-hit-testable
/// canvas above the window's content, hosting the insertion line or area preview of a drop
/// target inside that window. Part of the window's permanent chrome; shown and hidden by the
/// gesture visual.
/// </summary>
internal sealed class MarkerOverlay : Canvas
{
    private readonly Border _marker;

    public MarkerOverlay()
    {
        Name = "PART_WindowDropMarker";
        IsHitTestVisible = false;
        _marker = new Border { IsVisible = false };
        Children.Add(_marker);
    }

    /// <summary>Shows the marker, in the window's local coordinates.</summary>
    public void Show(Rect rect, bool isArea)
    {
        _marker.Background = isArea ? BerthBrushes.DropAreaPreview : BerthBrushes.DropMarker;
        _marker.Width = rect.Width;
        _marker.Height = rect.Height;
        SetLeft(_marker, rect.X);
        SetTop(_marker, rect.Y);
        _marker.IsVisible = true;
    }

    /// <summary>Hides the marker.</summary>
    public void Hide() => _marker.IsVisible = false;
}
