using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Core commands over the tab trees of <see cref="LayoutState"/> — the dock-area hosts and the
/// content trees of tool windows (spec document-area, section 5; tool-windows, section 9).
/// Every command is a pure transition ending in normalization (DA-3.2); the same command backs
/// menu items, shortcuts and completed drag gestures (ADR-0004). A command over a tab id absent
/// from the layout throws — except <see cref="OpenDocument"/> and <see cref="OpenPanelTab"/>,
/// whose purpose is introducing new ids. Commands that confirm tab owners — opening and moves
/// into a panel — take the <see cref="ToolWindowRegistry"/> (canHost, INV-D5, TW-9.11).
/// </summary>
public static class DockOperations
{
    /// <summary>
    /// Opens a document in the current group of the effective host (spec DA-5.1, DA-6.1): the
    /// tab is inserted after the group's active tab (into an empty group — as the only one) and
    /// activated per <see cref="ActivateTab"/>. The effective host is the main window while a
    /// tool window is active, otherwise the active dock host. An id already present anywhere in
    /// the layout — including a sleeping tab in a panel tree — is only activated, never moved
    /// (DA-E33, INV-D2). An id with a confirmed tool window owner is an error: panel tabs are
    /// opened by <see cref="OpenPanelTab"/> (DA-5.1, TW-9.12).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the document tab; a new id opens a new tab.</param>
    /// <param name="registry">Registry confirming tab owners (spec TW-9.11).</param>
    /// <exception cref="ArgumentException">The id is empty, or its confirmed owner is a tool window.</exception>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public static LayoutState OpenDocument(this LayoutState state, string id, ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(registry);
        if (ResolveOwner(registry, id) is { ToolWindowId: { } ownerPanel })
        {
            throw new ArgumentException(
                $"Tab '{id}' is owned by tool window '{ownerPanel}'; panel tabs are opened by OpenPanelTab (DA-5.1).",
                nameof(id));
        }

        if (FindTab(state, id) is not null)
        {
            return state.ActivateTab(id);
        }

        var host = TreeHost.OfDock(
            state.ActiveToolWindowId is null ? state.DockArea.ActiveDockHost : DockHost.MainWindow);
        var root = GetRoot(state, host);
        ImmutableArray<int> path;
        TabGroupNode group;
        if (GetCurrentTab(state, host) is { } current)
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
        state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, path, opened));
        return ActivateInDock(state, host.Dock, id);
    }

    /// <summary>
    /// Opens a panel tab in the tree of its owner (spec TW-9.12). The owner must be confirmed
    /// by a live claim and be a tool window; a sleeping id and a document are caller errors. An
    /// id already present anywhere in the layout only becomes the active tab of its group —
    /// with no other effects (INV-D2). Otherwise the tab is inserted into the first non-empty
    /// group of the owner's tree in depth-first order, after its active tab (into an empty
    /// tree — as the root group), and becomes the group's active tab. The panel's openness,
    /// <see cref="LayoutState.ActiveToolWindowId"/> and the dock-area hosts are not touched
    /// (TW-9.3) — activation is a follow-up <see cref="ActivateTab"/> by the UI.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the panel tab; a new id opens a new tab.</param>
    /// <param name="registry">Registry confirming tab owners (spec TW-9.11).</param>
    /// <exception cref="ArgumentException">The id is empty, unclaimed, owned by the dock area, or its owner has no state in the layout.</exception>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public static LayoutState OpenPanelTab(this LayoutState state, string id, ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(registry);
        var owner = ResolveOwner(registry, id);
        if (owner is null)
        {
            throw new ArgumentException(
                $"Tab '{id}' has no live ownership claim; a tab of a sleeping owner cannot be opened into a panel (TW-9.12).",
                nameof(id));
        }

        if (owner.Value.ToolWindowId is not { } panelId)
        {
            throw new ArgumentException(
                $"Tab '{id}' is a document; documents are opened by OpenDocument (DA-5.1).", nameof(id));
        }

        if (FindTab(state, id) is { } present)
        {
            if (string.Equals(present.Group.ActiveTabId, id, StringComparison.Ordinal))
            {
                return state;
            }

            var activated = present.Group with { ActiveTabId = id };
            return TabTreeNormalization.Normalize(WithRoot(
                state, present.Host,
                TabTreeTraversal.ReplaceNode(GetRoot(state, present.Host), present.Path, activated)));
        }

        var panel = FindPanel(state, panelId)
            ?? throw new ArgumentException(
                $"Tool window '{panelId}' owning tab '{id}' has no state in the layout.", nameof(id));
        var host = TreeHost.Panel(panelId);
        var root = panel.ContentTree;
        if (TabTreeTraversal.EnumerateGroups(root).FirstOrDefault(g => !g.Tabs.IsEmpty) is not { } firstGroup)
        {
            // A tree without tabs is the empty root group in canonical form (DA-2.3).
            return TabTreeNormalization.Normalize(
                WithRoot(state, host, new TabGroupNode { Tabs = [id], ActiveTabId = id }));
        }

        _ = TabTreeTraversal.TryFindGroupPath(root, firstGroup.Tabs[0], out var path, out var group);
        var insertAt = group!.ActiveTabId is { } active ? IndexOfTab(group.Tabs, active) + 1 : group.Tabs.Length;
        var opened = group with { Tabs = group.Tabs.Insert(insertAt, id), ActiveTabId = id };
        return TabTreeNormalization.Normalize(
            WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, path, opened)));
    }

    /// <summary>
    /// Closes a tab in any host — a dock-area host or a panel tree, the owner does not
    /// participate (spec DA-5.2). If the tab was active in its group, the previous neighbour
    /// becomes active (the closed first — the new first); an emptied group disappears by
    /// normalization. For a dock host whose current tab was the closed one, rule DA-6.3
    /// applies: the surviving group's new active tab, else the active tab of the previous group
    /// in depth-first order of the pre-removal tree (else the next), else null for an emptied
    /// main window; an emptied document window disappears entirely (INV-D6). Panels have no
    /// host current tab — only the group rule applies there.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab present in the layout.</param>
    /// <exception cref="ArgumentException">No such tab exists in the layout.</exception>
    public static LayoutState CloseTab(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var (host, path, group) = FindTab(state, id) ?? throw NotInLayout(id);
        var root = GetRoot(state, host);
        var remaining = RemoveFromGroup(group, id);

        state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, path, remaining));
        if (!host.IsPanel && string.Equals(GetCurrentTab(state, host), id, StringComparison.Ordinal))
        {
            state = WithCurrentTab(state, host, CurrentTabFallback(root, group, remaining));
        }

        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// Activates a tab (spec DA-5.3): the tab becomes the active tab of its group. A tab in a
    /// dock-area host additionally becomes the host's current tab, the host becomes the active
    /// dock host, and the active tool window is cleared (DA-6.2, TW-6.5). A tab in a panel tree
    /// activates the panel (<see cref="LayoutState.ActiveToolWindowId"/> = the owner) without
    /// touching the dock-area hosts (DA-6.1, DA-E19); a closed panel is opened per TW-5.1 —
    /// with layer eviction and rule R1 — because the active panel is always open (INV-5, DA-E39).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab present in the layout.</param>
    /// <exception cref="ArgumentException">No such tab exists in the layout.</exception>
    public static LayoutState ActivateTab(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var (host, path, group) = FindTab(state, id) ?? throw NotInLayout(id);
        if (!string.Equals(group.ActiveTabId, id, StringComparison.Ordinal))
        {
            state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(
                GetRoot(state, host), path, group with { ActiveTabId = id }));
        }

        if (host.PanelId is { } panelId)
        {
            return TabTreeNormalization.Normalize(state).Open(panelId);
        }

        return ActivateInDock(state, host.Dock, id);
    }

    /// <summary>
    /// Moves a tab into the target group at the given position — the position in the receiver's
    /// tab list after the moved tab is taken out, clamped into range (spec DA-5.4). Within one
    /// group this is a reorder. Moving into a panel from another host requires the registry to
    /// confirm that panel as the tab's owner (canHost, INV-D5, DA-8.2) — a sleeping tab cannot
    /// move into a panel; moves within one host and into dock-area hosts need no check. The
    /// moved tab becomes the active tab of the receiving group and — for a dock-area receiver —
    /// the current tab of the receiving host (panels have no host current tab, DA-6.1); the
    /// donor group's active tab follows DA-5.2, and a dock donor host's current tab follows
    /// DA-6.3 — only when the move crosses hosts. <see cref="DockAreaState.ActiveDockHost"/>
    /// and <see cref="LayoutState.ActiveToolWindowId"/> are not touched — activation is a
    /// follow-up <see cref="ActivateTab"/> by the UI.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the tab to move.</param>
    /// <param name="target">Group to move into (spec DA-1.3).</param>
    /// <param name="index">Position in the receiver after the moved tab is taken out; clamped.</param>
    /// <param name="registry">Registry confirming tab owners for moves into panels (spec TW-9.11).</param>
    /// <exception cref="ArgumentException">The tab or the target does not exist, or the move into a panel is not confirmed by ownership.</exception>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public static LayoutState MoveTab(
        this LayoutState state, string id, DockGroupRef target, int index, ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(registry);

        var source = FindTab(state, id) ?? throw NotInLayout(id);
        var destination = ResolveTarget(state, target);
        if (destination.Host.PanelId is { } receiverPanel && source.Host != destination.Host)
        {
            RequireOwnedBy(registry, id, receiverPanel);
        }

        if (source.Host == destination.Host && ReferenceEquals(source.Group, destination.Group))
        {
            var tabs = source.Group.Tabs.RemoveAt(IndexOfTab(source.Group.Tabs, id));
            tabs = tabs.Insert(Math.Clamp(index, 0, tabs.Length), id);
            var reordered = source.Group with { Tabs = tabs, ActiveTabId = id };
            state = WithRoot(state, source.Host, TabTreeTraversal.ReplaceNode(
                GetRoot(state, source.Host), source.Path, reordered));
            state = WithCurrentTab(state, source.Host, id);
            return TabTreeNormalization.Normalize(state);
        }

        var sourceRoot = GetRoot(state, source.Host);
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
            state = WithRoot(state, source.Host, TabTreeTraversal.ReplaceNode(tree, destination.Path, received));
            state = WithCurrentTab(state, source.Host, id);
        }
        else
        {
            state = WithRoot(state, source.Host, TabTreeTraversal.ReplaceNode(sourceRoot, source.Path, remaining));
            state = WithRoot(state, destination.Host, TabTreeTraversal.ReplaceNode(
                GetRoot(state, destination.Host), destination.Path, received));
            state = WithCurrentTab(state, destination.Host, id);
            if (!source.Host.IsPanel
                && string.Equals(GetCurrentTab(state, source.Host), id, StringComparison.Ordinal))
            {
                state = WithCurrentTab(state, source.Host, CurrentTabFallback(sourceRoot, source.Group, remaining));
            }
        }

        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// Splits by moving (spec DA-5.5, DA-2.4): a new group with the tab appears next to the
    /// tab's group in the given direction — in any host, since the tab never leaves its tree
    /// (canHost holds by construction). Along the parent split's orientation the new group
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
    /// <exception cref="ArgumentException">No such tab exists in the layout.</exception>
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

        var (host, path, group) = FindTab(state, id) ?? throw NotInLayout(id);
        var root = GetRoot(state, host);
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

        state = WithRoot(state, host, newRoot);
        // The receiver host is the donor host, so only the receiver rule of DA-5.4 applies.
        state = WithCurrentTab(state, host, id);
        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// Sets the share vector of the split at the given path of the given dock-area host (spec
    /// DA-5.6, DA-1.3). The UI reduces splitter drags to this command, changing only a pair of
    /// adjacent children; throttling is allowed, but the path must be re-resolved after every
    /// operation.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="host">Dock-area host of the tree containing the split.</param>
    /// <param name="path">Child indices from the root to the split; empty addresses the root.</param>
    /// <param name="shares">New shares, one per child, each in (0..1), summing to 1 (INV-D3).</param>
    /// <exception cref="ArgumentException">The host or the path does not address a split, or the share count or sum is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A share is outside the open interval (0..1).</exception>
    public static LayoutState SetSplitShares(
        this LayoutState state, DockHost host, ImmutableArray<int> path, ImmutableArray<double> shares)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (host.DocumentWindowIndex is { } windowIndex && windowIndex >= state.DockArea.Windows.Length)
        {
            throw new ArgumentException($"No document window {windowIndex} exists in the dock area.", nameof(host));
        }

        return SetSplitSharesCore(state, TreeHost.OfDock(host), path, shares);
    }

    /// <summary>
    /// Sets the share vector of the split at the given path of a tool window's content tree
    /// (spec DA-5.6, DA-1.3, TW-9.5) — the panel counterpart of the dock-area overload.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="toolWindowId">Id of the tool window hosting the tree.</param>
    /// <param name="path">Child indices from the root to the split; empty addresses the root.</param>
    /// <param name="shares">New shares, one per child, each in (0..1), summing to 1 (INV-D3).</param>
    /// <exception cref="ArgumentException">The tool window or the path does not address a split, or the share count or sum is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A share is outside the open interval (0..1).</exception>
    public static LayoutState SetSplitShares(
        this LayoutState state, string toolWindowId, ImmutableArray<int> path, ImmutableArray<double> shares)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolWindowId);
        if (FindPanel(state, toolWindowId) is null)
        {
            throw new ArgumentException($"No tool window '{toolWindowId}' exists in the layout.", nameof(toolWindowId));
        }

        return SetSplitSharesCore(state, TreeHost.Panel(toolWindowId), path, shares);
    }

    private static LayoutState SetSplitSharesCore(
        LayoutState state, TreeHost host, ImmutableArray<int> path, ImmutableArray<double> shares)
    {
        if (path.IsDefault)
        {
            throw new ArgumentException("The path must be initialized.", nameof(path));
        }

        if (shares.IsDefault)
        {
            throw new ArgumentException("The shares must be initialized.", nameof(shares));
        }

        var root = GetRoot(state, host);
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

        state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, path, target with { Children = children }));
        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// Moves a tab into a new document window with the given bounds (spec DA-5.7): the window is
    /// appended to the list (creation order, DA-2.5), its tree is a single group, the tab is the
    /// window's current tab. Dock-area hosts accept every tab (INV-D5) — documents, panel tabs
    /// (TW-9.8) and sleeping tabs alike, so no ownership check is needed. The rest follows
    /// <see cref="MoveTab"/>, including the disappearance of emptied groups and windows; pixels
    /// come from the UI (ADR-0002).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of the tab to move out.</param>
    /// <param name="bounds">Screen bounds of the new window.</param>
    /// <exception cref="ArgumentException">No such tab exists in the layout.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="bounds"/> is not a finite number (TW-5.9).</exception>
    public static LayoutState MoveTabToNewWindow(this LayoutState state, string id, FloatingBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bounds.ThrowIfNotFinite(nameof(bounds));

        var (host, path, group) = FindTab(state, id) ?? throw NotInLayout(id);
        var root = GetRoot(state, host);
        var remaining = RemoveFromGroup(group, id);

        state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, path, remaining));
        if (!host.IsPanel && string.Equals(GetCurrentTab(state, host), id, StringComparison.Ordinal))
        {
            state = WithCurrentTab(state, host, CurrentTabFallback(root, group, remaining));
        }

        var window = new DocumentWindowState(bounds, new TabGroupNode { Tabs = [id], ActiveTabId = id }, id);
        state = state with { DockArea = state.DockArea with { Windows = state.DockArea.Windows.Add(window) } };
        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// Remembers the bounds of the document window containing the tab (spec DA-5.8). The UI
    /// calls this while the window is moved or resized; throttling is allowed.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab of the document window.</param>
    /// <param name="bounds">New screen bounds of the window.</param>
    /// <exception cref="ArgumentException">No such tab exists, or the tab does not live in a document window.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="bounds"/> is not a finite number (TW-5.9).</exception>
    public static LayoutState SetDocumentWindowBounds(this LayoutState state, string id, FloatingBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bounds.ThrowIfNotFinite(nameof(bounds));

        var (host, _, _) = FindTab(state, id) ?? throw NotInLayout(id);
        if (host.IsPanel || host.Dock.DocumentWindowIndex is not { } index)
        {
            throw new ArgumentException(
                $"Tab '{id}' does not live in a document window, so there are no window bounds to set.", nameof(id));
        }

        var area = state.DockArea;
        var window = area.Windows[index];
        if (window.Bounds == bounds)
        {
            return state;
        }

        area = area with { Windows = area.Windows.SetItem(index, window with { Bounds = bounds }) };
        return TabTreeNormalization.Normalize(state with { DockArea = area });
    }

    /// <summary>
    /// Flips the orientation of the split holding the tab's group — in any host — keeping the
    /// order and the shares of the children (spec DA-5.9); normalization then merges levels
    /// whose orientations coincide (N3) — see DA-E25/E26/E31/E32 for the merge-free and merging
    /// cases.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tab whose group's parent split is rotated.</param>
    /// <exception cref="ArgumentException">No such tab exists, or the tab's group is the root of its tree.</exception>
    public static LayoutState RotateSplit(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var (host, path, _) = FindTab(state, id) ?? throw NotInLayout(id);
        if (path.IsEmpty)
        {
            throw new ArgumentException($"The group of tab '{id}' is the root of its tree — there is no split to rotate.", nameof(id));
        }

        var root = GetRoot(state, host);
        var parentPath = path.RemoveAt(path.Length - 1);
        var parent = (SplitNode)TabTreeTraversal.GetNode(root, parentPath);
        var rotated = parent with
        {
            Orientation = parent.Orientation == SplitOrientation.Row ? SplitOrientation.Column : SplitOrientation.Row,
        };
        state = WithRoot(state, host, TabTreeTraversal.ReplaceNode(root, parentPath, rotated));
        return TabTreeNormalization.Normalize(state);
    }

    // ---- ownership ----

    /// <summary>Resolves the owner of a tab id; a claim conflict throws per TW-9.11 (operations are resolve sites).</summary>
    private static TabOwner? ResolveOwner(ToolWindowRegistry registry, string tabId)
    {
        var found = registry.ResolveTabClaim(tabId, out var claim, out var conflict);
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }

        return found ? claim.Owner : null;
    }

    /// <summary>canHost for moves into a panel (INV-D5, DA-8.2): the confirmed owner must be that panel.</summary>
    private static void RequireOwnedBy(ToolWindowRegistry registry, string tabId, string panelId)
    {
        var owner = ResolveOwner(registry, tabId);
        if (owner != TabOwner.ToolWindow(panelId))
        {
            var actual = owner is null
                ? "not confirmed by any live claim"
                : owner.Value.IsDockArea ? "the dock area" : $"tool window '{owner.Value.ToolWindowId}'";
            throw new ArgumentException(
                $"Tab '{tabId}' cannot move into tool window '{panelId}': its owner is {actual} (canHost, INV-D5).",
                nameof(tabId));
        }
    }

    // ---- activation ----

    /// <summary>
    /// Finishes a dock-area activation (spec DA-5.3): the tab becomes the current tab of the
    /// host, the host becomes the active dock host, the active tool window is cleared (DA-6.2),
    /// and the layout is normalized. Returns the original state when nothing changed.
    /// </summary>
    private static LayoutState ActivateInDock(LayoutState state, DockHost host, string id)
    {
        var withCurrent = WithCurrentTab(state, TreeHost.OfDock(host), id);
        var area = withCurrent.DockArea;
        if (area.ActiveDockHost != host)
        {
            area = area with { ActiveDockHost = host };
        }

        var normalized = TabTreeNormalization.Normalize(
            ReferenceEquals(area, state.DockArea) ? state : withCurrent with { DockArea = area });
        if (ReferenceEquals(normalized, state) && state.ActiveToolWindowId is null)
        {
            return state;
        }

        return normalized with { ActiveToolWindowId = null };
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
    /// Rule DA-6.3 for a dock host whose current tab disappeared: the surviving donor group's
    /// new active tab; for a vanished group — the active tab of the previous group in
    /// depth-first order of the pre-removal tree, else the next; null when no group is left
    /// (the emptied main window; an emptied document window is removed by normalization instead).
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

    // ---- addressing ----

    private static (TreeHost Host, ImmutableArray<int> Path, TabGroupNode Group) ResolveTarget(
        LayoutState state, DockGroupRef target)
    {
        if (target.TabId is { } tabId)
        {
            return FindTab(state, tabId)
                ?? throw new ArgumentException($"No tab '{tabId}' exists in the layout.", nameof(target));
        }

        if (target.PanelId is { } panelId)
        {
            var panel = FindPanel(state, panelId)
                ?? throw new ArgumentException($"No tool window '{panelId}' exists in the layout.", nameof(target));
            if (panel.ContentTree is not TabGroupNode panelRoot)
            {
                throw new ArgumentException("PanelRoot addresses the root group, but the panel root is a split.", nameof(target));
            }

            return (TreeHost.Panel(panelId), [], panelRoot);
        }

        var host = target.Host;
        if (host.DocumentWindowIndex is { } index && index >= state.DockArea.Windows.Length)
        {
            throw new ArgumentException($"No document window {index} exists in the dock area.", nameof(target));
        }

        if (GetRoot(state, TreeHost.OfDock(host)) is not TabGroupNode rootGroup)
        {
            throw new ArgumentException("HostRoot addresses the root group, but the host root is a split.", nameof(target));
        }

        return (TreeHost.OfDock(host), [], rootGroup);
    }

    /// <summary>Finds a tab across every tree: the dock-area hosts first, then panel trees in state order.</summary>
    private static (TreeHost Host, ImmutableArray<int> Path, TabGroupNode Group)? FindTab(LayoutState state, string id)
    {
        var area = state.DockArea;
        if (TabTreeTraversal.TryFindGroupPath(area.Root, id, out var path, out var group))
        {
            return (TreeHost.OfDock(DockHost.MainWindow), path, group);
        }

        for (var i = 0; i < area.Windows.Length; i++)
        {
            if (TabTreeTraversal.TryFindGroupPath(area.Windows[i].Root, id, out path, out group))
            {
                return (TreeHost.OfDock(DockHost.DocumentWindow(i)), path, group);
            }
        }

        foreach (var window in state.ToolWindows)
        {
            if (TabTreeTraversal.TryFindGroupPath(window.ContentTree, id, out path, out group))
            {
                return (TreeHost.Panel(window.Id), path, group);
            }
        }

        return null;
    }

    private static ToolWindowState? FindPanel(LayoutState state, string id) =>
        state.ToolWindows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabTreeNode GetRoot(LayoutState state, TreeHost host)
    {
        if (host.PanelId is { } panelId)
        {
            return FindPanel(state, panelId)!.ContentTree;
        }

        return host.Dock.DocumentWindowIndex is { } index
            ? state.DockArea.Windows[index].Root
            : state.DockArea.Root;
    }

    private static LayoutState WithRoot(LayoutState state, TreeHost host, TabTreeNode root)
    {
        if (host.PanelId is { } panelId)
        {
            var windows = state.ToolWindows;
            for (var i = 0; i < windows.Length; i++)
            {
                if (string.Equals(windows[i].Id, panelId, StringComparison.Ordinal))
                {
                    return ReferenceEquals(windows[i].ContentTree, root)
                        ? state
                        : state with { ToolWindows = windows.SetItem(i, windows[i] with { ContentTree = root }) };
                }
            }

            // Hosts are resolved from the same state, so the panel record always exists.
            throw new InvalidOperationException($"Tool window '{panelId}' has no state in the layout.");
        }

        var area = state.DockArea;
        if (host.Dock.DocumentWindowIndex is not { } index)
        {
            return ReferenceEquals(area.Root, root)
                ? state
                : state with { DockArea = area with { Root = root } };
        }

        var window = area.Windows[index];
        return ReferenceEquals(window.Root, root)
            ? state
            : state with
            {
                DockArea = area with { Windows = area.Windows.SetItem(index, window with { Root = root }) },
            };
    }

    /// <summary>The current tab of a dock host; panels have no host current tab (DA-6.1) — null.</summary>
    private static string? GetCurrentTab(LayoutState state, TreeHost host)
    {
        if (host.IsPanel)
        {
            return null;
        }

        return host.Dock.DocumentWindowIndex is { } index
            ? state.DockArea.Windows[index].CurrentTabId
            : state.DockArea.CurrentTabId;
    }

    /// <summary>
    /// Sets the current tab of a dock host; a no-op for panels (no host current tab, DA-6.1).
    /// Null for a document window is ignored: it can only mean the window's tree emptied, and
    /// normalization removes such a window entirely (INV-D6).
    /// </summary>
    private static LayoutState WithCurrentTab(LayoutState state, TreeHost host, string? currentTabId)
    {
        if (host.IsPanel)
        {
            return state;
        }

        var area = state.DockArea;
        if (host.Dock.DocumentWindowIndex is not { } index)
        {
            return string.Equals(area.CurrentTabId, currentTabId, StringComparison.Ordinal)
                ? state
                : state with { DockArea = area with { CurrentTabId = currentTabId } };
        }

        if (currentTabId is null)
        {
            return state;
        }

        var window = area.Windows[index];
        return string.Equals(window.CurrentTabId, currentTabId, StringComparison.Ordinal)
            ? state
            : state with
            {
                DockArea = area with { Windows = area.Windows.SetItem(index, window with { CurrentTabId = currentTabId }) },
            };
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

    private static ArgumentException NotInLayout(string id) =>
        new($"No tab '{id}' exists in the layout.", nameof(id));
}
