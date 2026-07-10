using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Docked pane of one workspace side: hosts the open docked-layer tool windows of the side —
/// at most one per group (INV-2). A single open group takes the whole side; an open pair
/// stacks vertically on the left/right sides (Primary on top, spec TW-2.3) and horizontally on
/// the bottom (Primary on the left, TW-2.4), split at the side's
/// <see cref="SideState.CurrentRatio"/>. Minimum sizes clamp at render only (TW-2.8);
/// releasing a pair splitter drag commits rule R2 as one SetSideRatio (TW-5.9, TW-2.7).
/// </summary>
internal sealed class SidePane : Decorator
{
    public SidePane(ToolWindowSide side, LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
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
                ToolWindowDecorator.For(primary, registry, workspace),
                ToolWindowDecorator.For(secondary, registry, workspace),
                state.GetSide(side).CurrentRatio,
                vertical: side != ToolWindowSide.Bottom,
                "PART_PairSplitter",
                primaryShare => workspace.Execute(s => s.SetSideRatio(side, primaryShare)))
            : ToolWindowDecorator.For((primary ?? secondary)!, registry, workspace);
    }

    private static ToolWindowState? OpenDocked(LayoutState state, ToolWindowSlot slot) =>
        state.ToolWindows.FirstOrDefault(w =>
            w.IsOpen && w.Slot == slot && w.Mode.GetLayer() == ToolWindowLayer.Docked);
}
