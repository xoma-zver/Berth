namespace Berth;

/// <summary>
/// Coordinator of the content lifecycle over the immutable layout (spec TW-9.2…TW-9.4, TW-9.11,
/// DA-9.3, DA-9.4; ADR-0003). Holds the only mutable content maps — tool window id and tab id to
/// live content; the layout state itself stays pure and serializable, independent of whether
/// content exists (spec TW-9.3). Creation is pull-based: <see cref="GetOrCreateToolWindowContent"/>,
/// <see cref="MaterializeTab"/>, and Eager creation inside <see cref="Register"/>. Release is
/// diff-based via <see cref="NotifyTransition"/>, or performed by the state-changing methods of
/// this class themselves.
///
/// The transition contract: the application reports every layout transition — each core command
/// and every application of <see cref="LayoutApply.Apply"/> or
/// <see cref="LayoutApply.ResetToDefaults"/> — to <see cref="NotifyTransition"/>, one call per
/// operation. Batching several operations into one call is unsupported: a transient close of a
/// <see cref="ContentRetentionPolicy.DisposeOnClose"/> window would be missed (spec TW-9.2).
/// Transitions produced by this class itself — <see cref="Register"/>, <see cref="Unregister"/>
/// and the refusal path of <see cref="MaterializeTab"/> — maintain the maps internally and must
/// not be reported again. Single-threaded by design, like <see cref="ToolWindowRegistry"/>.
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
    /// eagerly or before a keep-alive close — is returned as is. Null for a window registered
    /// without a <see cref="ToolWindowDescriptor.ContentFactory"/>.
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
    /// Materializes a dock-area tab (spec DA-9.3, DA-9.4): returns the live content, creating it
    /// via the owning factory on first request. A tab whose owner has no live claim sleeps —
    /// nothing is touched and the UI shows a placeholder (DA-9.4). A factory refusing with null
    /// closes the tab by a regular CloseTab — uniformly for restored and freshly opened tabs
    /// (DA-9.3) — and the caller must continue from <see cref="TabMaterialization.State"/>,
    /// re-reading paths and pending materializations (spec DA-1.3).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="tabId">Id of a tab present in the dock area.</param>
    /// <exception cref="ArgumentException">No such tab exists in the dock area.</exception>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public TabMaterialization MaterializeTab(LayoutState state, string tabId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (!ContainsTab(state.DockArea, tabId))
        {
            throw new ArgumentException($"No tab '{tabId}' exists in the dock area.", nameof(tabId));
        }

        if (_tabContent.TryGetValue(tabId, out var existing))
        {
            return new TabMaterialization(TabMaterializationKind.Materialized, existing.Content, state);
        }

        if (!_registry.TryResolveTabClaim(tabId, out var factory, out _))
        {
            return new TabMaterialization(TabMaterializationKind.Sleeping, content: null, state);
        }

        if (factory.CreateContent(tabId) is not { } content)
        {
            return new TabMaterialization(TabMaterializationKind.Refused, content: null, state.CloseTab(tabId));
        }

        _tabContent[tabId] = (factory, content);
        return new TabMaterialization(TabMaterializationKind.Materialized, content, state);
    }

    /// <summary>
    /// Reports one layout transition and releases what left it. Tool window content under
    /// <see cref="ContentRetentionPolicy.DisposeOnClose"/> is released on the transition out of
    /// the open state by any path (spec TW-9.2); tab content is released when its id is no
    /// longer present in the dock area — a move between groups, hosts or windows keeps the id
    /// present and never touches content (spec DA-5.4).
    /// </summary>
    /// <param name="before">The state the operation was applied to.</param>
    /// <param name="after">The state the operation produced.</param>
    public void NotifyTransition(LayoutState before, LayoutState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<string>? closedWindows = null;
        foreach (var id in _toolWindowContent.Keys)
        {
            if (_registry.TryGet(id, out var descriptor)
                && descriptor.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose
                && IsOpen(before, id)
                && !IsOpen(after, id))
            {
                (closedWindows ??= []).Add(id);
            }
        }

        if (closedWindows is not null)
        {
            foreach (var id in closedWindows)
            {
                ReleaseToolWindowContent(id);
            }
        }

        List<string>? goneTabs = null;
        foreach (var id in _tabContent.Keys)
        {
            if (!ContainsTab(after.DockArea, id))
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
    /// after the existing windows of its slot. Sleeping tabs of the new owner are not touched:
    /// they materialize lazily as usual (spec DA-9.4). The call is transactional: Eager content
    /// is created before the registry mutation, so a throwing factory leaves the registry, the
    /// maps and the caller's state untouched — fix the factory and register again. Not a pure
    /// function: the registry and the content maps change alongside the returned state. Do not
    /// report this transition to <see cref="NotifyTransition"/>.
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

        return result;
    }

    /// <summary>
    /// Unregisters a tool window in a live session (spec TW-9.4): the window is closed, its body
    /// content is released under any retention policy, its state stays in the layout as sleeping
    /// (TW-10.2), and the tabs claimed by its <see cref="ToolWindowDescriptor.TabFactory"/> are
    /// closed in every dock-area host with their content released (spec TW-9.10, DA-8.3), in
    /// traversal order (spec DA-9.2). Re-registration picks the sleeping state up but does not
    /// restore the closed tabs (TW-9.10). Do not report this transition to
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

        // The claim disappears with the registration — collect the owner's tabs first.
        List<string>? ownedTabs = null;
        if (descriptor.TabFactory is { } tabFactory)
        {
            foreach (var tab in EnumerateDockTabs(state.DockArea))
            {
                if (tabFactory.OwnsTab(tab))
                {
                    (ownedTabs ??= []).Add(tab);
                }
            }
        }

        _registry.Unregister(toolWindowId);

        var result = state;
        if (state.ToolWindows.Any(w => string.Equals(w.Id, toolWindowId, StringComparison.Ordinal)))
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

    private static bool ContainsTab(DockAreaState area, string tabId)
    {
        if (TabTreeTraversal.FindGroupContaining(area.Root, tabId) is not null)
        {
            return true;
        }

        foreach (var window in area.Windows)
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
