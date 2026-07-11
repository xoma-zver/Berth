using System.Collections.Immutable;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Context of one materialized tab tree (spec DA-9.6): the dock area of the main window
/// (<see cref="PanelId"/> null) or the content tree of a tool window. Group and split views
/// are tree-agnostic and reach everything host-specific — the root, the SetSplitShares
/// receiver, the shared host cache — through this context; the reconciliation logic is shared
/// verbatim between the trees.
/// </summary>
internal sealed class TabTreeContext
{
    public TabTreeContext(BerthWorkspace workspace, string? panelId)
    {
        Workspace = workspace;
        PanelId = panelId;
    }

    /// <summary>The owning workspace — the command funnel and the shared host cache.</summary>
    public BerthWorkspace Workspace { get; }

    /// <summary>Id of the tool window hosting the tree, or null for the main window's dock area.</summary>
    public string? PanelId { get; }

    /// <summary>Root of the projected tree in the given state; null when the panel left the layout.</summary>
    public TabTreeNode? GetRoot(LayoutState state) => PanelId is null
        ? state.DockArea.Root
        : state.ToolWindows.FirstOrDefault(w => string.Equals(w.Id, PanelId, StringComparison.Ordinal))?.ContentTree;

    /// <summary>The SetSplitShares receiver of this tree (spec DA-5.6): the main window or the panel overload.</summary>
    public LayoutState SetShares(LayoutState state, ImmutableArray<int> path, ImmutableArray<double> shares) =>
        PanelId is null
            ? state.SetSplitShares(DockHost.MainWindow, path, shares)
            : state.SetSplitShares(PanelId, path, shares);

    /// <summary>Reconciles one state node into a view: an existing view of the right kind updates in place.</summary>
    public Control Reconcile(
        TabTreeNode node, Control? existing, LayoutState state, ToolWindowRegistry registry, ImmutableArray<int> path)
    {
        switch (node)
        {
            case TabGroupNode group:
            {
                var view = existing as TabGroupView ?? new TabGroupView(this);
                view.Update(group, state, registry, path);
                return view;
            }

            case SplitNode split:
            {
                var view = existing as SplitView ?? new SplitView(this);
                view.Update(split, state, registry, path);
                return view;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node, message: null);
        }
    }

    /// <summary>
    /// Reconciles the whole tree into a container: a root kind mismatch discards the old view —
    /// its hosts return to the cache — before the new one attaches (the whitelisted structural
    /// rebuild of the addressed node, spec DA-9.6).
    /// </summary>
    public void ReconcileRoot(Decorator container, TabTreeNode root, LayoutState state, ToolWindowRegistry registry)
    {
        var existing = container.Child as Control;
        var kindMatches = root is TabGroupNode ? existing is TabGroupView : existing is SplitView;
        if (existing is not null && !kindMatches)
        {
            ReleaseHosts(existing);
            container.Child = null;
            existing = null;
        }

        var view = Reconcile(root, existing, state, registry, []);
        if (!ReferenceEquals(container.Child, view))
        {
            container.Child = view;
        }
    }

    /// <summary>Reconciliation key of a child view (groups have no identity, spec DA-1.3).</summary>
    public static HashSet<string> TabsOfView(Control view) => view switch
    {
        TabGroupView group => group.Tabs,
        SplitView split => split.Tabs,
        _ => [],
    };

    /// <summary>Returns every cached host of a discarded view subtree to the cache (spec DA-9.6).</summary>
    public static void ReleaseHosts(Control view)
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
}
