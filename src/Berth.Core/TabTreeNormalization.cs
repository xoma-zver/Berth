using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Normalization of tab-group trees and the dock area to canonical form (spec DA-3.1, DA-3.2).
/// Runs after every core operation and on Apply; idempotent. Beyond the spec, normalization
/// preserves instances: a canonical node or state is returned as the same reference, so callers
/// can use reference equality as a «nothing changed» check.
/// </summary>
public static class TabTreeNormalization
{
    /// <summary>
    /// Numeric tolerance of the share sum of a split (INV-D3): a sum within the tolerance of 1
    /// is canonical, a larger deviation is renormalized by rule N4 (spec DA-3.1).
    /// </summary>
    public const double ShareSumTolerance = 1e-9;

    /// <summary>
    /// Normalizes a tree by rules N1–N5 (spec DA-3.1): empty non-root groups are removed (N1),
    /// degenerate splits are collapsed (N2), a child split of its parent's orientation is merged
    /// into the parent with share multiplication (N3), invalid shares are replaced with equal
    /// ones and every share vector is renormalized to sum 1 (N4), and an invalid active tab of a
    /// group is replaced by its first tab (N5). The root group survives even empty (spec DA-2.3).
    /// </summary>
    public static TabTreeNode Normalize(TabTreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return NormalizeNode(root) switch
        {
            SplitNode { Children.Length: 0 } => TabGroupNode.Empty,
            SplitNode { Children.Length: 1 } single => single.Children[0].Node,
            var node => node,
        };
    }

    /// <summary>
    /// Normalizes the dock area: the trees of all hosts by N1–N5, plus the zone level of spec
    /// DA-3.1 — document windows whose tree holds no tabs are removed (INV-D6) and a surviving
    /// active host keeps its identity under its new index, while an active host pointing at a
    /// removed or missing window falls back to the main window. Invalid current tabs are
    /// reassigned within their host (N5): a tab that exists but is not active in its group is
    /// replaced by that group's active tab; an unknown id falls back to the active tab of the
    /// first non-empty group in depth-first order (null for a tab-less main window tree).
    /// </summary>
    public static DockAreaState Normalize(DockAreaState area)
    {
        ArgumentNullException.ThrowIfNull(area);

        var mainRoot = Normalize(area.Root);
        var mainCurrent = NormalizeCurrentTab(mainRoot, area.CurrentTabId);

        var windows = ImmutableArray.CreateBuilder<DocumentWindowState>(area.Windows.Length);
        var windowsChanged = false;
        var activeIndex = area.ActiveDockHost.DocumentWindowIndex;
        int? remappedActiveIndex = null;
        for (var i = 0; i < area.Windows.Length; i++)
        {
            var window = area.Windows[i];
            var root = Normalize(window.Root);
            if (!TabTreeTraversal.HasTabs(root))
            {
                windowsChanged = true;
                continue;
            }

            // The tree holds a tab, so the current-tab fallback cannot come up empty.
            var current = NormalizeCurrentTab(root, window.CurrentTabId)!;
            if (!ReferenceEquals(root, window.Root)
                || !string.Equals(current, window.CurrentTabId, StringComparison.Ordinal))
            {
                window = window with { Root = root, CurrentTabId = current };
                windowsChanged = true;
            }

            if (activeIndex == i)
            {
                remappedActiveIndex = windows.Count;
            }

            windows.Add(window);
        }

        var activeHost = activeIndex is null
            ? DockHost.MainWindow
            : remappedActiveIndex is { } remapped
                ? DockHost.DocumentWindow(remapped)
                : DockHost.MainWindow;

        if (ReferenceEquals(mainRoot, area.Root)
            && string.Equals(mainCurrent, area.CurrentTabId, StringComparison.Ordinal)
            && !windowsChanged
            && activeHost == area.ActiveDockHost)
        {
            return area;
        }

        return area with
        {
            Root = mainRoot,
            CurrentTabId = mainCurrent,
            Windows = windowsChanged ? windows.ToImmutable() : area.Windows,
            ActiveDockHost = activeHost,
        };
    }

    /// <summary>
    /// Normalizes the whole layout: the dock area by <see cref="Normalize(DockAreaState)"/> plus
    /// every tool window's content tree by N1–N5 (spec TW-9.5, DA-3.1); an empty panel tree is
    /// legal and survives (DA-8.4, DA-2.3). Instance-preserving like the other overloads.
    /// </summary>
    internal static LayoutState Normalize(LayoutState state)
    {
        var area = Normalize(state.DockArea);
        var windows = state.ToolWindows;
        for (var i = 0; i < windows.Length; i++)
        {
            var normalized = Normalize(windows[i].ContentTree);
            if (!ReferenceEquals(normalized, windows[i].ContentTree))
            {
                windows = windows.SetItem(i, windows[i] with { ContentTree = normalized });
            }
        }

        if (ReferenceEquals(area, state.DockArea) && windows == state.ToolWindows)
        {
            return state;
        }

        return state with { DockArea = area, ToolWindows = windows };
    }

    private static TabTreeNode NormalizeNode(TabTreeNode node) => node switch
    {
        TabGroupNode group => NormalizeGroup(group),
        SplitNode split => NormalizeSplit(split),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, message: null),
    };

    /// <summary>N5, group part: the active tab belongs to the group; null only when the group is empty.</summary>
    private static TabGroupNode NormalizeGroup(TabGroupNode group)
    {
        if (group.Tabs.IsEmpty)
        {
            return group.ActiveTabId is null ? group : group with { ActiveTabId = null };
        }

        return group.ActiveTabId is { } active && group.Tabs.Contains(active, StringComparer.Ordinal)
            ? group
            : group with { ActiveTabId = group.Tabs[0] };
    }

    /// <summary>
    /// N1–N4 over one split: children are normalized bottom-up, degenerate and same-orientation
    /// children are flattened, then the share rule runs. A split left with fewer than two
    /// children is collapsed by its own parent (or by <see cref="Normalize(TabTreeNode)"/> at
    /// the root), so the invalid share vector of such a remnant never survives.
    /// </summary>
    private static SplitNode NormalizeSplit(SplitNode split)
    {
        var children = ImmutableArray.CreateBuilder<SplitChild>(split.Children.Length);
        foreach (var child in split.Children)
        {
            AddNormalized(children, split.Orientation, NormalizeNode(child.Node), child.Share);
        }

        if (children.Count >= 2)
        {
            ApplyShareRule(children);
        }

        return SameChildren(split.Children, children)
            ? split
            : split with { Children = children.ToImmutable() };
    }

    /// <summary>
    /// Appends a normalized child, flattening on the way: an empty group is dropped (N1), a
    /// childless split is dropped and a single-child split is replaced by its child under the
    /// same share (N2), and a split of the parent's orientation contributes its children with
    /// shares scaled by its own (N3, gᵢ → s·gᵢ).
    /// </summary>
    private static void AddNormalized(
        ImmutableArray<SplitChild>.Builder children, SplitOrientation orientation, TabTreeNode node, double share)
    {
        switch (node)
        {
            case TabGroupNode { Tabs.IsEmpty: true }:
                break;
            case SplitNode { Children.Length: 0 }:
                break;
            case SplitNode { Children.Length: 1 } single:
                AddNormalized(children, orientation, single.Children[0].Node, share);
                break;
            case SplitNode nested when nested.Orientation == orientation:
                foreach (var grandChild in nested.Children)
                {
                    children.Add(new SplitChild(grandChild.Node, share * grandChild.Share));
                }

                break;
            default:
                children.Add(new SplitChild(node, share));
                break;
        }
    }

    /// <summary>
    /// N4: invalid shares (NaN or outside (0..1)) become the equal share 1/n, then the vector is
    /// renormalized — only when the sum deviates from 1 beyond <see cref="ShareSumTolerance"/>,
    /// so canonical vectors pass untouched. If renormalization underflows a share out of the
    /// valid range (pathological doubles), the whole vector falls back to equal shares.
    /// </summary>
    private static void ApplyShareRule(ImmutableArray<SplitChild>.Builder children)
    {
        var equal = 1.0 / children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            if (!IsValidShare(children[i].Share))
            {
                children[i] = children[i] with { Share = equal };
            }
        }

        var sum = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            sum += children[i].Share;
        }

        if (Math.Abs(sum - 1) <= ShareSumTolerance)
        {
            return;
        }

        var stillValid = true;
        for (var i = 0; i < children.Count; i++)
        {
            var renormalized = children[i].Share / sum;
            children[i] = children[i] with { Share = renormalized };
            stillValid &= IsValidShare(renormalized);
        }

        if (!stillValid)
        {
            for (var i = 0; i < children.Count; i++)
            {
                children[i] = children[i] with { Share = equal };
            }
        }
    }

    private static bool IsValidShare(double share) => share > 0 && share < 1;

    private static bool SameChildren(
        ImmutableArray<SplitChild> original, ImmutableArray<SplitChild>.Builder normalized)
    {
        if (original.Length != normalized.Count)
        {
            return false;
        }

        for (var i = 0; i < original.Length; i++)
        {
            if (!ReferenceEquals(original[i].Node, normalized[i].Node)
                || !original[i].Share.Equals(normalized[i].Share))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>N5, host part — see <see cref="Normalize(DockAreaState)"/> for the rule.</summary>
    private static string? NormalizeCurrentTab(TabTreeNode root, string? currentTabId)
    {
        if (currentTabId is not null
            && TabTreeTraversal.FindGroupContaining(root, currentTabId) is { } group)
        {
            // The group's active tab is already normalized, so this both keeps a valid current
            // tab and replaces one that is not active in its group.
            return group.ActiveTabId;
        }

        return TabTreeTraversal.EnumerateGroups(root)
            .FirstOrDefault(g => !g.Tabs.IsEmpty)?
            .ActiveTabId;
    }
}
