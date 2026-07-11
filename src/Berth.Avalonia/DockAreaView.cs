using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Berth.Controls;

/// <summary>
/// Projection of the main window's dock-area tree (spec TW-2.1, DA-9.6): reconciles split and
/// group views against the state. Tab hosts (<see cref="DockTabHost"/>) are cached by id,
/// created once and reattached only by command semantics; group and split views are matched
/// by tab overlap — groups have no identity of their own (DA-1.3) — and carry no retained
/// state, so a structural rebuild of the addressed node recreates them freely while the
/// surviving hosts return from the cache. Content is pulled through the workspace's
/// <see cref="ContentLifecycle"/> outside the projection pass: the refusal path of
/// MaterializeTab mutates the state (DA-9.3) and must not run from a render-triggered sync —
/// the lesson of the decorator's body bridge. Only active tabs of visible groups materialize
/// (TW-9.3); a tab of a sleeping owner keeps its placeholder (DA-9.4). Document windows are
/// not materialized until the floating-window phase (phase 6) — their tabs keep their cached
/// hosts and built views while away (DA-9.6).
/// </summary>
internal sealed class DockAreaView : Decorator
{
    private readonly BerthWorkspace _workspace;
    private readonly Dictionary<string, DockTabHost> _hosts = new(StringComparer.Ordinal);
    private bool _materializationScheduled;

    public DockAreaView(BerthWorkspace workspace)
    {
        _workspace = workspace;
        Name = "PART_DockTree";
    }

    /// <summary>The incremental projection pass (spec DA-9.6): hosts update in place, containers relay around them.</summary>
    public void Update(LayoutState state, ToolWindowRegistry registry)
    {
        // Hosts of ids gone from the whole layout are dropped — their content was released by
        // the lifecycle (TW-9.2); a tab away in a panel tree or a document window keeps its
        // host and built view for the return (DA-9.6).
        List<string>? gone = null;
        foreach (var id in _hosts.Keys)
        {
            if (!DockTrees.LayoutContainsTab(state, id))
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                BerthWorkspace.DetachFromParent(_hosts[id]);
                _hosts.Remove(id);
            }
        }

        var root = state.DockArea.Root;
        var existing = Child as Control;
        var kindMatches = root is TabGroupNode ? existing is TabGroupView : existing is SplitView;
        if (existing is not null && !kindMatches)
        {
            ReleaseHosts(existing);
            Child = null;
            existing = null;
        }

        var view = Reconcile(root, existing, state, registry, []);
        if (!ReferenceEquals(Child, view))
        {
            Child = view;
        }

        // Sleeping is re-resolved on every pass — a live registration plus Refresh() wakes
        // the tab up (DA-9.4); the resolve is a cheap registry lookup.
        foreach (var host in _hosts.Values)
        {
            host.IsSleeping = false;
        }

        ScheduleMaterialization();
    }

    /// <summary>Host of a tab, created on first need (spec DA-9.6); the placeholder title follows the provider.</summary>
    internal DockTabHost GetHost(string id)
    {
        if (!_hosts.TryGetValue(id, out var host))
        {
            host = new DockTabHost(id);
            _hosts[id] = host;
        }

        host.UpdateTitle(_workspace.TabTitleProvider?.Invoke(id) ?? id);
        return host;
    }

    /// <summary>
    /// Moves keyboard focus into the tab's content (spec TW-6.6, DA-6.4): false when the tab
    /// has no attached host — closed, away in a non-materialized host, or a null id; focus
    /// already inside the host is left alone.
    /// </summary>
    internal bool TryFocusTab(string? id)
    {
        // A parentless host is a detached cache entry (DA-9.6) — nothing to focus; the
        // parent is the attachment signal the ReleaseHosts discipline maintains.
        if (id is null || !_hosts.TryGetValue(id, out var host) || host.Parent is null)
        {
            return false;
        }

        if (!host.IsKeyboardFocusWithin)
        {
            host.FocusContent();
        }

        return true;
    }

    /// <summary>Reconciles one state node into a view: an existing view of the right kind updates in place.</summary>
    internal Control Reconcile(
        TabTreeNode node, Control? existing, LayoutState state, ToolWindowRegistry registry, ImmutableArray<int> path)
    {
        switch (node)
        {
            case TabGroupNode group:
            {
                var view = existing as TabGroupView ?? new TabGroupView(_workspace, this);
                view.Update(group, state, registry, path);
                return view;
            }

            case SplitNode split:
            {
                var view = existing as SplitView ?? new SplitView(_workspace, this);
                view.Update(split, state, registry, path);
                return view;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node, message: null);
        }
    }

    /// <summary>Reconciliation key of a child view (groups have no identity, spec DA-1.3).</summary>
    internal static HashSet<string> TabsOfView(Control view) => view switch
    {
        TabGroupView group => group.Tabs,
        SplitView split => split.Tabs,
        _ => [],
    };

    /// <summary>Returns every cached host of a discarded view subtree to the cache (spec DA-9.6).</summary>
    internal static void ReleaseHosts(Control view)
    {
        switch (view)
        {
            case TabGroupView group:
                group.DetachHost();
                break;
            case SplitView split:
                foreach (var child in split.ChildViews)
                {
                    ReleaseHosts(child);
                }

                break;
        }
    }

    // ---- lazy materialization outside the projection pass ----

    private void ScheduleMaterialization()
    {
        if (_materializationScheduled
            || _workspace.Lifecycle is null
            || _workspace.State is not { } state
            || NextPendingTab(state) is null)
        {
            return;
        }

        _materializationScheduled = true;
        Dispatcher.UIThread.Post(Materialize);
    }

    /// <summary>
    /// The pull pass (spec DA-9.3, DA-9.4): materializes active tabs of visible groups from
    /// the current state, re-read after every step (DA-1.3). A refusal closed the tab inside
    /// the coordinator — the produced state is assigned without a lifecycle report (the
    /// coordinator-transition contract) and the loop continues from it; the loop terminates
    /// because every refusal removes a tab.
    /// </summary>
    private void Materialize()
    {
        _materializationScheduled = false;
        while (_workspace.Lifecycle is { } lifecycle && _workspace.State is { } state)
        {
            if (NextPendingTab(state) is not { } id)
            {
                return;
            }

            var result = lifecycle.MaterializeTab(state, id);
            switch (result.Kind)
            {
                case TabMaterializationKind.Materialized:
                    _hosts[id].SetContent(result.Content!);
                    break;
                case TabMaterializationKind.Sleeping:
                    _hosts[id].IsSleeping = true; // the placeholder stays (DA-9.4)
                    break;
                case TabMaterializationKind.Refused:
                    _workspace.State = result.State;
                    break;
            }
        }
    }

    private string? NextPendingTab(LayoutState state)
    {
        foreach (var group in DockTrees.Groups(state.DockArea.Root))
        {
            if (group.ActiveTabId is { } active
                && _hosts.TryGetValue(active, out var host)
                && !host.HasContent
                && !host.IsSleeping)
            {
                return active;
            }
        }

        return null;
    }
}
