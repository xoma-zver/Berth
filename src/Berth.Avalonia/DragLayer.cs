using Avalonia;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Overlay layer of a drag gesture (spec TW-5.17): the ghost following the pointer — the light
/// face of the dragged subject over a target, its content miniature outside every target
/// (v0.26) — plus the target visuals: the insertion marker, the translucent post-drop zone
/// preview and the «Move to {slot}» hint label. Pure visualization — never hit-testable, never
/// part of the state (ADR-0004); leaf chrome by classification (TW-9.13). The layer is also
/// the pointer-capture owner of an active gesture: it lives in the permanent skeleton, so
/// re-projections triggered by external state changes never tear the capture (TW-5.17). The
/// narrow surface is the seam between the platforms: the browser keeps this overlay
/// implementation for everything, the desktop routes the ghost into an OS window and reuses
/// this layer for the main window's markers, zones and the ghost-less hint of the dock guide
/// (ADR-0006).
/// </summary>
internal sealed class DragLayer : Canvas
{
    private readonly Border _ghost;
    private readonly Border _marker;
    private readonly Border _zone;
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private Control? _lightFace;
    private Control? _miniatureView;
    private Point _ghostPosition;
    private Rect _markerRect;

    public DragLayer()
    {
        Name = "PART_DragLayer";
        IsHitTestVisible = false;
        _ghost = new Border
        {
            Name = "PART_DragGhost",
            Opacity = 0.85, // the reference's THUMB_OPACITY
            IsVisible = false,
        };
        _marker = new Border
        {
            Name = "PART_DropMarker",
            Background = BerthBrushes.DropMarker,
            IsVisible = false,
        };
        _zone = new Border
        {
            Name = "PART_DropZonePreview",
            Background = BerthBrushes.DropAreaPreview,
            IsVisible = false,
        };
        _hint = GhostChrome.Chip(out _hintText);
        _hint.Name = "PART_DropHint";
        _hint.IsVisible = false;
        Children.Add(_zone);
        Children.Add(_marker);
        Children.Add(_hint);
        Children.Add(_ghost);
    }

    /// <summary>Whether the ghost is currently shown.</summary>
    public bool GhostVisible => _ghost.IsVisible;

    /// <summary>The hint text currently shown, or null — the test observation point (spec TW-5.17).</summary>
    public string? HintText => _hint.IsVisible ? _hintText.Text : null;

    /// <summary>Whether the ghost currently shows the content miniature (spec TW-5.17, v0.26).</summary>
    public bool GhostShowsMiniature =>
        _ghost.IsVisible && _miniatureView is not null && ReferenceEquals(_ghost.Child, _miniatureView);

    /// <summary>Shows the ghost with the passport's light face (spec TW-5.17).</summary>
    public void ShowGhost(GhostPassport passport)
    {
        _lightFace = passport.LightFace;
        _miniatureView = passport.Miniature is { } miniature
            ? GhostChrome.MiniatureView(miniature, passport.MiniatureSize)
            : null;
        _ghost.Child = _lightFace;
        _ghost.IsVisible = true;
    }

    /// <summary>Moves the ghost next to the pointer, in workspace coordinates.</summary>
    public void MoveGhost(Point position)
    {
        _ghostPosition = new Point(position.X + 12, position.Y + 12);
        SetLeft(_ghost, _ghostPosition.X);
        SetTop(_ghost, _ghostPosition.Y);
        PlaceHint();
    }

    /// <summary>
    /// Switches the ghost between the light face (over a target) and the content miniature
    /// (outside every target, spec TW-5.17 v0.26); without a captured miniature the light
    /// face stays.
    /// </summary>
    public void SetGhostMiniature(bool miniature)
    {
        if (!_ghost.IsVisible || _lightFace is null)
        {
            return;
        }

        var next = miniature && _miniatureView is not null ? _miniatureView : _lightFace;
        if (!ReferenceEquals(_ghost.Child, next))
        {
            _ghost.Child = next;
            PlaceHint();
        }
    }

    /// <summary>
    /// Shows the marker over the active drop target, in workspace coordinates: the position
    /// fill of a stripe zone or the area preview of a wedge/center (both translucent), or the
    /// opaque insertion line of a tab strip (spec TW-5.17, DA-9.7).
    /// </summary>
    public void ShowMarker(Rect rect, bool isArea = false)
    {
        _marker.Background = isArea ? BerthBrushes.DropAreaPreview : BerthBrushes.DropMarker;
        _marker.Width = rect.Width;
        _marker.Height = rect.Height;
        SetLeft(_marker, rect.X);
        SetTop(_marker, rect.Y);
        _markerRect = rect;
        _marker.IsVisible = true;
        PlaceHint();
    }

    /// <summary>Hides the insertion marker — the pointer is over no target.</summary>
    public void HideMarker() => _marker.IsVisible = false;

    /// <summary>Shows the translucent post-drop zone preview, in workspace coordinates (spec TW-5.17 v0.26).</summary>
    public void ShowZone(Rect rect)
    {
        _zone.Width = rect.Width;
        _zone.Height = rect.Height;
        SetLeft(_zone, rect.X);
        SetTop(_zone, rect.Y);
        _zone.IsVisible = true;
    }

    /// <summary>Hides the zone preview.</summary>
    public void HideZone() => _zone.IsVisible = false;

    /// <summary>
    /// Shows or hides (null) the target hint label (spec TW-5.17 v0.26): anchored under the
    /// ghost while one is shown, else next to the marker — the ghost-less path of the panel
    /// dock guide (TW-7.7, TW-7.1).
    /// </summary>
    public void SetHint(string? text)
    {
        _hintText.Text = text;
        _hint.IsVisible = text is not null;
        PlaceHint();
    }

    /// <summary>Hides every gesture visual — the gesture ended or was cancelled.</summary>
    public void HideAll()
    {
        _ghost.IsVisible = false;
        _ghost.Child = null;
        _lightFace = null;
        _miniatureView = null;
        _marker.IsVisible = false;
        _zone.IsVisible = false;
        _hint.IsVisible = false;
    }

    private void PlaceHint()
    {
        if (!_hint.IsVisible)
        {
            return;
        }

        if (_ghost.IsVisible)
        {
            _ghost.Measure(Size.Infinity);
            SetLeft(_hint, _ghostPosition.X);
            SetTop(_hint, _ghostPosition.Y + _ghost.DesiredSize.Height + 4);
        }
        else if (_marker.IsVisible)
        {
            _hint.Measure(Size.Infinity);
            var x = Math.Max(0, Math.Min(_markerRect.Right + 8, Bounds.Width - _hint.DesiredSize.Width));
            SetLeft(_hint, x);
            SetTop(_hint, Math.Max(0, _markerRect.Center.Y - (_hint.DesiredSize.Height / 2)));
        }
    }
}
