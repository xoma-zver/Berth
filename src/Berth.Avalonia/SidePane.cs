using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Persistent docked pane of one workspace side: hosts the open docked-layer tool windows of
/// the side — at most one per group (INV-2) — in two fixed cells with a pair splitter between
/// them, stacked vertically on the left/right sides (Primary on top, spec TW-2.3) and
/// horizontally on the bottom (Primary on the left, TW-2.4). A closed group's cell collapses to
/// zero instead of leaving the tree, so forming or dissolving the pair never reattaches the
/// remaining window's host (spec TW-9.13); an open pair splits at the derived ratio of rule R1
/// (<see cref="LayoutState.GetPairRatio"/>, TW-2.7). Minimum sizes clamp at render only
/// (TW-2.8); releasing a pair splitter drag commits rule R2 as one SetSideRatio (TW-5.9).
/// </summary>
internal sealed class SidePane : Decorator
{
    private readonly bool _vertical;
    private readonly Grid _grid = new();
    private readonly Decorator _primaryHost = new();
    private readonly Decorator _secondaryHost = new();
    private readonly GridSplitter _splitter;

    public SidePane(ToolWindowSide side, BerthWorkspace workspace)
    {
        Name = side switch
        {
            ToolWindowSide.Left => "PART_LeftPane",
            ToolWindowSide.Right => "PART_RightPane",
            _ => "PART_BottomPane",
        };
        _vertical = side != ToolWindowSide.Bottom;

        _splitter = Splitters.Create(
            "PART_PairSplitter", _vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns);
        Splitters.CommitOnDragEnd(_splitter, () =>
        {
            var primary = Extent(_primaryHost);
            var total = primary + Extent(_secondaryHost);
            if (total > 0)
            {
                var share = BerthMetrics.ClampFraction(primary / total);
                workspace.Execute(s => s.SetSideRatio(side, share));
            }
        });

        if (_vertical)
        {
            _grid.RowDefinitions = [new(), new() { Height = GridLength.Auto }, new()];
            Grid.SetRow(_splitter, 1);
            Grid.SetRow(_secondaryHost, 2);
        }
        else
        {
            _grid.ColumnDefinitions = [new(), new() { Width = GridLength.Auto }, new()];
            Grid.SetColumn(_splitter, 1);
            Grid.SetColumn(_secondaryHost, 2);
        }

        _grid.Children.Add(_primaryHost);
        _grid.Children.Add(_splitter);
        _grid.Children.Add(_secondaryHost);
        Child = _grid;
    }

    /// <summary>
    /// Projects the side's open docked windows into the fixed cells: a lone group takes the
    /// whole pane, an empty one collapses; <paramref name="pairRatio"/> is the derived R1 share
    /// when both cells are occupied, null otherwise.
    /// </summary>
    public void Update(ToolWindowDecorator? primary, ToolWindowDecorator? secondary, double? pairRatio)
    {
        SetCell(_primaryHost, primary);
        SetCell(_secondaryHost, secondary);
        _splitter.IsVisible = pairRatio is not null;
        SetCellShare(0, pairRatio ?? (primary is not null ? 1 : 0), occupied: primary is not null);
        SetCellShare(2, pairRatio is { } ratio ? 1 - ratio : (secondary is not null ? 1 : 0), occupied: secondary is not null);
    }

    private static void SetCell(Decorator host, ToolWindowDecorator? decorator)
    {
        if (ReferenceEquals(host.Child, decorator))
        {
            return;
        }

        // The displaced occupant leaves through the draining detach: an evicted window may
        // reappear in a floating window later, and the old root's layout queue must not keep
        // naming it (see BerthWorkspace.DetachFromParent).
        if (host.Child is { } previous)
        {
            BerthWorkspace.DetachFromParent(previous);
        }

        if (decorator is not null)
        {
            BerthWorkspace.DetachFromParent(decorator);
            host.Child = decorator;
        }
    }

    private void SetCellShare(int index, double share, bool occupied)
    {
        var length = new GridLength(share, GridUnitType.Star);
        var min = occupied ? BerthMetrics.MinPaneSize : 0;
        if (_vertical)
        {
            var row = _grid.RowDefinitions[index];
            row.Height = length;
            row.MinHeight = min;
        }
        else
        {
            var column = _grid.ColumnDefinitions[index];
            column.Width = length;
            column.MinWidth = min;
        }
    }

    private double Extent(Control cell) => _vertical ? cell.Bounds.Height : cell.Bounds.Width;
}
