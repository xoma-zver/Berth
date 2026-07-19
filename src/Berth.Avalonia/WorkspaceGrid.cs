using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// The persistent docked layout of the workspace (TW-2.1): an outer grid stacking the main
/// row above the bottom pane — the bottom pane spans the full width between the stripes — and
/// an inner row of the left pane, the dock area and the right pane. Sides without an open
/// docked window collapse to zero instead of leaving the tree, so opening a neighbouring side
/// or the bottom pane only relays geometry around the remaining hosts (TW-9.13). Star
/// sizes follow the side weights (TW-2.5) with render-time minimums (TW-2.8); the drag of a
/// side splitter is pure visualization (ADR-0004), and its release commits the side weight —
/// the pane's share of the star-sized cells — as one SetSideSize from the rendered bounds
/// (TW-5.9, TW-2.6).
/// </summary>
internal sealed class WorkspaceGrid : Grid
{
    private readonly Grid _row = new();
    private readonly Border _dockArea = new() { Name = "PART_DockArea" };
    private readonly SidePane _left;
    private readonly SidePane _right;
    private readonly SidePane _bottom;
    private readonly GridSplitter _leftSplitter;
    private readonly GridSplitter _rightSplitter;
    private readonly GridSplitter _bottomSplitter;

    public WorkspaceGrid(BerthWorkspace workspace, Control dockContent)
    {
        _dockArea.Child = dockContent; // the dock-area tree projection (DA-9.6)
        _left = new SidePane(ToolWindowSide.Left, workspace);
        _right = new SidePane(ToolWindowSide.Right, workspace);
        _bottom = new SidePane(ToolWindowSide.Bottom, workspace);

        _leftSplitter = Splitters.Create("PART_LeftSideSplitter", GridResizeDirection.Columns);
        _rightSplitter = Splitters.Create("PART_RightSideSplitter", GridResizeDirection.Columns);
        _bottomSplitter = Splitters.Create("PART_BottomSplitter", GridResizeDirection.Rows);
        Splitters.CommitOnDragEnd(_leftSplitter, () => CommitSideWidth(workspace, ToolWindowSide.Left, _left));
        Splitters.CommitOnDragEnd(_rightSplitter, () => CommitSideWidth(workspace, ToolWindowSide.Right, _right));
        Splitters.CommitOnDragEnd(_bottomSplitter, () =>
        {
            var total = _row.Bounds.Height + _bottom.Bounds.Height;
            if (total > 0)
            {
                var mainShare = BerthMetrics.ClampFraction(_row.Bounds.Height / total);
                workspace.Execute(s => s.SetSideSize(ToolWindowSide.Bottom, 1 - mainShare));
            }
        });

        _row.ColumnDefinitions =
        [
            new(),
            new() { Width = GridLength.Auto },
            new() { MinWidth = BerthMetrics.MinPaneSize },
            new() { Width = GridLength.Auto },
            new(),
        ];
        SetColumn(_left, 0);
        SetColumn(_leftSplitter, 1);
        SetColumn(_dockArea, 2);
        SetColumn(_rightSplitter, 3);
        SetColumn(_right, 4);
        _row.Children.Add(_left);
        _row.Children.Add(_leftSplitter);
        _row.Children.Add(_dockArea);
        _row.Children.Add(_rightSplitter);
        _row.Children.Add(_right);

        RowDefinitions =
        [
            new() { MinHeight = BerthMetrics.MinPaneSize },
            new() { Height = GridLength.Auto },
            new(),
        ];
        SetRow(_row, 0);
        SetRow(_bottomSplitter, 1);
        SetRow(_bottom, 2);
        Children.Add(_row);
        Children.Add(_bottomSplitter);
        Children.Add(_bottom);
    }

    /// <summary>Fills the side cells from the host cache and lays the collapse/expand geometry of the docked layer.</summary>
    public void Update(LayoutState state, Func<ToolWindowSlot, ToolWindowDecorator?> docked)
    {
        var leftWeight = UpdateSide(state, ToolWindowSide.Left, _left, docked);
        var rightWeight = UpdateSide(state, ToolWindowSide.Right, _right, docked);
        var bottomWeight = UpdateSide(state, ToolWindowSide.Bottom, _bottom, docked);

        _leftSplitter.IsVisible = leftWeight > 0;
        _rightSplitter.IsVisible = rightWeight > 0;
        SetColumnShare(0, leftWeight, occupied: leftWeight > 0);
        SetColumnShare(2, Math.Max(0, 1 - leftWeight - rightWeight), occupied: true);
        SetColumnShare(4, rightWeight, occupied: rightWeight > 0);

        _bottomSplitter.IsVisible = bottomWeight > 0;
        RowDefinitions[0].Height = new GridLength(bottomWeight > 0 ? 1 - bottomWeight : 1, GridUnitType.Star);
        var bottomRow = RowDefinitions[2];
        bottomRow.Height = new GridLength(bottomWeight, GridUnitType.Star);
        bottomRow.MinHeight = bottomWeight > 0 ? BerthMetrics.MinPaneSize : 0;
    }

    /// <summary>Projects one side into its pane; returns the side's effective weight — zero when nothing docked is open there.</summary>
    private static double UpdateSide(
        LayoutState state, ToolWindowSide side, SidePane pane, Func<ToolWindowSlot, ToolWindowDecorator?> docked)
    {
        var primary = docked(new ToolWindowSlot(side, ToolWindowGroup.Primary));
        var secondary = docked(new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        pane.Update(primary, secondary, state.GetPairRatio(side));
        return primary is not null || secondary is not null ? state.GetSide(side).Weight : 0;
    }

    private void SetColumnShare(int index, double share, bool occupied)
    {
        var column = _row.ColumnDefinitions[index];
        column.Width = new GridLength(share, GridUnitType.Star);
        if (index != 2)
        {
            column.MinWidth = occupied ? BerthMetrics.MinPaneSize : 0;
        }
    }

    private void CommitSideWidth(BerthWorkspace workspace, ToolWindowSide side, SidePane pane)
    {
        // Collapsed cells contribute zero width, so the sum spans exactly the open panes and
        // the dock area — the star-sized cells of the row.
        var total = _left.Bounds.Width + _dockArea.Bounds.Width + _right.Bounds.Width;
        if (total > 0)
        {
            var weight = BerthMetrics.ClampFraction(pane.Bounds.Width / total);
            workspace.Execute(s => s.SetSideSize(side, weight));
        }
    }
}
