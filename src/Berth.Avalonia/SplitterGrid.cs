using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Two children split by a live splitter — [share ★ | splitter | 1−share ★] along one axis —
/// with render-time minimums on both cells (spec TW-2.8). Shared by the side pair stacks
/// (TW-2.3, TW-2.4) and the bottom pane row of the workspace (TW-2.1). The drag itself is pure
/// visualization (ADR-0004); releasing after actual movement commits the first child's share
/// of the two cells through the callback as one core command (TW-5.9).
/// </summary>
internal static class SplitterGrid
{
    /// <summary>
    /// Wires a commit to run when a drag ends after actual movement: Thumb raises
    /// DragCompleted for a plain click too, which must not rewrite the state — no gesture,
    /// no command (ADR-0004); a clamped render would otherwise snap into the state and even
    /// layout rounding would drift it on every click.
    /// </summary>
    public static void CommitOnDragEnd(GridSplitter splitter, Action commit)
    {
        var moved = false;
        splitter.DragStarted += (_, _) => moved = false;
        splitter.DragDelta += (_, e) => moved |= e.Vector != default;
        splitter.DragCompleted += (_, _) =>
        {
            if (moved)
            {
                commit();
            }
        };
    }

    public static Grid Build(
        Control first,
        Control second,
        double firstShare,
        bool vertical,
        string splitterName,
        Action<double> commitFirstShare)
    {
        var grid = new Grid();
        var splitter = new GridSplitter
        {
            Name = splitterName,
            Background = BerthBrushes.Separator,
            ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            Focusable = false, // keyboard resize would bypass the release commit
            MinWidth = 0, // theme minimums would widen the 4px separator
            MinHeight = 0,
        };
        CommitOnDragEnd(splitter, () =>
        {
            var firstExtent = vertical ? first.Bounds.Height : first.Bounds.Width;
            var total = firstExtent + (vertical ? second.Bounds.Height : second.Bounds.Width);
            if (total > 0)
            {
                commitFirstShare(BerthMetrics.ClampFraction(firstExtent / total));
            }
        });

        if (vertical)
        {
            grid.RowDefinitions =
            [
                new RowDefinition { Height = new GridLength(firstShare, GridUnitType.Star), MinHeight = BerthMetrics.MinPaneSize },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1 - firstShare, GridUnitType.Star), MinHeight = BerthMetrics.MinPaneSize },
            ];
            splitter.Height = BerthMetrics.SplitterThickness;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }
        else
        {
            grid.ColumnDefinitions =
            [
                new ColumnDefinition { Width = new GridLength(firstShare, GridUnitType.Star), MinWidth = BerthMetrics.MinPaneSize },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1 - firstShare, GridUnitType.Star), MinWidth = BerthMetrics.MinPaneSize },
            ];
            splitter.Width = BerthMetrics.SplitterThickness;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }
}
