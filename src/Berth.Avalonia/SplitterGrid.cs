using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Two children split by a passive separator — [share ★ | splitter | 1−share ★] along one
/// axis — with render-time minimums on both cells (spec TW-2.8). Shared by the side pair
/// stacks (TW-2.3, TW-2.4) and the bottom pane row of the workspace (TW-2.1); the splitter is
/// a passive visual until drags reduce to core commands (ADR-0004, TW-5.9).
/// </summary>
internal static class SplitterGrid
{
    public static Grid Build(Control first, Control second, double firstShare, bool vertical, string splitterName)
    {
        var grid = new Grid();
        var splitter = new Border { Name = splitterName, Background = BerthBrushes.Separator };
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
