using Avalonia;

namespace Berth.Controls;

/// <summary>
/// Geometry of the post-drop zone preview (TW-5.17 v0.26): the workspace region a tool
/// window's content occupies in a given state, mirrored from the layout math of
/// <see cref="WorkspaceGrid"/>, <see cref="SidePane"/> and <see cref="UndockOverlay"/>. The
/// caller runs the drop's command sequence on the current state in memory — a pure core
/// function, never assigned — and reads the zone off the result, so the preview agrees with
/// the actual outcome by construction, pair formation and the derived R1 share included
/// (TW-2.7). Splitter thicknesses and render-time minimum clamps (TW-2.8) are ignored — the
/// preview is a translucent approximation, not a layout pass.
/// </summary>
internal static class DockZonePreview
{
    /// <summary>
    /// The zone the tool window occupies in the state, in workspace coordinates, or null when
    /// it occupies none — closed, or floating (a stripe drop never leaves a floating result:
    /// the docking SetMode of TW-7.8 runs before the preview is read).
    /// </summary>
    /// <param name="state">The in-memory drop result.</param>
    /// <param name="id">Id of the dragged tool window.</param>
    /// <param name="center">The docked center area — the workspace between the stripes — in workspace coordinates.</param>
    public static Rect? ZoneOf(LayoutState state, string id, Rect center)
    {
        var window = state.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, id, StringComparison.Ordinal));
        if (window is null || !window.IsOpen || center.Width <= 0 || center.Height <= 0)
        {
            return null;
        }

        var side = window.Slot.Side;
        switch (window.Mode.GetLayer())
        {
            case ToolWindowLayer.Overlay:
                return OverlayZone(state, side, center);
            case ToolWindowLayer.Docked:
                break;
            default:
                return null;
        }

        var pane = DockedPane(state, side, center);
        if (state.GetPairRatio(side) is not { } ratio)
        {
            return pane; // the sole occupant takes the whole pane (rules R3/R4)
        }

        // An open pair splits at the derived R1 share: vertically on the sides (Primary on
        // top, TW-2.3), horizontally on the bottom (Primary on the left, TW-2.4).
        var primary = window.Slot.Group == ToolWindowGroup.Primary;
        if (side == ToolWindowSide.Bottom)
        {
            var split = pane.Width * ratio;
            return primary
                ? new Rect(pane.X, pane.Y, split, pane.Height)
                : new Rect(pane.X + split, pane.Y, pane.Width - split, pane.Height);
        }

        var at = pane.Height * ratio;
        return primary
            ? new Rect(pane.X, pane.Y, pane.Width, at)
            : new Rect(pane.X, pane.Y + at, pane.Width, pane.Height - at);
    }

    /// <summary>The pane of one side in the docked layout — the star-share math of <see cref="WorkspaceGrid"/>.</summary>
    private static Rect DockedPane(LayoutState state, ToolWindowSide side, Rect center)
    {
        var bottomWeight = DockedWeight(state, ToolWindowSide.Bottom);
        var mainHeight = center.Height * (bottomWeight > 0 ? 1 - bottomWeight : 1);
        if (side == ToolWindowSide.Bottom)
        {
            // The bottom pane spans the full width between the stripes (TW-2.1).
            return new Rect(center.X, center.Y + mainHeight, center.Width, center.Height - mainHeight);
        }

        var width = center.Width * DockedWeight(state, side);
        var x = side == ToolWindowSide.Left ? center.X : center.Right - width;
        return new Rect(x, center.Y, width, mainHeight);
    }

    /// <summary>Effective weight of a side in the docked row: zero while nothing docked is open there (TW-9.13 collapse).</summary>
    private static double DockedWeight(LayoutState state, ToolWindowSide side) =>
        state.ToolWindows.Any(w =>
            w.IsOpen && w.Slot.Side == side && w.Mode.GetLayer() == ToolWindowLayer.Docked)
            ? state.GetSide(side).Weight
            : 0;

    /// <summary>The Undock overlay slot at full extent (TW-3.3) — the geometry of <see cref="UndockOverlay"/>.</summary>
    private static Rect OverlayZone(LayoutState state, ToolWindowSide side, Rect center)
    {
        var weight = state.GetSide(side).Weight;
        var width = Thickness(weight, center.Width);
        var height = Thickness(weight, center.Height);
        return side switch
        {
            ToolWindowSide.Left => new Rect(center.X, center.Y, width, center.Height),
            ToolWindowSide.Right => new Rect(center.Right - width, center.Y, width, center.Height),
            _ => new Rect(center.X, center.Bottom - height, center.Width, height),
        };
    }

    private static double Thickness(double weight, double extent) =>
        Math.Min(extent, Math.Max(BerthMetrics.MinPaneSize, weight * extent));
}
