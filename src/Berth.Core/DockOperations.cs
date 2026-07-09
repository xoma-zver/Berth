using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Core commands over the document area of <see cref="LayoutState"/> (spec document-area,
/// section 5). Every command is a pure transition ending in zone normalization (DA-3.2); the
/// same command backs menu items, shortcuts and completed drag gestures (ADR-0004). A command
/// over a tab id absent from the dock area throws — except <see cref="OpenDocument"/>, whose
/// purpose is introducing new ids. Tab owner validation (canHost, INV-D5) arrives with panel
/// content trees (backlog 1.8); persistence commands — backlog 1.6.
/// </summary>
public static class DockOperations
{
    /// <summary>
    /// Opens a document in the current group of the effective host (spec DA-5.1, DA-6.1): the
    /// tab is inserted after the group's active tab (into an empty group — as the only one) and
    /// activated per <see cref="ActivateTab"/>. The effective host is the main window while a
    /// tool window is active, otherwise the active dock host. A document already open anywhere
    /// in the dock area is only activated — never moved (DA-E33).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the document tab; a new id opens a new tab.</param>
    /// <exception cref="ArgumentException">The id is empty or whitespace.</exception>
    public static LayoutState OpenDocument(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var area = state.DockArea;
        if (FindTab(area, id) is not null)
        {
            return state.ActivateTab(id);
        }

        var host = state.ActiveToolWindowId is null ? area.ActiveDockHost : DockHost.MainWindow;
        var root = GetRoot(area, host);
        ImmutableArray<int> path;
        TabGroupNode group;
        if (GetCurrentTab(area, host) is { } current)
        {
            // INV-D4: the current tab of a host always exists in its tree.
            _ = TabTreeTraversal.TryFindGroupPath(root, current, out path, out var found);
            group = found!;
        }
        else
        {
            // INV-D4: a null current tab means the tree holds no tabs — the empty root group (DA-2.3).
            path = [];
            group = (TabGroupNode)root;
        }

        var insertAt = group.ActiveTabId is { } active ? IndexOfTab(group.Tabs, active) + 1 : 0;
        var opened = group with { Tabs = group.Tabs.Insert(insertAt, id), ActiveTabId = id };
        area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(root, path, opened));
        return ActivateIn(state, area, host, id);
    }

    /// <summary>
    /// Closes a tab in any dock-area host (spec DA-5.2). If the tab was active in its group, the
    /// previous neighbour becomes active (the closed first — the new first); an emptied group
    /// disappears by normalization. The host's current tab, if it was the closed one, follows
    /// rule DA-6.3: the surviving group's new active tab, else the active tab of the previous
    /// group in depth-first order of the pre-removal tree (else the next), else null for an
    /// emptied main window; an emptied document window disappears entirely (INV-D6).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab present in the dock area.</param>
    /// <exception cref="ArgumentException">No such tab exists in the dock area.</exception>
    public static LayoutState CloseTab(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var area = state.DockArea;
        var (host, path, group) = FindTab(area, id) ?? throw NotInDockArea(id);
        var root = GetRoot(area, host);
        var remaining = RemoveFromGroup(group, id);

        area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(root, path, remaining));
        if (string.Equals(GetCurrentTab(area, host), id, StringComparison.Ordinal))
        {
            area = WithCurrentTab(area, host, CurrentTabFallback(root, group, remaining));
        }

        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Activates a tab (spec DA-5.3): the tab becomes the active tab of its group, the current
    /// tab of its host, and the host becomes the active dock host; the active tool window is
    /// cleared (DA-6.2, TW-6.5). Tabs of panel trees arrive with backlog 1.8.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab present in the dock area.</param>
    /// <exception cref="ArgumentException">No such tab exists in the dock area.</exception>
    public static LayoutState ActivateTab(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var area = state.DockArea;
        var (host, path, group) = FindTab(area, id) ?? throw NotInDockArea(id);
        if (!string.Equals(group.ActiveTabId, id, StringComparison.Ordinal))
        {
            area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(GetRoot(area, host), path, group with { ActiveTabId = id }));
        }

        return ActivateIn(state, area, host, id);
    }

    /// <summary>
    /// Moves a tab into the target group at the given position — the position in the receiver's
    /// tab list after the moved tab is taken out, clamped into range (spec DA-5.4). Within one
    /// group this is a reorder. The moved tab becomes the active tab of the receiving group and
    /// the current tab of the receiving host; the donor group's active tab follows DA-5.2, and
    /// the donor host's current tab follows DA-6.3 — only when the move crosses hosts: within
    /// one host the tab does not leave it, and only the receiver rule applies.
    /// <see cref="DockAreaState.ActiveDockHost"/> and <see cref="LayoutState.ActiveToolWindowId"/>
    /// are not touched — activation is a follow-up <see cref="ActivateTab"/> by the UI.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the tab to move.</param>
    /// <param name="target">Group to move into (spec DA-1.3).</param>
    /// <param name="index">Position in the receiver after the moved tab is taken out; clamped.</param>
    /// <exception cref="ArgumentException">The tab or the target does not exist in the dock area.</exception>
    public static LayoutState MoveTab(this LayoutState state, string id, DockGroupRef target, int index)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var area = state.DockArea;
        var source = FindTab(area, id) ?? throw NotInDockArea(id);
        var destination = ResolveTarget(area, target);

        if (source.Host == destination.Host && ReferenceEquals(source.Group, destination.Group))
        {
            var tabs = source.Group.Tabs.RemoveAt(IndexOfTab(source.Group.Tabs, id));
            tabs = tabs.Insert(Math.Clamp(index, 0, tabs.Length), id);
            var reordered = source.Group with { Tabs = tabs, ActiveTabId = id };
            area = WithRoot(area, source.Host, TabTreeTraversal.ReplaceNode(GetRoot(area, source.Host), source.Path, reordered));
            area = WithCurrentTab(area, source.Host, id);
            return state with { DockArea = TabTreeNormalization.Normalize(area) };
        }

        var sourceRoot = GetRoot(area, source.Host);
        var remaining = RemoveFromGroup(source.Group, id);
        var received = destination.Group with
        {
            Tabs = destination.Group.Tabs.Insert(Math.Clamp(index, 0, destination.Group.Tabs.Length), id),
            ActiveTabId = id,
        };

        if (source.Host == destination.Host)
        {
            // Paths address distinct leaves of one tree, so two in-place replacements compose.
            var tree = TabTreeTraversal.ReplaceNode(sourceRoot, source.Path, remaining);
            area = WithRoot(area, source.Host, TabTreeTraversal.ReplaceNode(tree, destination.Path, received));
            area = WithCurrentTab(area, source.Host, id);
        }
        else
        {
            area = WithRoot(area, source.Host, TabTreeTraversal.ReplaceNode(sourceRoot, source.Path, remaining));
            area = WithRoot(area, destination.Host, TabTreeTraversal.ReplaceNode(GetRoot(area, destination.Host), destination.Path, received));
            area = WithCurrentTab(area, destination.Host, id);
            if (string.Equals(GetCurrentTab(area, source.Host), id, StringComparison.Ordinal))
            {
                area = WithCurrentTab(area, source.Host, CurrentTabFallback(sourceRoot, source.Group, remaining));
            }
        }

        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Splits by moving (spec DA-5.5, DA-2.4): a new group with the tab appears next to the
    /// tab's group in the given direction. Along the parent split's orientation the new group
    /// becomes a sibling (before the group for <see cref="SplitDirection.Left"/>/<see cref="SplitDirection.Up"/>,
    /// after for <see cref="SplitDirection.Right"/>/<see cref="SplitDirection.Down"/>); across
    /// the orientation — or at a root group — the group is replaced by a split of the two, the
    /// new group on the direction side. Both halves share the donor's share; a donor emptied by
    /// the move passes its place and its whole share to the new group, so splitting a
    /// single-tab group restores the exact structure and shares (DA-E1). The rest follows
    /// <see cref="MoveTab"/>.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the tab to split away.</param>
    /// <param name="direction">Direction of the split (spec DA-1.2).</param>
    /// <exception cref="ArgumentException">No such tab exists in the dock area.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The direction is not a defined value.</exception>
    public static LayoutState SplitTab(this LayoutState state, string id, SplitDirection direction)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var orientation = direction switch
        {
            SplitDirection.Left or SplitDirection.Right => SplitOrientation.Row,
            SplitDirection.Up or SplitDirection.Down => SplitOrientation.Column,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, message: null),
        };
        var insertBefore = direction is SplitDirection.Left or SplitDirection.Up;

        var area = state.DockArea;
        var (host, path, group) = FindTab(area, id) ?? throw NotInDockArea(id);
        var root = GetRoot(area, host);
        var remaining = RemoveFromGroup(group, id);
        var newGroup = new TabGroupNode { Tabs = [id], ActiveTabId = id };

        TabTreeNode newRoot;
        if (remaining.Tabs.IsEmpty)
        {
            // The emptied donor passes its place and full share to the new group (DA-E1).
            newRoot = TabTreeTraversal.ReplaceNode(root, path, newGroup);
        }
        else if (!path.IsEmpty
            && TabTreeTraversal.GetNode(root, path.RemoveAt(path.Length - 1)) is SplitNode parent
            && parent.Orientation == orientation)
        {
            // Along the parent orientation: n-ary insertion, only the donor share is halved (DA-E14).
            var parentPath = path.RemoveAt(path.Length - 1);
            var childIndex = path[^1];
            var half = parent.Children[childIndex].Share / 2;
            var children = parent.Children
                .SetItem(childIndex, new SplitChild(remaining, half))
                .Insert(insertBefore ? childIndex : childIndex + 1, new SplitChild(newGroup, half));
            newRoot = TabTreeTraversal.ReplaceNode(root, parentPath, parent with { Children = children });
        }
        else
        {
            // Across the orientation or at the root: the group becomes a split of the two (DA-E13).
            var pair = insertBefore
                ? ImmutableArray.Create(new SplitChild(newGroup, 0.5), new SplitChild(remaining, 0.5))
                : ImmutableArray.Create(new SplitChild(remaining, 0.5), new SplitChild(newGroup, 0.5));
            newRoot = TabTreeTraversal.ReplaceNode(root, path, new SplitNode { Orientation = orientation, Children = pair });
        }

        area = WithRoot(area, host, newRoot);
        // The receiver host is the donor host, so only the receiver rule of DA-5.4 applies.
        area = WithCurrentTab(area, host, id);
        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Sets the share vector of the split at the given path of the given host (spec DA-5.6,
    /// DA-1.3). The UI reduces splitter drags to this command, changing only a pair of adjacent
    /// children; throttling is allowed, but the path must be re-resolved after every operation.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="host">Host of the tree containing the split.</param>
    /// <param name="path">Child indices from the root to the split; empty addresses the root.</param>
    /// <param name="shares">New shares, one per child, each in (0..1), summing to 1 (INV-D3).</param>
    /// <exception cref="ArgumentException">The host or the path does not address a split, or the share count or sum is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A share is outside the open interval (0..1).</exception>
    public static LayoutState SetSplitShares(
        this LayoutState state, DockHost host, ImmutableArray<int> path, ImmutableArray<double> shares)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (path.IsDefault)
        {
            throw new ArgumentException("The path must be initialized.", nameof(path));
        }

        if (shares.IsDefault)
        {
            throw new ArgumentException("The shares must be initialized.", nameof(shares));
        }

        var area = state.DockArea;
        if (host.DocumentWindowIndex is { } windowIndex && windowIndex >= area.Windows.Length)
        {
            throw new ArgumentException($"No document window {windowIndex} exists in the dock area.", nameof(host));
        }

        var root = GetRoot(area, host);
        var node = root;
        foreach (var index in path)
        {
            if (node is not SplitNode split || index < 0 || index >= split.Children.Length)
            {
                throw new ArgumentException("The path does not address a node of the host tree.", nameof(path));
            }

            node = split.Children[index].Node;
        }

        if (node is not SplitNode target)
        {
            throw new ArgumentException("The path addresses a group, not a split.", nameof(path));
        }

        if (shares.Length != target.Children.Length)
        {
            throw new ArgumentException(
                $"Expected {target.Children.Length} shares for the split, got {shares.Length}.", nameof(shares));
        }

        var sum = 0.0;
        foreach (var share in shares)
        {
            if (!(share > 0 && share < 1))
            {
                throw new ArgumentOutOfRangeException(nameof(shares), share, "Each share must be in the open interval (0, 1).");
            }

            sum += share;
        }

        if (Math.Abs(sum - 1) > TabTreeNormalization.ShareSumTolerance)
        {
            throw new ArgumentException($"The shares must sum to 1 within the core tolerance; got {sum}.", nameof(shares));
        }

        var children = target.Children;
        for (var i = 0; i < shares.Length; i++)
        {
            children = children.SetItem(i, children[i] with { Share = shares[i] });
        }

        area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(root, path, target with { Children = children }));
        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Moves a tab into a new document window with the given bounds (spec DA-5.7): the window is
    /// appended to the list (creation order, DA-2.5), its tree is a single group, the tab is the
    /// window's current tab. The rest follows <see cref="MoveTab"/>, including the disappearance
    /// of emptied groups and windows; pixels come from the UI (ADR-0002).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the tab to move out.</param>
    /// <param name="bounds">Screen bounds of the new window.</param>
    /// <exception cref="ArgumentException">No such tab exists in the dock area.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="bounds"/> is not a finite number (TW-5.9).</exception>
    public static LayoutState MoveTabToNewWindow(this LayoutState state, string id, FloatingBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bounds.ThrowIfNotFinite(nameof(bounds));

        var area = state.DockArea;
        var (host, path, group) = FindTab(area, id) ?? throw NotInDockArea(id);
        var root = GetRoot(area, host);
        var remaining = RemoveFromGroup(group, id);

        area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(root, path, remaining));
        if (string.Equals(GetCurrentTab(area, host), id, StringComparison.Ordinal))
        {
            area = WithCurrentTab(area, host, CurrentTabFallback(root, group, remaining));
        }

        var window = new DocumentWindowState(bounds, new TabGroupNode { Tabs = [id], ActiveTabId = id }, id);
        area = area with { Windows = area.Windows.Add(window) };
        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Remembers the bounds of the document window containing the tab (spec DA-5.8). The UI
    /// calls this while the window is moved or resized; throttling is allowed.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab of the document window.</param>
    /// <param name="bounds">New screen bounds of the window.</param>
    /// <exception cref="ArgumentException">No such tab exists, or the tab lives in the main window.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="bounds"/> is not a finite number (TW-5.9).</exception>
    public static LayoutState SetDocumentWindowBounds(this LayoutState state, string id, FloatingBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bounds.ThrowIfNotFinite(nameof(bounds));

        var area = state.DockArea;
        var (host, _, _) = FindTab(area, id) ?? throw NotInDockArea(id);
        if (host.DocumentWindowIndex is not { } index)
        {
            throw new ArgumentException($"Tab '{id}' lives in the main window, which has no document window bounds.", nameof(id));
        }

        var window = area.Windows[index];
        if (window.Bounds == bounds)
        {
            return state;
        }

        area = area with { Windows = area.Windows.SetItem(index, window with { Bounds = bounds }) };
        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Flips the orientation of the split holding the tab's group, keeping the order and the
    /// shares of the children (spec DA-5.9); normalization then merges levels whose orientations
    /// coincide (N3) — see DA-E25/E26/E31/E32 for the merge-free and merging cases.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab whose group's parent split is rotated.</param>
    /// <exception cref="ArgumentException">No such tab exists, or the tab's group is the root of its tree.</exception>
    public static LayoutState RotateSplit(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var area = state.DockArea;
        var (host, path, _) = FindTab(area, id) ?? throw NotInDockArea(id);
        if (path.IsEmpty)
        {
            throw new ArgumentException($"The group of tab '{id}' is the root of its tree — there is no split to rotate.", nameof(id));
        }

        var root = GetRoot(area, host);
        var parentPath = path.RemoveAt(path.Length - 1);
        var parent = (SplitNode)TabTreeTraversal.GetNode(root, parentPath);
        var rotated = parent with
        {
            Orientation = parent.Orientation == SplitOrientation.Row ? SplitOrientation.Column : SplitOrientation.Row,
        };
        area = WithRoot(area, host, TabTreeTraversal.ReplaceNode(root, parentPath, rotated));
        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// Finishes an activation (spec DA-5.3): the tab becomes the current tab of the host, the
    /// host becomes the active dock host, the active tool window is cleared (DA-6.2), and the
    /// zone is normalized. Returns the original state when nothing changed.
    /// </summary>
    private static LayoutState ActivateIn(LayoutState state, DockAreaState area, DockHost host, string id)
    {
        area = WithCurrentTab(area, host, id);
        if (area.ActiveDockHost != host)
        {
            area = area with { ActiveDockHost = host };
        }

        area = TabTreeNormalization.Normalize(area);
        if (ReferenceEquals(area, state.DockArea) && state.ActiveToolWindowId is null)
        {
            return state;
        }

        return state with { DockArea = area, ActiveToolWindowId = null };
    }

    /// <summary>
    /// Removes a tab from a group with the active-tab rule of spec DA-5.2: the previous
    /// neighbour of a removed active tab becomes active, for the removed first — the new first.
    /// </summary>
    private static TabGroupNode RemoveFromGroup(TabGroupNode group, string id)
    {
        var index = IndexOfTab(group.Tabs, id);
        var tabs = group.Tabs.RemoveAt(index);
        if (!string.Equals(group.ActiveTabId, id, StringComparison.Ordinal))
        {
            return group with { Tabs = tabs };
        }

        return group with { Tabs = tabs, ActiveTabId = tabs.IsEmpty ? null : tabs[Math.Max(0, index - 1)] };
    }

    /// <summary>
    /// Rule DA-6.3 for a host whose current tab disappeared: the surviving donor group's new
    /// active tab; for a vanished group — the active tab of the previous group in depth-first
    /// order of the pre-removal tree, else the next; null when no group is left (the emptied
    /// main window; an emptied document window is removed by normalization instead).
    /// </summary>
    private static string? CurrentTabFallback(TabTreeNode preRemovalRoot, TabGroupNode donor, TabGroupNode remaining)
    {
        if (!remaining.Tabs.IsEmpty)
        {
            return remaining.ActiveTabId;
        }

        TabGroupNode? previous = null;
        TabGroupNode? next = null;
        var donorSeen = false;
        foreach (var group in TabTreeTraversal.EnumerateGroups(preRemovalRoot))
        {
            if (ReferenceEquals(group, donor))
            {
                donorSeen = true;
                continue;
            }

            if (donorSeen)
            {
                next = group;
                break;
            }

            previous = group;
        }

        return (previous ?? next)?.ActiveTabId;
    }

    private static (DockHost Host, ImmutableArray<int> Path, TabGroupNode Group) ResolveTarget(
        DockAreaState area, DockGroupRef target)
    {
        if (target.TabId is { } tabId)
        {
            return FindTab(area, tabId)
                ?? throw new ArgumentException($"No tab '{tabId}' exists in the dock area.", nameof(target));
        }

        var host = target.Host;
        if (host.DocumentWindowIndex is { } index && index >= area.Windows.Length)
        {
            throw new ArgumentException($"No document window {index} exists in the dock area.", nameof(target));
        }

        if (GetRoot(area, host) is not TabGroupNode rootGroup)
        {
            throw new ArgumentException("HostRoot addresses the root group, but the host root is a split.", nameof(target));
        }

        return (host, [], rootGroup);
    }

    private static (DockHost Host, ImmutableArray<int> Path, TabGroupNode Group)? FindTab(DockAreaState area, string id)
    {
        if (TabTreeTraversal.TryFindGroupPath(area.Root, id, out var path, out var group))
        {
            return (DockHost.MainWindow, path, group);
        }

        for (var i = 0; i < area.Windows.Length; i++)
        {
            if (TabTreeTraversal.TryFindGroupPath(area.Windows[i].Root, id, out path, out group))
            {
                return (DockHost.DocumentWindow(i), path, group);
            }
        }

        return null;
    }

    private static TabTreeNode GetRoot(DockAreaState area, DockHost host) =>
        host.DocumentWindowIndex is { } index ? area.Windows[index].Root : area.Root;

    private static string? GetCurrentTab(DockAreaState area, DockHost host) =>
        host.DocumentWindowIndex is { } index ? area.Windows[index].CurrentTabId : area.CurrentTabId;

    private static DockAreaState WithRoot(DockAreaState area, DockHost host, TabTreeNode root)
    {
        if (host.DocumentWindowIndex is not { } index)
        {
            return ReferenceEquals(area.Root, root) ? area : area with { Root = root };
        }

        var window = area.Windows[index];
        return ReferenceEquals(window.Root, root)
            ? area
            : area with { Windows = area.Windows.SetItem(index, window with { Root = root }) };
    }

    /// <summary>
    /// Sets the current tab of a host. Null for a document window is ignored: it can only mean
    /// the window's tree emptied, and zone normalization removes such a window entirely (INV-D6).
    /// </summary>
    private static DockAreaState WithCurrentTab(DockAreaState area, DockHost host, string? currentTabId)
    {
        if (host.DocumentWindowIndex is not { } index)
        {
            return string.Equals(area.CurrentTabId, currentTabId, StringComparison.Ordinal)
                ? area
                : area with { CurrentTabId = currentTabId };
        }

        if (currentTabId is null)
        {
            return area;
        }

        var window = area.Windows[index];
        return string.Equals(window.CurrentTabId, currentTabId, StringComparison.Ordinal)
            ? area
            : area with { Windows = area.Windows.SetItem(index, window with { CurrentTabId = currentTabId }) };
    }

    private static int IndexOfTab(ImmutableArray<string> tabs, string id)
    {
        for (var i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static ArgumentException NotInDockArea(string id) =>
        new($"No tab '{id}' exists in the dock area.", nameof(id));
}
