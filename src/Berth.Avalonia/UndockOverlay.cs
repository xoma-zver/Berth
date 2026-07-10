using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Berth.Controls;

/// <summary>
/// Overlay layer of the open Undock tool windows (spec TW-3.3): each entry hugs the side of
/// its placement at full extent — a side overlay takes the whole workspace height, including
/// the bottom pane area; the bottom overlay takes the whole width between the stripes — with
/// the thickness given by the side's <see cref="SideState.Weight"/> (the docked layer and the
/// overlay share one side width), clamped to the render minimum (TW-2.8). The overlay paints
/// above the docked layout on an opaque backdrop — the panels underneath must not show
/// through — and never affects their sizes.
/// </summary>
internal sealed class UndockOverlay : Panel
{
    private readonly List<(Control Control, ToolWindowSide Side, double Weight)> _entries = [];

    public UndockOverlay() => Name = "PART_UndockOverlay";

    public void AddOverlay(Control control, ToolWindowSide side, double weight)
    {
        var entry = new OverlayBackdrop(control);
        _entries.Add((entry, side, weight));
        Children.Add(entry);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var (control, side, weight) in _entries)
        {
            control.Measure(SlotFor(side, weight, availableSize).Size);
        }

        // The overlay never demands size of its own: it covers whatever the workspace gives.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (control, side, weight) in _entries)
        {
            control.Arrange(SlotFor(side, weight, finalSize));
        }

        return finalSize;
    }

    private static Rect SlotFor(ToolWindowSide side, double weight, Size area)
    {
        var width = Thickness(weight, area.Width);
        var height = Thickness(weight, area.Height);
        return side switch
        {
            ToolWindowSide.Left => new Rect(0, 0, width, area.Height),
            ToolWindowSide.Right => new Rect(area.Width - width, 0, width, area.Height),
            _ => new Rect(0, area.Height - height, area.Width, height),
        };
    }

    private static double Thickness(double weight, double extent) =>
        Math.Min(extent, Math.Max(BerthMetrics.MinPaneSize, weight * extent));

    /// <summary>
    /// Opaque backdrop of one overlay entry (spec TW-3.3): the skeleton brushes are
    /// translucent, so without a backdrop the panels underneath would show through the
    /// overlay. The surface color follows the theme variant; real theming is a later concern.
    /// </summary>
    private sealed class OverlayBackdrop : Border
    {
        public OverlayBackdrop(Control child)
        {
            Name = "PART_OverlayBackdrop";
            Child = child;
            ActualThemeVariantChanged += (_, _) => UpdateBackground();
            UpdateBackground();
        }

        private void UpdateBackground() => Background = ActualThemeVariant == ThemeVariant.Dark
            ? BerthBrushes.DarkOverlaySurface
            : BerthBrushes.LightOverlaySurface;
    }
}
