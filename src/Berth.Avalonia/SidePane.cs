using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Docked pane of one workspace side: hosts the open docked-layer tool windows of the side —
/// at most one per group (INV-2). A single open group takes the whole side; an open pair
/// stacks vertically on the left/right sides (Primary on top, spec TW-2.3) and horizontally on
/// the bottom (Primary on the left, TW-2.4), split at the side's
/// <see cref="SideState.CurrentRatio"/>. Minimum sizes clamp at render only (TW-2.8); the
/// splitter is a passive visual until drags reduce to core commands (ADR-0004, TW-5.9).
/// </summary>
internal sealed class SidePane : Decorator
{
    public SidePane(ToolWindowSide side, LayoutState state, ToolWindowRegistry registry)
    {
        Name = side switch
        {
            ToolWindowSide.Left => "PART_LeftPane",
            ToolWindowSide.Right => "PART_RightPane",
            _ => "PART_BottomPane",
        };

        var primary = OpenDocked(state, new ToolWindowSlot(side, ToolWindowGroup.Primary));
        var secondary = OpenDocked(state, new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        Child = primary is not null && secondary is not null
            ? SplitterGrid.Build(
                ToolWindowDecorator.For(primary, registry),
                ToolWindowDecorator.For(secondary, registry),
                state.GetSide(side).CurrentRatio,
                vertical: side != ToolWindowSide.Bottom,
                "PART_PairSplitter")
            : ToolWindowDecorator.For((primary ?? secondary)!, registry);
    }

    private static ToolWindowState? OpenDocked(LayoutState state, ToolWindowSlot slot) =>
        state.ToolWindows.FirstOrDefault(w =>
            w.IsOpen && w.Slot == slot && w.Mode.GetLayer() == ToolWindowLayer.Docked);
}
