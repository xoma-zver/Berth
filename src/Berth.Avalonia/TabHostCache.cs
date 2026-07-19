using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Berth.Controls;

/// <summary>
/// The single tab-host cache of a workspace (DA-9.6): one <see cref="DockTabHost"/> per
/// tab id across every materialized tree — the dock area of the main window and the content
/// trees of open tool windows. Tab ids are unique across the whole layout (INV-D2), so the id
/// is a global key and a move between a panel and the dock area reattaches the same host with
/// its built view. The cache also runs the lazy materialization pass — content is pulled
/// through the workspace's <see cref="ContentLifecycle"/> outside the projection pass, because
/// the refusal path of MaterializeTab mutates the state (DA-9.3) and must not run from a
/// render-triggered sync — and the sweep pass: hosts of ids gone from the layout are dropped
/// (their content was released by the lifecycle, TW-9.2), and a host whose tab lost its live
/// ownership claim forgets the released content (the owner's unregistration, TW-9.4).
/// </summary>
internal sealed class TabHostCache
{
    private readonly BerthWorkspace _workspace;
    private readonly Dictionary<string, DockTabHost> _hosts = new(StringComparer.Ordinal);
    private bool _materializationScheduled;

    public TabHostCache(BerthWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Host of a tab, created on first need (DA-9.6). The placeholder title follows the
    /// fallback chain: the application's <see cref="BerthWorkspace.TabTitleProvider"/>, then —
    /// for a body tab, whose id names its tool window (TW-9.5) — the descriptor's title, then
    /// the id itself.
    /// </summary>
    public DockTabHost GetHost(string id)
    {
        if (!_hosts.TryGetValue(id, out var host))
        {
            host = new DockTabHost(id);
            _hosts[id] = host;
        }

        host.UpdateTitle(TitleOf(_workspace, id));
        return host;
    }

    /// <summary>The cached host of a tab, or null — never creates one (the ghost passport peek of TW-5.17 v0.26).</summary>
    public DockTabHost? TryPeek(string id) => _hosts.TryGetValue(id, out var host) ? host : null;

    /// <summary>
    /// The tab title fallback chain of DA-9.6: the application's
    /// <see cref="BerthWorkspace.TabTitleProvider"/>, then — for a body tab, whose id names
    /// its tool window (TW-9.5) — the descriptor's title, then the id itself. Shared by the
    /// host placeholder and the tab headers.
    /// </summary>
    public static string TitleOf(BerthWorkspace workspace, string id)
    {
        var title = workspace.TabTitleProvider?.Invoke(id);
        if (title is null && workspace.Registry is { } registry && registry.TryGet(id, out var descriptor))
        {
            title = descriptor.Title;
        }

        return title ?? id;
    }

    /// <summary>
    /// Moves keyboard focus into the tab's content (TW-6.6, DA-6.4): false when the tab
    /// has no attached host — closed, away in a non-materialized host, or a null id; focus
    /// already inside the host is left alone.
    /// </summary>
    public bool TryFocusTab(string? id)
    {
        // Only a host in the live tree can take focus: a detached cache entry has no parent,
        // and a host retained inside a closed panel's subtree keeps its parent while being
        // outside the tree (DA-9.6) — attachment to the tree root is the reliable signal.
        if (id is null
            || !_hosts.TryGetValue(id, out var host)
            || !((ILogical)host).IsAttachedToLogicalTree)
        {
            return false;
        }

        if (!host.IsKeyboardFocusWithin)
        {
            // A host living in a floating window activates its OS window first (TW-6.6,
            // DA-6.4) — keyboard focus follows window activation on real platforms.
            BerthWorkspace.ActivateWindowOf(host);
            host.FocusContent();
        }

        return true;
    }

    /// <summary>Drops the host's content and view without touching the cache entry (DA-9.6).</summary>
    public void ResetContent(string id)
    {
        if (_hosts.TryGetValue(id, out var host))
        {
            host.ResetContent();
        }
    }

    /// <summary>
    /// The per-sync sweep: hosts of ids gone from the whole layout are dropped; a host whose
    /// tab has content but no live ownership claim forgets it — the claims die only with the
    /// owner's unregistration, which released the content (TW-9.4, DA-9.6); sleeping markers
    /// reset so a live registration plus Refresh() wakes tabs up (DA-9.4).
    /// </summary>
    public void Sweep(LayoutState state, ToolWindowRegistry registry)
    {
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

        foreach (var host in _hosts.Values)
        {
            host.IsSleeping = false;
            if (host.HasContent && !HasLiveClaim(registry, host.TabId))
            {
                host.ResetContent();
            }
        }
    }

    /// <summary>Detaches every host and empties the cache — the full reconfiguration of the workspace.</summary>
    public void Clear()
    {
        foreach (var host in _hosts.Values)
        {
            BerthWorkspace.DetachFromParent(host);
        }

        _hosts.Clear();
    }

    // ---- lazy materialization outside the projection pass ----

    /// <summary>Schedules the pull pass when something visible lacks content (TW-9.3, DA-9.3).</summary>
    public void ScheduleMaterialization()
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
    /// The pull pass (DA-9.3, DA-9.4): materializes active tabs of visible groups —
    /// the main window's and those of open panels' trees — from the current state, re-read
    /// after every step (DA-1.3). A refusal closed the tab inside the coordinator — the
    /// produced state is assigned without a lifecycle report (the coordinator-transition
    /// contract) and the loop continues from it; the loop terminates because every refusal
    /// removes a tab.
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

    /// <summary>
    /// Next visible tab lacking content: active tabs of the main window's groups, of the
    /// materialized document windows — real or pseudo (DA-9.6, tasks 6.0/6.1) — then of the
    /// trees of open, hosted panels — a closed panel's tabs never materialize (TW-9.3,
    /// DA-9.6), keeping the OnFirstOpen creation moment of the body (TW-9.2). Document
    /// windows on a platform where they never materialize stay unmaterialized.
    /// </summary>
    private string? NextPendingTab(LayoutState state)
    {
        foreach (var group in DockTrees.Groups(state.DockArea.Root))
        {
            if (Pending(group) is { } id)
            {
                return id;
            }
        }

        if (_workspace.CanFloat)
        {
            foreach (var window in state.DockArea.Windows)
            {
                foreach (var group in DockTrees.Groups(window.Root))
                {
                    if (Pending(group) is { } id)
                    {
                        return id;
                    }
                }
            }
        }

        foreach (var panel in state.ToolWindows)
        {
            if (!_workspace.IsHosted(panel))
            {
                continue;
            }

            foreach (var group in DockTrees.Groups(panel.ContentTree))
            {
                if (Pending(group) is { } id)
                {
                    return id;
                }
            }
        }

        return null;
    }

    private string? Pending(TabGroupNode group) =>
        group.ActiveTabId is { } active
            && _hosts.TryGetValue(active, out var host)
            && !host.HasContent
            && !host.IsSleeping
        ? active
        : null;

    /// <summary>
    /// Whether any live registration claims the id. A conflicted claim counts as live: content
    /// could not have been created under it, and the application error surfaces at operations
    /// and materialization, not here (TW-9.11).
    /// </summary>
    private static bool HasLiveClaim(ToolWindowRegistry registry, string id)
    {
        try
        {
            return registry.ResolveTabOwner(id) is not null;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
