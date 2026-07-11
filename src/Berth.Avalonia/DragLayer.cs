using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Berth.Controls;

/// <summary>
/// Overlay layer of a drag gesture (spec TW-5.17): the ghost chip following the pointer and
/// the insertion marker over the active drop target. Pure visualization — never hit-testable,
/// never part of the state (ADR-0004); leaf chrome by classification (TW-9.13). The layer is
/// also the pointer-capture owner of an active gesture: it lives in the permanent skeleton, so
/// re-projections triggered by external state changes never tear the capture (TW-5.17). The
/// narrow surface — show, move, hide — is the seam of phase 6: the browser keeps this overlay
/// implementation, the desktop adds an OS-window ghost behind the same calls (ADR-0006).
/// </summary>
internal sealed class DragLayer : Canvas
{
    private readonly Border _ghost;
    private readonly TextBlock _ghostTitle;
    private readonly Border _marker;

    public DragLayer()
    {
        Name = "PART_DragLayer";
        IsHitTestVisible = false;
        _ghostTitle = new TextBlock();
        _ghost = new Border
        {
            Name = "PART_DragGhost",
            Child = _ghostTitle,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Opacity = 0.85, // the reference's THUMB_OPACITY
            IsVisible = false,
        };
        _marker = new Border
        {
            Name = "PART_DropMarker",
            Background = BerthBrushes.DropMarker,
            IsVisible = false,
        };
        Children.Add(_marker);
        Children.Add(_ghost);
        ActualThemeVariantChanged += (_, _) => UpdateGhostSurface();
        UpdateGhostSurface();
    }

    /// <summary>Shows the ghost chip with the dragged subject's title (spec TW-5.17).</summary>
    public void ShowGhost(string title)
    {
        _ghostTitle.Text = title;
        _ghost.IsVisible = true;
    }

    /// <summary>Moves the ghost chip next to the pointer, in workspace coordinates.</summary>
    public void MoveGhost(Point position)
    {
        SetLeft(_ghost, position.X + 12);
        SetTop(_ghost, position.Y + 12);
    }

    /// <summary>Shows the insertion marker over the active drop target, in workspace coordinates.</summary>
    public void ShowMarker(Rect rect)
    {
        _marker.Width = rect.Width;
        _marker.Height = rect.Height;
        SetLeft(_marker, rect.X);
        SetTop(_marker, rect.Y);
        _marker.IsVisible = true;
    }

    /// <summary>Hides the insertion marker — the pointer is over no target.</summary>
    public void HideMarker() => _marker.IsVisible = false;

    /// <summary>Hides every gesture visual — the gesture ended or was cancelled.</summary>
    public void HideAll()
    {
        _ghost.IsVisible = false;
        _marker.IsVisible = false;
    }

    /// <summary>The skeleton brushes are translucent; the ghost needs an opaque surface to stay legible over any content.</summary>
    private void UpdateGhostSurface() => _ghost.Background = ActualThemeVariant == ThemeVariant.Dark
        ? BerthBrushes.DarkOverlaySurface
        : BerthBrushes.LightOverlaySurface;
}
