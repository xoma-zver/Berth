using System.Collections.Immutable;
using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Context of one materialized tab tree (spec DA-9.6): the dock area of the main window
/// (<see cref="PanelId"/> null), the content tree of a tool window, or the tree of a
/// document window. Group and split views are tree-agnostic and reach everything
/// host-specific — the root, the SetSplitShares receiver, the shared host cache — through
/// this context; the reconciliation logic is shared verbatim between the trees. Document
/// windows have no identity of their own (spec DA-1.3): the floating layer refreshes
/// <see cref="DocumentWindowIndex"/> on every reconciliation pass, and every state change
/// re-projects before any further command can run, so the index is fresh at command time.
/// </summary>
internal sealed class TabTreeContext
{
    private readonly bool _isDocumentWindow;

    public TabTreeContext(BerthWorkspace workspace, string? panelId)
    {
        Workspace = workspace;
        PanelId = panelId;
    }

    private TabTreeContext(BerthWorkspace workspace)
    {
        Workspace = workspace;
        _isDocumentWindow = true;
    }

    /// <summary>Context of one document window's tree (spec DA-7.1); the index is set by the floating layer per pass.</summary>
    public static TabTreeContext ForDocumentWindow(BerthWorkspace workspace) => new(workspace);

    /// <summary>The owning workspace — the command funnel and the shared host cache.</summary>
    public BerthWorkspace Workspace { get; }

    /// <summary>Id of the tool window hosting the tree, or null for a dock-area host — the canHost key of the drop catalog (spec DA-9.7).</summary>
    public string? PanelId { get; }

    /// <summary>Index of the projected document window in <see cref="DockAreaState.Windows"/>; meaningful only for a document-window context.</summary>
    public int DocumentWindowIndex { get; set; } = -1;

    /// <summary>Root of the projected tree in the given state; null when the host left the layout.</summary>
    public TabTreeNode? GetRoot(LayoutState state)
    {
        if (PanelId is { } panelId)
        {
            return state.ToolWindows.FirstOrDefault(
                w => string.Equals(w.Id, panelId, StringComparison.Ordinal))?.ContentTree;
        }

        if (_isDocumentWindow)
        {
            return DocumentWindowIndex >= 0 && DocumentWindowIndex < state.DockArea.Windows.Length
                ? state.DockArea.Windows[DocumentWindowIndex].Root
                : null;
        }

        return state.DockArea.Root;
    }

    /// <summary>The SetSplitShares receiver of this tree (spec DA-5.6): the dock host or the panel overload.</summary>
    public LayoutState SetShares(LayoutState state, ImmutableArray<int> path, ImmutableArray<double> shares) =>
        PanelId is { } panelId
            ? state.SetSplitShares(panelId, path, shares)
            : state.SetSplitShares(
                _isDocumentWindow ? DockHost.DocumentWindow(DocumentWindowIndex) : DockHost.MainWindow,
                path,
                shares);

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
