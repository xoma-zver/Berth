using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Projection of the main window's dock-area tree (spec TW-2.1, DA-9.6): the main-window
/// <see cref="TabTreeContext"/> materialized into the workspace grid. Tab hosts come from the
/// workspace-wide <see cref="TabHostCache"/> — shared with the panel trees, so a move between
/// hosts reattaches the same host with its built view; group and split views are matched by
/// tab overlap — groups have no identity of their own (DA-1.3) — and carry no retained state.
/// The sweep and the lazy materialization pass run at the workspace level, over every
/// materialized tree at once. Document windows are projected by the floating layer over the
/// same cache (task 6.0); on a platform without real windows their tabs keep their cached
/// hosts and built views while away (DA-9.6).
/// </summary>
internal sealed class DockAreaView : Decorator
{
    private readonly TabTreeContext _context;

    public DockAreaView(BerthWorkspace workspace)
    {
        _context = new TabTreeContext(workspace, panelId: null);
        Name = "PART_DockTree";
    }

    /// <summary>The incremental projection pass (spec DA-9.6): hosts update in place, containers relay around them.</summary>
    public void Update(LayoutState state, ToolWindowRegistry registry) =>
        _context.ReconcileRoot(this, state.DockArea.Root, state, registry);
}
