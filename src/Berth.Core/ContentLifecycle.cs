namespace Berth;

/// <summary>
/// Coordinator of the content lifecycle over the immutable layout (spec TW-9.2…TW-9.5, TW-9.11,
/// DA-9.3, DA-9.4; ADR-0003). Holds the only mutable content maps — tool window id and tab id to
/// live content; the layout state itself stays pure and serializable, independent of whether
/// content exists (spec TW-9.3). Creation is pull-based: <see cref="GetOrCreateToolWindowContent"/>,
/// <see cref="MaterializeTab"/>, and Eager creation inside <see cref="Register"/>. Release is
/// diff-based via <see cref="NotifyTransition"/>, or performed by the state-changing methods of
/// this class themselves. The body of a tool window is the tab of its tree whose id equals the
/// window's id (spec TW-9.5): body content lives in one map entry shared by
/// <see cref="GetOrCreateToolWindowContent"/> and <see cref="MaterializeTab"/>, so both paths
/// return the same object.
///
/// The transition contract: the application reports every layout transition — each core command
/// and every application of <see cref="LayoutApply.Apply"/> or
/// <see cref="LayoutApply.ResetToDefaults"/> — to <see cref="NotifyTransition"/>, one call per
/// operation. Batching several operations into one call is unsupported: a transient close of a
/// <see cref="ContentRetentionPolicy.DisposeOnClose"/> window would be missed (spec TW-9.2).
/// Transitions produced by this class itself — <see cref="Register"/>,
/// <see cref="RegisterDockContent"/>, <see cref="Unregister"/> and the refusal path of
/// <see cref="MaterializeTab"/> — maintain the maps internally and must not be reported again.
/// Single-threaded by design, like <see cref="ToolWindowRegistry"/>.
/// </summary>
public sealed class ContentLifecycle
{
    private readonly ToolWindowRegistry _registry;
    private readonly Dictionary<string, (IToolWindowContentFactory Factory, object Content)> _toolWindowContent =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ITabContentFactory Factory, object Content)> _tabContent =
        new(StringComparer.Ordinal);

    /// <summary>Creates a coordinator over the given registry.</summary>
    public ContentLifecycle(ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Returns the live body content of a tool window, creating it on first request
    /// (<see cref="ContentCreationPolicy.OnFirstOpen"/>, spec TW-9.2); content already created —
    /// eagerly, before a keep-alive close, or through <see cref="MaterializeTab"/> of the body
    /// tab (the shared entry of the TW-9.5 bridge) — is returned as is: a body living in a
    /// dock host yields the same object, its release then follows the tab rules rather than
    /// the retention policy (TW-9.2, DA-8.3). Null for a window registered without a
    /// <see cref="ToolWindowDescriptor.ContentFactory"/>.
    /// </summary>
    /// <param name="id">Id of a registered tool window.</param>
    /// <exception cref="ArgumentException">No tool window with the given id is registered.</exception>
    public object? GetOrCreateToolWindowContent(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_registry.TryGet(id, out var descriptor))
        {
            throw new ArgumentException($"No tool window '{id}' is registered.", nameof(id));
        }

        if (_toolWindowContent.TryGetValue(id, out var existing))
        {
            return existing.Content;
        }

        if (descriptor.ContentFactory is not { } factory)
        {
            return null;
        }

        var content = factory.CreateContent(id);
        _toolWindowContent[id] = (factory, content);
        return content;
    }

    /// <summary>
    /// Materializes a tab of any tree — a dock-area host or a panel tree (spec DA-9.3, DA-9.4):
    /// returns the live content, creating it via the owning claim on first request. A tab
    /// without a live claim sleeps — nothing is touched and the UI shows a placeholder
    /// (DA-9.4). The body tab of a tool window delegates to its body factory — the bridge of
    /// TW-9.5, sharing the entry with <see cref="GetOrCreateToolWindowContent"/> — and has no
    /// refusal path. A tab factory refusing with null closes the tab by a regular CloseTab —
    /// uniformly for restored and freshly opened tabs (DA-9.3) — and the caller must continue
    /// from <see cref="TabMaterialization.State"/>, re-reading paths and pending
    /// materializations (spec DA-1.3).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="tabId">Id of a tab present in the layout.</param>
    /// <exception cref="ArgumentException">No such tab exists in the layout.</exception>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public TabMaterialization MaterializeTab(LayoutState state, string tabId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (!TabTreeTraversal.LayoutContainsTab(state, tabId))
        {
            throw new ArgumentException($"No tab '{tabId}' exists in the layout.", nameof(tabId));
        }

        if (_tabContent.TryGetValue(tabId, out var existing))
        {
            return new TabMaterialization(TabMaterializationKind.Materialized, existing.Content, state);
        }

        var found = _registry.ResolveTabClaim(tabId, out var claim, out var conflict);
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }

        if (!found)
        {
            return new TabMaterialization(TabMaterializationKind.Sleeping, content: null, state);
        }

        if (claim.BodyFactory is { } bodyFactory)
        {
            if (_toolWindowContent.TryGetValue(tabId, out var body))
            {
                return new TabMaterialization(TabMaterializationKind.Materialized, body.Content, state);
            }

            var bodyContent = bodyFactory.CreateContent(tabId);
            _toolWindowContent[tabId] = (bodyFactory, bodyContent);
            return new TabMaterialization(TabMaterializationKind.Materialized, bodyContent, state);
        }

        if (claim.TabFactory!.CreateContent(tabId) is not { } content)
        {
            return new TabMaterialization(TabMaterializationKind.Refused, content: null, state.CloseTab(tabId));
        }

        _tabContent[tabId] = (claim.TabFactory, content);
        return new TabMaterialization(TabMaterializationKind.Materialized, content, state);
    }

    /// <summary>
    /// Reports one layout transition and releases what left it. Body content is released when
    /// its tab leaves the layout by any path — CloseTab in any tree, an Apply — under any
    /// retention policy («content without a tab» does not exist, spec TW-9.2), and under
    /// <see cref="ContentRetentionPolicy.DisposeOnClose"/> on the window's transition out of
    /// the open state — unless the body tab lives in a dock host, which shields it from the
    /// panel's openness (DA-8.3, DA-E37). Tab content is released when its id leaves the
    /// layout — a move between groups, hosts, windows or panels keeps the id present and never
    /// touches content (spec DA-5.4).
    /// </summary>
    /// <param name="before">The state the operation was applied to.</param>
    /// <param name="after">The state the operation produced.</param>
    public void NotifyTransition(LayoutState before, LayoutState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<string>? releasedBodies = null;
        foreach (var id in _toolWindowContent.Keys)
        {
            if (TabTreeTraversal.LayoutContainsTab(before, id)
                && !TabTreeTraversal.LayoutContainsTab(after, id))
            {
                (releasedBodies ??= []).Add(id);
                continue;
            }

            if (_registry.TryGet(id, out var descriptor)
                && descriptor.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose
                && IsOpen(before, id)
                && !IsOpen(after, id)
                && !LivesInDockHost(after, id))
            {
                (releasedBodies ??= []).Add(id);
            }
        }

        if (releasedBodies is not null)
        {
            foreach (var id in releasedBodies)
            {
                ReleaseToolWindowContent(id);
            }
        }

        List<string>? goneTabs = null;
        foreach (var id in _tabContent.Keys)
        {
            if (TabTreeTraversal.LayoutContainsTab(before, id)
                && !TabTreeTraversal.LayoutContainsTab(after, id))
            {
                (goneTabs ??= []).Add(id);
            }
        }

        if (goneTabs is not null)
        {
            foreach (var id in goneTabs)
            {
                ReleaseTabContent(id);
            }
        }
    }

    /// <summary>
    /// Registers a tool window in a live session (spec TW-10.3, E15): the descriptor enters the
    /// registry and the layout is reconciled atomically — a saved state, sleeping or live, wins
    /// over the descriptor; without one the window gets descriptor defaults, closed, ordered
    /// after the existing windows of its slot. Reconciliation also relocates tabs whose owner
    /// the new claims confirm as foreign out of panel trees into the main window (INV-D5,
    /// DA-9.2; no report channel — the deliberate asymmetry with Apply, TW-10.3) and seeds the
    /// body tab (TW-9.5). Sleeping tabs of the new owner are not touched: they materialize
    /// lazily as usual (spec DA-9.4). The call is transactional: Eager content is created
    /// before the registry mutation, so a throwing factory leaves the registry, the maps and
    /// the caller's state untouched — fix the factory and register again. Not a pure function:
    /// the registry and the content maps change alongside the returned state. Do not report
    /// this transition to <see cref="NotifyTransition"/>.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="descriptor">Descriptor to register.</param>
    /// <exception cref="ArgumentException">A descriptor with the same id is already registered.</exception>
    public LayoutState Register(LayoutState state, ToolWindowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_registry.TryGet(descriptor.Id, out _))
        {
            throw new ArgumentException($"Tool window '{descriptor.Id}' is already registered.", nameof(descriptor));
        }

        var result = state;
        if (!state.ToolWindows.Any(w => string.Equals(w.Id, descriptor.Id, StringComparison.Ordinal)))
        {
            var nextOrder = state.ToolWindows
                .Where(w => w.Slot == descriptor.DefaultSlot)
                .Select(w => w.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            result = state with
            {
                ToolWindows = state.ToolWindows.Add(
                    LayoutApply.FromDescriptor(descriptor, Math.Max(0, nextOrder))),
            };
        }

        (IToolWindowContentFactory Factory, object Content)? eager = null;
        if (descriptor.CreationPolicy == ContentCreationPolicy.Eager
            && descriptor.ContentFactory is { } factory)
        {
            eager = (factory, factory.CreateContent(descriptor.Id));
        }

        _registry.Register(descriptor);
        if (eager is { } entry)
        {
            _toolWindowContent[descriptor.Id] = entry;
        }

        result = LayoutApply.RelocateForeignPanelTabs(result, _registry, fixes: null);
        return LayoutApply.SeedBody(result, descriptor);
    }

    /// <summary>
    /// Registers dock-area content in a live session (spec TW-9.11, TW-10.3): the factory's
    /// claims become live and the layout is reconciled — a tab in a panel tree that the new
    /// claims confirm as a document relocates to the main window's current group (INV-D5,
    /// DA-9.2, DA-E35); registration has no report channel — the deliberate asymmetry with
    /// <see cref="LayoutApply.Apply"/>. Previously sleeping documents materialize lazily as
    /// usual (DA-9.4). Do not report this transition to <see cref="NotifyTransition"/>.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="factory">Dock-area content factory to register.</param>
    public LayoutState RegisterDockContent(LayoutState state, ITabContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(factory);
        _registry.RegisterDockContent(factory);
        return LayoutApply.RelocateForeignPanelTabs(state, _registry, fixes: null);
    }

    /// <summary>
    /// Unregisters a tool window in a live session (spec TW-9.4): the window is closed, its body
    /// content is released under any retention policy, its state stays in the layout as sleeping
    /// (TW-10.2) — including its content tree, which is inert and sleeps with the record — and
    /// the owner's tabs living in dock-area hosts (the claims of its
    /// <see cref="ToolWindowDescriptor.TabFactory"/> plus the body tab, TW-9.11) are closed with
    /// their content released (spec TW-9.10, DA-8.3), in traversal order (spec DA-9.2). The
    /// content of the tabs of the panel's own tree is released too, while the tabs themselves
    /// sleep in place (TW-9.4). Re-registration picks the sleeping state up but does not restore
    /// the closed dock tabs (TW-9.10). Do not report this transition to
    /// <see cref="NotifyTransition"/>.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="toolWindowId">Id of a registered tool window.</param>
    /// <exception cref="ArgumentException">No tool window with the given id is registered.</exception>
    public LayoutState Unregister(LayoutState state, string toolWindowId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolWindowId);
        if (!_registry.TryGet(toolWindowId, out var descriptor))
        {
            throw new ArgumentException($"No tool window '{toolWindowId}' is registered.", nameof(toolWindowId));
        }

        // The claims disappear with the registration — collect the owner's dock-hosted tabs first.
        List<string>? ownedTabs = null;
        foreach (var tab in EnumerateDockTabs(state.DockArea))
        {
            if (OwnsTab(descriptor, tab))
            {
                (ownedTabs ??= []).Add(tab);
            }
        }

        _registry.Unregister(toolWindowId);

        var result = state;
        var record = state.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, toolWindowId, StringComparison.Ordinal));
        if (record is not null)
        {
            result = result.Close(toolWindowId);
        }

        ReleaseToolWindowContent(toolWindowId);
        if (ownedTabs is not null)
        {
            foreach (var tab in ownedTabs)
            {
                result = result.CloseTab(tab);
                ReleaseTabContent(tab);
            }
        }

        if (record is not null)
        {
            // The own tree is inert and sleeps (TW-9.4); only the content of its tabs is released.
            foreach (var group in TabTreeTraversal.EnumerateGroups(record.ContentTree))
            {
                foreach (var tab in group.Tabs)
                {
                    ReleaseTabContent(tab);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Releases every live content object and clears the maps — the session teardown. The layout
    /// state is not touched: it is what gets serialized (spec TW-10.1).
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var (id, entry) in _toolWindowContent)
        {
            entry.Factory.ReleaseContent(id, entry.Content);
        }

        _toolWindowContent.Clear();
        foreach (var (id, entry) in _tabContent)
        {
            entry.Factory.ReleaseContent(id, entry.Content);
        }

        _tabContent.Clear();
    }

    private void ReleaseToolWindowContent(string id)
    {
        if (_toolWindowContent.Remove(id, out var entry))
        {
            entry.Factory.ReleaseContent(id, entry.Content);
        }
    }

    private void ReleaseTabContent(string id)
    {
        if (_tabContent.Remove(id, out var entry))
        {
            entry.Factory.ReleaseContent(id, entry.Content);
        }
    }

    /// <summary>The union of one registration's claims: the tab predicate plus the implicit body claim (spec TW-9.5, TW-9.11).</summary>
    private static bool OwnsTab(ToolWindowDescriptor descriptor, string tabId) =>
        (descriptor.TabFactory?.OwnsTab(tabId) == true)
        || (descriptor.ContentFactory is not null
            && string.Equals(descriptor.Id, tabId, StringComparison.Ordinal));

    private static bool IsOpen(LayoutState state, string id)
    {
        foreach (var window in state.ToolWindows)
        {
            if (string.Equals(window.Id, id, StringComparison.Ordinal))
            {
                return window.IsOpen;
            }
        }

        return false;
    }

    /// <summary>Whether the tab lives in a dock-area host — the shield of DA-8.3 against the panel's openness.</summary>
    private static bool LivesInDockHost(LayoutState state, string tabId)
    {
        if (TabTreeTraversal.FindGroupContaining(state.DockArea.Root, tabId) is not null)
        {
            return true;
        }

        foreach (var window in state.DockArea.Windows)
        {
            if (TabTreeTraversal.FindGroupContaining(window.Root, tabId) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tabs of every dock-area host in the traversal order of spec DA-9.2.</summary>
    private static IEnumerable<string> EnumerateDockTabs(DockAreaState area)
    {
        foreach (var group in TabTreeTraversal.EnumerateGroups(area.Root))
        {
            foreach (var tab in group.Tabs)
            {
                yield return tab;
            }
        }

        foreach (var window in area.Windows)
        {
            foreach (var group in TabTreeTraversal.EnumerateGroups(window.Root))
            {
                foreach (var tab in group.Tabs)
                {
                    yield return tab;
                }
            }
        }
    }
}
