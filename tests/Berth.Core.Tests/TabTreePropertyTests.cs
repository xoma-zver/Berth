using System.Collections.Immutable;
using CsCheck;
using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Property tests of normalization (spec DA-3.1, DA-3.2): normalizing any tree or dock area —
/// including garbage shares, degenerate splits and dangling activity — yields a state that
/// satisfies INV-D1…INV-D6, normalization is idempotent, and canonical input is returned as the
/// same instance.
/// </summary>
public class TabTreePropertyTests
{
    private static readonly ToolWindowRegistry EmptyRegistry = new();

    [Fact]
    public void DA_3_2_normalization_of_any_tree_is_idempotent()
    {
        GenNode(3).Sample(
            raw =>
            {
                var next = 0;
                var tree = Relabel(raw, ref next);
                var normalized = TabTreeNormalization.Normalize(tree);
                Assert.Same(normalized, TabTreeNormalization.Normalize(normalized));
            },
            iter: 20_000);
    }

    [Fact]
    public void DA_3_2_normalization_of_any_dock_area_satisfies_the_invariants_and_is_idempotent()
    {
        GenAreaShape.Sample(
            shape =>
            {
                var area = Materialize(shape);
                var normalized = TabTreeNormalization.Normalize(area);

                var layout = LayoutState.Empty with { DockArea = normalized };
                Assert.Empty(LayoutInvariants.Validate(layout, EmptyRegistry));
                Assert.Same(normalized, TabTreeNormalization.Normalize(normalized));
            },
            iter: 10_000);
    }

    [Fact]
    public void DA_3_2_canonical_trees_pass_normalization_unchanged()
    {
        GenCanonical(3, parentOrientation: null, allowEmptyGroup: true).Sample(
            raw =>
            {
                var next = 0;
                var tree = Relabel(raw, ref next);
                Assert.Same(tree, TabTreeNormalization.Normalize(tree));
            },
            iter: 20_000);
    }

    // ---- Arbitrary (garbage-tolerant) generators ----

    /// <summary>Shares: 70 % valid, the rest NaN, negative or above 1.</summary>
    private static readonly Gen<double> GenShare =
        from kind in Gen.Int[0, 9]
        from value in Gen.Double[0.05, 0.95]
        select kind switch
        {
            7 => double.NaN,
            8 => -value,
            9 => 1 + value,
            _ => value,
        };

    private static readonly Gen<SplitOrientation> GenOrientation =
        Gen.OneOfConst(SplitOrientation.Row, SplitOrientation.Column);

    /// <summary>
    /// Arbitrary tree with placeholder tab ids (unique within a group; <see cref="Relabel"/>
    /// makes them globally unique). Groups may be empty, actives may dangle, splits may have
    /// zero or one child or repeat the parent orientation.
    /// </summary>
    private static Gen<TabTreeNode> GenNode(int depth)
    {
        var group =
            from tabCount in Gen.Int[0, 3]
            from activeChoice in Gen.Int[0, tabCount + 1]
            select (TabTreeNode)MakeGroup(tabCount, activeChoice);
        if (depth <= 0)
        {
            return group;
        }

        var child =
            from node in GenNode(depth - 1)
            from share in GenShare
            select new SplitChild(node, share);
        var split =
            from orientation in GenOrientation
            from count in Gen.Int[0, 3]
            from children in child.Array[count]
            select (TabTreeNode)new SplitNode { Orientation = orientation, Children = [.. children] };
        return Gen.OneOf(group, group, split);
    }

    private static TabGroupNode MakeGroup(int tabCount, int activeChoice)
    {
        var tabs = Enumerable.Range(0, tabCount).Select(i => $"x{i}").ToImmutableArray();
        var active = activeChoice < tabCount
            ? tabs[activeChoice]
            : activeChoice == tabCount ? null : "ghost";
        return new TabGroupNode { Tabs = tabs, ActiveTabId = active };
    }

    private sealed record AreaShape(
        TabTreeNode MainRoot,
        int MainCurrentKind,
        (TabTreeNode Root, int CurrentKind)[] Windows,
        int ActiveChoice);

    /// <summary>
    /// Dock-area shape: current-tab kinds are 0 — none/first tab, 1 — first tab, 2 — unknown id;
    /// the active choice 0 is the main window, above it a window index that may be out of range.
    /// Window trees regularly come out tab-less, covering removal with active-host remapping.
    /// </summary>
    private static readonly Gen<AreaShape> GenAreaShape =
        from mainRoot in GenNode(3)
        from mainCurrentKind in Gen.Int[0, 2]
        from windowCount in Gen.Int[0, 3]
        from windows in (from root in GenNode(2) from kind in Gen.Int[1, 2] select (root, kind)).Array[windowCount]
        from activeChoice in Gen.Int[0, 4]
        select new AreaShape(mainRoot, mainCurrentKind, windows, activeChoice);

    private static DockAreaState Materialize(AreaShape shape)
    {
        var next = 0;
        var mainRoot = Relabel(shape.MainRoot, ref next);
        var windows = ImmutableArray.CreateBuilder<DocumentWindowState>(shape.Windows.Length);
        foreach (var (rawRoot, kind) in shape.Windows)
        {
            var root = Relabel(rawRoot, ref next);
            var current = (kind == 1 ? FirstTab(root) : null) ?? "ghost";
            windows.Add(new DocumentWindowState(new FloatingBounds(0, 0, 800, 600), root, current));
        }

        return new DockAreaState
        {
            Root = mainRoot,
            CurrentTabId = shape.MainCurrentKind switch
            {
                0 => null,
                1 => FirstTab(mainRoot),
                _ => "ghost",
            },
            Windows = windows.ToImmutable(),
            ActiveDockHost = shape.ActiveChoice == 0
                ? DockHost.MainWindow
                : DockHost.DocumentWindow(shape.ActiveChoice - 1),
        };
    }

    // ---- Canonical generator ----

    /// <summary>
    /// Trees that are canonical by construction: non-empty groups outside the root, splits with
    /// 2–4 children of alternating orientation, valid actives, shares normalized to sum 1.
    /// </summary>
    private static Gen<TabTreeNode> GenCanonical(int depth, SplitOrientation? parentOrientation, bool allowEmptyGroup)
    {
        var group =
            from tabCount in Gen.Int[allowEmptyGroup ? 0 : 1, 3]
            from activeIndex in Gen.Int[0, Math.Max(0, tabCount - 1)]
            select (TabTreeNode)MakeGroup(tabCount, tabCount == 0 ? 0 : activeIndex);
        if (depth <= 0)
        {
            return group;
        }

        var orientations = parentOrientation switch
        {
            null => GenOrientation,
            SplitOrientation.Row => Gen.Const(SplitOrientation.Column),
            _ => Gen.Const(SplitOrientation.Row),
        };
        var split =
            from orientation in orientations
            from count in Gen.Int[2, 4]
            from nodes in GenCanonical(depth - 1, orientation, allowEmptyGroup: false).Array[count]
            from rawShares in Gen.Double[0.05, 0.95].Array[count]
            select (TabTreeNode)MakeCanonicalSplit(orientation, nodes, rawShares);
        return Gen.OneOf(group, split);
    }

    private static SplitNode MakeCanonicalSplit(SplitOrientation orientation, TabTreeNode[] nodes, double[] rawShares)
    {
        var sum = rawShares.Sum();
        var children = ImmutableArray.CreateBuilder<SplitChild>(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            children.Add(new SplitChild(nodes[i], rawShares[i] / sum));
        }

        return new SplitNode { Orientation = orientation, Children = children.ToImmutable() };
    }

    // ---- Helpers ----

    /// <summary>
    /// Renames tabs to globally unique ids, keeping the active tab pointed at the same position
    /// (a dangling active like "ghost" stays dangling).
    /// </summary>
    private static TabTreeNode Relabel(TabTreeNode node, ref int next)
    {
        switch (node)
        {
            case TabGroupNode group:
            {
                var tabs = ImmutableArray.CreateBuilder<string>(group.Tabs.Length);
                for (var i = 0; i < group.Tabs.Length; i++)
                {
                    tabs.Add($"t{next++}");
                }

                var activeIndex = group.ActiveTabId is null ? -1 : group.Tabs.IndexOf(group.ActiveTabId);
                var active = group.ActiveTabId is null
                    ? null
                    : activeIndex >= 0 ? tabs[activeIndex] : group.ActiveTabId;
                return new TabGroupNode { Tabs = tabs.ToImmutable(), ActiveTabId = active };
            }

            case SplitNode split:
            {
                var children = ImmutableArray.CreateBuilder<SplitChild>(split.Children.Length);
                foreach (var child in split.Children)
                {
                    children.Add(child with { Node = Relabel(child.Node, ref next) });
                }

                return split with { Children = children.ToImmutable() };
            }

            default:
                return node;
        }
    }

    private static string? FirstTab(TabTreeNode node) => node switch
    {
        TabGroupNode group => group.Tabs.IsEmpty ? null : group.Tabs[0],
        SplitNode split => split.Children.Select(c => FirstTab(c.Node)).FirstOrDefault(t => t is not null),
        _ => null,
    };
}
