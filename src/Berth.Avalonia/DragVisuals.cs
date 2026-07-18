using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
/// The ghost passport of one drag gesture (spec TW-5.17 v0.26, DA-9.7 v0.17), assembled once
/// at the gesture start: the light face shown over any target — the stripe icon face of a
/// panel (the same face as the stripe button), the title chip of a tab — and the optional
/// content miniature shown outside every target, the herald of the take-out (TW-7.8, DA-9.7).
/// The miniature exists only for a subject whose view was built and attached at the start —
/// a hosted open panel, the active tab of a visible group (DA-9.6); the choice is fixed at
/// the start and never re-captured mid-gesture.
/// </summary>
internal sealed class GhostPassport
{
    public GhostPassport(Control lightFace, IImage? miniature, Size miniatureSize)
    {
        LightFace = lightFace;
        Miniature = miniature;
        MiniatureSize = miniatureSize;
    }

    /// <summary>The light image over targets; one control instance — exactly one visual lives per gesture.</summary>
    public Control LightFace { get; }

    /// <summary>Content miniature captured at the gesture start, or null — the light face then stays everywhere.</summary>
    public IImage? Miniature { get; }

    /// <summary>Display size of the miniature, capped at <see cref="BerthMetrics.GhostMiniatureMaxSize"/>.</summary>
    public Size MiniatureSize { get; }
}

/// <summary>
/// Shared chrome of the gesture visuals: opaque, theme-aware chips for the ghost faces and
/// the hint label, and the framed view of a content miniature. Transient per-gesture leaf
/// chrome (spec TW-9.13).
/// </summary>
internal static class GhostChrome
{
    /// <summary>An opaque themed chip around arbitrary content.</summary>
    public static Border Chip(Control child, Thickness padding)
    {
        var chip = new Border
        {
            Child = child,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = padding,
        };
        chip.ActualThemeVariantChanged += (_, _) => UpdateSurface(chip);
        UpdateSurface(chip);
        return chip;
    }

    /// <summary>A text chip, returning its text block — the tab ghost face and the hint label.</summary>
    public static Border Chip(out TextBlock text)
    {
        text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        return Chip(text, new Thickness(8, 4));
    }

    /// <summary>The tab ghost face: the familiar title chip (spec DA-9.7).</summary>
    public static Border TitleChip(string title)
    {
        var chip = Chip(out var text);
        text.Text = title;
        return chip;
    }

    /// <summary>The panel ghost face: the stripe icon face on an opaque chip (spec TW-5.17 v0.26).</summary>
    public static Border IconChip(string? iconKey, string title) =>
        Chip(new StripeIconFace(iconKey, title), new Thickness(2));

    /// <summary>The framed miniature view of the ghost outside every target (spec TW-5.17 v0.26).</summary>
    public static Control MiniatureView(IImage image, Size size) => new Border
    {
        BorderBrush = BerthBrushes.Separator,
        BorderThickness = new Thickness(1),
        Child = new Image
        {
            Source = image,
            Width = size.Width,
            Height = size.Height,
            Stretch = Stretch.Uniform,
        },
    };

    private static void UpdateSurface(Border chip) => chip.Background =
        chip.ActualThemeVariant == ThemeVariant.Dark
            ? BerthBrushes.DarkOverlaySurface
            : BerthBrushes.LightOverlaySurface;
}

/// <summary>
/// Visualization of one drag gesture behind the narrow contract of spec TW-5.17: the ghost at
/// the pointer and the visuals of the active target — the marker, the post-drop zone preview
/// and the hint — addressed in gesture coordinates and updated as one through
/// <see cref="UpdateTarget"/>. One instance lives per gesture; <see cref="HideAll"/> is also
/// the teardown. The overlay platform draws everything in the workspace
/// <see cref="DragLayer"/>; the windowed platform paints the ghost as an OS window above every
/// window of the workspace (= the reference's DialogDragImageView) and routes the marker into
/// the overlay of the window containing the target. The panel dock guide (TW-7.7, TW-7.1)
/// drives the same contract without ever showing a ghost — the hint then anchors at the
/// marker in the workspace layer.
/// </summary>
internal interface IDragVisual
{
    /// <summary>Whether the ghost is currently shown — the test observation point.</summary>
    public bool GhostVisible { get; }

    /// <summary>The hint text currently shown, or null — the test observation point (v0.26).</summary>
    public string? HintText { get; }

    /// <summary>Whether the ghost currently shows the content miniature — the test observation point (v0.26).</summary>
    public bool GhostShowsMiniature { get; }

    /// <summary>Shows the ghost from the gesture's passport.</summary>
    public void ShowGhost(GhostPassport passport);

    /// <summary>Moves the ghost next to the pointer, in gesture coordinates.</summary>
    public void MoveGhost(Point gesturePoint);

    /// <summary>
    /// Updates every target visual at once (spec TW-5.17 v0.26): over a target — the marker,
    /// the zone preview and the hint show, and the ghost switches to its light face; outside
    /// every target (null) they hide and the ghost switches to the miniature when one was
    /// captured at the start.
    /// </summary>
    public void UpdateTarget(DropTarget? target);

    /// <summary>Hides every visual and releases gesture resources (the ghost window).</summary>
    public void HideAll();
}

/// <summary>
/// The overlay-platform visual (spec TW-7.7): gesture space is the workspace, so the existing
/// <see cref="DragLayer"/> — which paints above the pseudo-window canvas — hosts the ghost,
/// the marker, the zone preview and the hint directly.
/// </summary>
internal sealed class OverlayDragVisual : IDragVisual
{
    private readonly DragLayer _layer;

    public OverlayDragVisual(DragLayer layer) => _layer = layer;

    public bool GhostVisible => _layer.GhostVisible;

    public string? HintText => _layer.HintText;

    public bool GhostShowsMiniature => _layer.GhostShowsMiniature;

    public void ShowGhost(GhostPassport passport) => _layer.ShowGhost(passport);

    public void MoveGhost(Point gesturePoint) => _layer.MoveGhost(gesturePoint);

    public void UpdateTarget(DropTarget? target)
    {
        if (target is null)
        {
            _layer.HideMarker();
            _layer.HideZone();
            _layer.SetHint(null);
            _layer.SetGhostMiniature(true);
            return;
        }

        _layer.ShowMarker(target.MarkerRect, target.AreaMarker);
        if (target.ZoneRect is { } zone)
        {
            _layer.ShowZone(zone);
        }
        else
        {
            _layer.HideZone();
        }

        _layer.SetHint(target.Hint);
        _layer.SetGhostMiniature(false);
    }

    public void HideAll() => _layer.HideAll();
}

/// <summary>
/// The windowed-platform visual (task 6.2): the ghost is an undecorated always-on-top OS
/// window following the pointer across every window of the workspace (= the reference's
/// DialogDragImageView; the in-layer ghost cannot leave its window); the marker paints in the
/// overlay of the window containing the target — the workspace <see cref="DragLayer"/> for
/// the main window, the per-window <see cref="MarkerOverlay"/> for a floating one; the
/// post-drop zone preview always paints in the workspace layer — stripe zones live in the
/// main window only (v0.26). The hint rides in the ghost window; without a ghost — the panel
/// dock guide of the frameless Float (TW-7.1) — it falls back to the workspace layer at the
/// marker.
/// </summary>
internal sealed class WindowedDragVisual : IDragVisual
{
    private readonly BerthWorkspace _workspace;
    private readonly DragLayer _mainLayer;
    private GhostWindow? _ghost;
    private GhostPassport? _passport;
    private MarkerOverlay? _markedOverlay;

    public WindowedDragVisual(BerthWorkspace workspace, DragLayer mainLayer)
    {
        _workspace = workspace;
        _mainLayer = mainLayer;
    }

    public bool GhostVisible => _ghost?.IsVisible == true;

    public string? HintText => _ghost is { } ghost ? ghost.HintText : _mainLayer.HintText;

    public bool GhostShowsMiniature => _ghost?.ShowsMiniature == true;

    public void ShowGhost(GhostPassport passport) => _passport = passport;

    public void MoveGhost(Point gesturePoint)
    {
        if (_passport is null)
        {
            return;
        }

        if (_ghost is null)
        {
            _ghost = new GhostWindow(_passport);
            _ghost.MoveTo(gesturePoint);
            _ghost.Show(); // positioned first: no flash at the origin
        }
        else
        {
            _ghost.MoveTo(gesturePoint);
        }
    }

    public void UpdateTarget(DropTarget? target)
    {
        if (target is null)
        {
            HideMarker();
            _mainLayer.HideZone();
            SetHint(null);
            _ghost?.SetMiniature(true);
            return;
        }

        ShowMarker(target);
        if (target.ZoneRect is { } zone && ToWorkspaceRect(zone) is { } zoneRect)
        {
            _mainLayer.ShowZone(zoneRect);
        }
        else
        {
            _mainLayer.HideZone();
        }

        SetHint(target.Hint);
        _ghost?.SetMiniature(false);
    }

    public void HideAll()
    {
        HideMarker();
        _mainLayer.HideZone();
        _mainLayer.SetHint(null);
        _ghost?.Close();
        _ghost = null;
    }

    /// <summary>The hint rides in the ghost window; the ghost-less dock guide anchors it at the marker in the workspace layer.</summary>
    private void SetHint(string? text)
    {
        if (_ghost is { } ghost)
        {
            ghost.SetHint(text);
            _mainLayer.SetHint(null);
        }
        else
        {
            _mainLayer.SetHint(text);
        }
    }

    private void ShowMarker(DropTarget target)
    {
        switch (target.WindowKey)
        {
            case FloatingWindowLayer.FloatingWindowBase floating:
                HideMarker();
                floating.Markers.Show(
                    GestureSpace.ToTopLevel(floating, target.MarkerRect), target.AreaMarker);
                _markedOverlay = floating.Markers;
                break;
            case TopLevel:
                // The main-window marker lives in the workspace DragLayer, whose coordinates
                // are the workspace's own.
                if (ToWorkspaceRect(target.MarkerRect) is { } rect)
                {
                    if (_markedOverlay is { } previous)
                    {
                        previous.Hide();
                        _markedOverlay = null;
                    }

                    _mainLayer.ShowMarker(rect, target.AreaMarker);
                }

                break;
        }
    }

    private void HideMarker()
    {
        _mainLayer.HideMarker();
        if (_markedOverlay is { } overlay)
        {
            overlay.Hide();
            _markedOverlay = null;
        }
    }

    /// <summary>A gesture rectangle in workspace coordinates, or null while the workspace is detached.</summary>
    private Rect? ToWorkspaceRect(Rect gesture)
    {
        if (TopLevel.GetTopLevel(_workspace) is not { } main)
        {
            return null;
        }

        var local = GestureSpace.ToTopLevel(main, gesture);
        return main.TranslatePoint(local.TopLeft, _workspace) is { } origin
            ? new Rect(origin, local.Size)
            : null;
    }
}

/// <summary>
/// The OS-window ghost of a windowed-platform drag (spec TW-5.17; = the reference's
/// DialogDragImageView, opacity 0.85): an undecorated, non-activating, topmost window
/// positioned next to the pointer in screen coordinates, stacking the subject's face — the
/// light face over targets, the content miniature outside them (v0.26) — above the optional
/// target hint. Never hit-testable for the gesture — pointer events stay with the capture
/// owner regardless.
/// </summary>
internal sealed class GhostWindow : Window
{
    private readonly Decorator _face;
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private readonly Control _lightFace;
    private readonly Control? _miniatureView;

    public GhostWindow(GhostPassport passport)
    {
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Opacity = 0.85; // the reference's THUMB_OPACITY
        _lightFace = passport.LightFace;
        _miniatureView = passport.Miniature is { } miniature
            ? GhostChrome.MiniatureView(miniature, passport.MiniatureSize)
            : null;
        _face = new Decorator { Child = _lightFace };
        _hint = GhostChrome.Chip(out _hintText);
        _hint.IsVisible = false;
        _hint.Margin = new Thickness(0, 4, 0, 0);
        _hint.HorizontalAlignment = HorizontalAlignment.Left;
        var stack = new StackPanel();
        stack.Children.Add(_face);
        stack.Children.Add(_hint);
        Content = stack;
    }

    /// <summary>The hint text currently shown, or null — the test observation point (v0.26).</summary>
    public string? HintText => _hint.IsVisible ? _hintText.Text : null;

    /// <summary>Whether the miniature is currently shown — the test observation point (v0.26).</summary>
    public bool ShowsMiniature => _miniatureView is not null && ReferenceEquals(_face.Child, _miniatureView);

    /// <summary>Moves the ghost next to the pointer, in gesture (screen) coordinates.</summary>
    public void MoveTo(Point gesturePoint) => Position = new PixelPoint(
        (int)Math.Round(gesturePoint.X + 12, MidpointRounding.AwayFromZero),
        (int)Math.Round(gesturePoint.Y + 12, MidpointRounding.AwayFromZero));

    /// <summary>Switches between the light face and the miniature; without a miniature the face stays (v0.26).</summary>
    public void SetMiniature(bool miniature)
    {
        var next = miniature && _miniatureView is not null ? _miniatureView : _lightFace;
        if (!ReferenceEquals(_face.Child, next))
        {
            _face.Child = next;
        }
    }

    /// <summary>Shows or hides (null) the target hint below the face (v0.26).</summary>
    public void SetHint(string? text)
    {
        _hintText.Text = text;
        _hint.IsVisible = text is not null;
    }
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
