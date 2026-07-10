using Avalonia;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Overlay layer of the open Undock tool windows (spec TW-3.3): each entry hugs the side of
/// its placement at full extent — a side overlay takes the whole workspace height, including
/// the bottom pane area; the bottom overlay takes the whole width between the stripes — with
/// the thickness given by the window's <see cref="ToolWindowState.UndockWeight"/>, clamped to
/// the render minimum (TW-2.8). The overlay paints above the docked layout and never affects
/// its sizes.
/// </summary>
internal sealed class UndockOverlay : Panel
{
    private readonly List<(Control Control, ToolWindowSide Side, double Weight)> _entries = [];

    public UndockOverlay() => Name = "PART_UndockOverlay";

    public void AddOverlay(Control control, ToolWindowSide side, double weight)
    {
        _entries.Add((control, side, weight));
        Children.Add(control);
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
}
