using System.Collections.Immutable;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// One split node of a materialized tree (DA-2.1): a Grid laying the reconciled child
/// views along the split orientation with a splitter between each adjacent pair. Children are
/// matched to state nodes by tab overlap and placed by grid index without reordering the
/// visual children, so inserting or removing a sibling — and even rotating the split
/// (DA-5.9) — never reattaches the surviving subtrees (DA-9.6). Definitions and
/// splitters are leaf chrome, rebuilt on every update. Star sizes follow the shares (DA-2.2)
/// with render-time minimums that never touch the state (DA-E24); releasing a splitter drag
/// after actual movement commits one SetSplitShares changing only the adjacent pair
/// (DA-5.6, ADR-0004) — addressed to the tree's host: the main window or the panel (DA-1.3).
/// </summary>
internal sealed class SplitView : Grid
{
    private readonly TabTreeContext _context;
    private readonly List<Control> _childViews = [];
    private readonly List<GridSplitter> _splitters = [];
    private ImmutableArray<int> _path;
    private bool _vertical;

    public SplitView(TabTreeContext context) => _context = context;

    /// <summary>Tabs of the projected subtree — the reconciliation key (DA-1.3).</summary>
    public HashSet<string> Tabs { get; } = new(StringComparer.Ordinal);

    /// <summary>Child views in state order, splitters excluded.</summary>
    public IReadOnlyList<Control> ChildViews => _childViews;

    /// <summary>Reconciles the children against the state node and relays the grid around them.</summary>
    public void Update(SplitNode split, LayoutState state, ToolWindowRegistry registry, ImmutableArray<int> path)
    {
        _path = path;
        _vertical = split.Orientation == SplitOrientation.Column;

        Tabs.Clear();
        var childTabs = new HashSet<string>[split.Children.Length];
        for (var i = 0; i < split.Children.Length; i++)
        {
            childTabs[i] = DockTrees.TabsOf(split.Children[i].Node);
            Tabs.UnionWith(childTabs[i]);
        }

        // Match state children to existing views by tab overlap (INV-D2 makes tabs unique
        // keys). A kind mismatch discards the old view before anything new attaches: its
        // hosts return to the cache and reattach under the new structure — the whitelisted
        // rebuild of the addressed node (DA-9.6).
        var matches = new Control?[split.Children.Length];
        var used = new bool[_childViews.Count];
        for (var i = 0; i < split.Children.Length; i++)
        {
            for (var j = 0; j < _childViews.Count; j++)
            {
                if (used[j] || !TabTreeContext.TabsOfView(_childViews[j]).Overlaps(childTabs[i]))
                {
                    continue;
                }

                used[j] = true;
                var kindMatches = split.Children[i].Node is TabGroupNode
                    ? _childViews[j] is TabGroupView
                    : _childViews[j] is SplitView;
                if (kindMatches)
                {
                    matches[i] = _childViews[j];
                }
                else
                {
                    TabTreeContext.ReleaseHosts(_childViews[j]);
                    Children.Remove(_childViews[j]);
                }

                break;
            }
        }

        for (var j = 0; j < _childViews.Count; j++)
        {
            if (!used[j])
            {
                TabTreeContext.ReleaseHosts(_childViews[j]);
                Children.Remove(_childViews[j]);
            }
        }

        _childViews.Clear();
        for (var i = 0; i < split.Children.Length; i++)
        {
            var view = _context.Reconcile(split.Children[i].Node, matches[i], state, registry, path.Add(i));
            if (!Children.Contains(view))
            {
                Children.Add(view);
            }

            _childViews.Add(view);
        }

        RebuildChrome(split);
    }

    /// <summary>Definitions and splitters — leaf chrome (DA-9.6): rebuilt each pass; children are placed by index, never reattached.</summary>
    private void RebuildChrome(SplitNode split)
    {
        if (_vertical)
        {
            var rows = new RowDefinitions();
            for (var i = 0; i < split.Children.Length; i++)
            {
                if (i > 0)
                {
                    rows.Add(new RowDefinition { Height = GridLength.Auto });
                }

                rows.Add(new RowDefinition
                {
                    Height = new GridLength(split.Children[i].Share, GridUnitType.Star),
                    MinHeight = BerthMetrics.MinPaneSize,
                });
            }

            RowDefinitions = rows;
            ColumnDefinitions = [];
        }
        else
        {
            var columns = new ColumnDefinitions();
            for (var i = 0; i < split.Children.Length; i++)
            {
                if (i > 0)
                {
                    columns.Add(new ColumnDefinition { Width = GridLength.Auto });
                }

                columns.Add(new ColumnDefinition
                {
                    Width = new GridLength(split.Children[i].Share, GridUnitType.Star),
                    MinWidth = BerthMetrics.MinPaneSize,
                });
            }

            ColumnDefinitions = columns;
            RowDefinitions = [];
        }

        for (var i = 0; i < _childViews.Count; i++)
        {
            SetColumn(_childViews[i], _vertical ? 0 : 2 * i);
            SetRow(_childViews[i], _vertical ? 2 * i : 0);
        }

        foreach (var splitter in _splitters)
        {
            Children.Remove(splitter);
        }

        _splitters.Clear();
        for (var i = 0; i < _childViews.Count - 1; i++)
        {
            var splitter = Splitters.Create(
                "PART_DockSplitter", _vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns);
            var pairIndex = i;
            Splitters.CommitOnDragEnd(splitter, () => CommitShares(pairIndex));
            SetColumn(splitter, _vertical ? 0 : (2 * i) + 1);
            SetRow(splitter, _vertical ? (2 * i) + 1 : 0);
            Children.Add(splitter);
            _splitters.Add(splitter);
        }
    }

    /// <summary>
    /// One SetSplitShares from the rendered bounds of the adjacent pair (DA-5.6): the pair's
    /// total share is redistributed at the dragged ratio, every other share stays untouched.
    /// </summary>
    private void CommitShares(int pairIndex)
    {
        var first = _childViews[pairIndex];
        var second = _childViews[pairIndex + 1];
        var a = _vertical ? first.Bounds.Height : first.Bounds.Width;
        var b = _vertical ? second.Bounds.Height : second.Bounds.Width;
        if (a + b <= 0)
        {
            return;
        }

        var ratio = BerthMetrics.ClampFraction(a / (a + b));
        var path = _path;
        _context.Workspace.Execute(s =>
        {
            // The path is fresh — every state change re-projects and a drag is pure
            // visualization (ADR-0004) — but the walk stays guarded against surprises.
            if (_context.GetRoot(s) is not { } root
                || DockTrees.SplitAt(root, path) is not { } node
                || node.Children.Length <= pairIndex + 1)
            {
                return s;
            }

            var pairTotal = node.Children[pairIndex].Share + node.Children[pairIndex + 1].Share;
            var shares = ImmutableArray.CreateBuilder<double>(node.Children.Length);
            for (var i = 0; i < node.Children.Length; i++)
            {
                shares.Add(i == pairIndex
                    ? pairTotal * ratio
                    : i == pairIndex + 1 ? pairTotal * (1 - ratio) : node.Children[i].Share);
            }

            return _context.SetShares(s, path, shares.ToImmutable());
        });
    }
}
