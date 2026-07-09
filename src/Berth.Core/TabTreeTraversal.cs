namespace Berth;

/// <summary>
/// Read-only traversal helpers over tab-group trees, shared by normalization and invariant
/// validation. Depth-first, left-to-right — the traversal order of spec DA-6.3 and DA-9.2.
/// </summary>
internal static class TabTreeTraversal
{
    /// <summary>Groups of the tree in depth-first, left-to-right order.</summary>
    public static IEnumerable<TabGroupNode> EnumerateGroups(TabTreeNode root)
    {
        switch (root)
        {
            case TabGroupNode group:
                yield return group;
                break;
            case SplitNode split:
                foreach (var child in split.Children)
                {
                    foreach (var group in EnumerateGroups(child.Node))
                    {
                        yield return group;
                    }
                }

                break;
        }
    }

    /// <summary>Split nodes of the tree in depth-first, left-to-right order, root first.</summary>
    public static IEnumerable<SplitNode> EnumerateSplits(TabTreeNode root)
    {
        if (root is not SplitNode split)
        {
            yield break;
        }

        yield return split;
        foreach (var child in split.Children)
        {
            foreach (var nested in EnumerateSplits(child.Node))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Whether the tree contains at least one tab (the INV-D6 gate for document windows).</summary>
    public static bool HasTabs(TabTreeNode root) => EnumerateGroups(root).Any(g => !g.Tabs.IsEmpty);

    /// <summary>The group containing the given tab id, or null when the tree has no such tab.</summary>
    public static TabGroupNode? FindGroupContaining(TabTreeNode root, string tabId) =>
        EnumerateGroups(root).FirstOrDefault(g => g.Tabs.Contains(tabId, StringComparer.Ordinal));
}
