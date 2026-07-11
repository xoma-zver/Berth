using System.Collections.Immutable;

namespace Berth.Controls;

/// <summary>
/// Read-only tree walks over the public node model, shared by the dock-area projection, the
/// tab menus and the focus wiring. Paths are child indices from the root (spec DA-1.3).
/// </summary>
internal static class DockTrees
{
    /// <summary>Groups of the tree in depth-first, left-to-right order (spec DA-6.3, DA-9.2).</summary>
    public static IEnumerable<TabGroupNode> Groups(TabTreeNode root)
    {
        switch (root)
        {
            case TabGroupNode group:
                yield return group;
                break;
            case SplitNode split:
                foreach (var child in split.Children)
                {
                    foreach (var group in Groups(child.Node))
                    {
                        yield return group;
                    }
                }

                break;
        }
    }

    /// <summary>Whether the tree contains the tab.</summary>
    public static bool ContainsTab(TabTreeNode root, string tabId) =>
        Groups(root).Any(g => g.Tabs.Contains(tabId, StringComparer.Ordinal));

    /// <summary>Whether the tab id is present in any tree of the layout — a dock-area host or a panel tree.</summary>
    public static bool LayoutContainsTab(LayoutState state, string tabId)
    {
        if (ContainsTab(state.DockArea.Root, tabId))
        {
            return true;
        }

        foreach (var window in state.DockArea.Windows)
        {
            if (ContainsTab(window.Root, tabId))
            {
                return true;
            }
        }

        foreach (var panel in state.ToolWindows)
        {
            if (ContainsTab(panel.ContentTree, tabId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tab ids of the subtree — the reconciliation key of the projection (groups have no identity, spec DA-1.3).</summary>
    public static HashSet<string> TabsOf(TabTreeNode node)
    {
        var tabs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups(node))
        {
            tabs.UnionWith(group.Tabs);
        }

        return tabs;
    }

    /// <summary>The split at the given path, or null when the path no longer addresses one.</summary>
    public static SplitNode? SplitAt(TabTreeNode root, ImmutableArray<int> path)
    {
        var node = root;
        foreach (var index in path)
        {
            if (node is not SplitNode split || index < 0 || index >= split.Children.Length)
            {
                return null;
            }

            node = split.Children[index].Node;
        }

        return node as SplitNode;
    }

    /// <summary>First non-empty group of the tree in depth-first order, or null.</summary>
    public static TabGroupNode? FirstNonEmptyGroup(TabTreeNode root) =>
        Groups(root).FirstOrDefault(g => !g.Tabs.IsEmpty);
}
