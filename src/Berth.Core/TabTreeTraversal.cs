using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Berth;

/// <summary>
/// Traversal helpers over tab-group trees, shared by normalization, invariant validation and
/// the dock-area operations. Depth-first, left-to-right — the traversal order of spec DA-6.3
/// and DA-9.2. Node addressing follows spec DA-1.3: a path is the sequence of child indices
/// from the root.
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

    /// <summary>
    /// Finds the group containing the given tab together with its path — the child indices from
    /// the root (spec DA-1.3); the path is empty when the root is that group.
    /// </summary>
    public static bool TryFindGroupPath(
        TabTreeNode root, string tabId, out ImmutableArray<int> path, [NotNullWhen(true)] out TabGroupNode? group)
    {
        var builder = ImmutableArray.CreateBuilder<int>();
        if (TryFindGroupPath(root, tabId, builder, out group))
        {
            path = builder.ToImmutable();
            return true;
        }

        path = [];
        return false;
    }

    private static bool TryFindGroupPath(
        TabTreeNode node, string tabId, ImmutableArray<int>.Builder path, [NotNullWhen(true)] out TabGroupNode? group)
    {
        switch (node)
        {
            case TabGroupNode candidate when candidate.Tabs.Contains(tabId, StringComparer.Ordinal):
                group = candidate;
                return true;
            case SplitNode split:
                for (var i = 0; i < split.Children.Length; i++)
                {
                    path.Add(i);
                    if (TryFindGroupPath(split.Children[i].Node, tabId, path, out group))
                    {
                        return true;
                    }

                    path.RemoveAt(path.Count - 1);
                }

                break;
        }

        group = null;
        return false;
    }

    /// <summary>The node at the given path; the path must be valid for the tree.</summary>
    public static TabTreeNode GetNode(TabTreeNode root, ImmutableArray<int> path)
    {
        var node = root;
        foreach (var index in path)
        {
            node = ((SplitNode)node).Children[index].Node;
        }

        return node;
    }

    /// <summary>
    /// Functional replacement of the node at the given path: ancestors are recreated, the shares
    /// and the siblings stay untouched. An empty path replaces the root.
    /// </summary>
    public static TabTreeNode ReplaceNode(TabTreeNode root, ImmutableArray<int> path, TabTreeNode replacement) =>
        ReplaceAt(root, path, depth: 0, replacement);

    private static TabTreeNode ReplaceAt(TabTreeNode node, ImmutableArray<int> path, int depth, TabTreeNode replacement)
    {
        if (depth == path.Length)
        {
            return replacement;
        }

        var split = (SplitNode)node;
        var index = path[depth];
        var child = split.Children[index];
        return split with
        {
            Children = split.Children.SetItem(index, child with { Node = ReplaceAt(child.Node, path, depth + 1, replacement) }),
        };
    }
}
