using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Builder of the dock tab context menu (spec DA-5.2, DA-5.5, DA-5.9, DA-8.2): every item
/// invokes a core command through <see cref="BerthWorkspace.Execute"/> (ADR-0004). Menus are
/// leaf chrome, rebuilt with the strip on every update, so they always reflect the state they
/// were built from. Splitting transfers focus into the moved tab — the gesture activation of
/// DA-6.4. «Move to …» appears only for a tab whose single confirmed owner is a tool window
/// (canHost, INV-D5); a conflicted claim confirms nothing (TW-9.11) and yields no item.
/// «Move to New Window» is absent until document windows materialize (phase 6).
/// </summary>
internal static class DockTabMenus
{
    /// <summary>The context menu of one tab header.</summary>
    public static MenuFlyout BuildTabMenu(
        LayoutState state, string tabId, ToolWindowRegistry registry, BerthWorkspace workspace, bool canRotate)
    {
        var menu = new MenuFlyout();
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => workspace.Execute(s => s.CloseTab(tabId));
        menu.Items.Add(close);

        menu.Items.Add(SplitItem("Split Right", SplitDirection.Right));
        menu.Items.Add(SplitItem("Split Down", SplitDirection.Down));
        menu.Items.Add(SplitItem("Split Left", SplitDirection.Left));
        menu.Items.Add(SplitItem("Split Up", SplitDirection.Up));

        if (canRotate)
        {
            // Only a group under a split parent rotates — a root group has no item (DA-5.9).
            var rotate = new MenuItem { Header = "Rotate Split" };
            rotate.Click += (_, _) => workspace.Execute(s => s.RotateSplit(tabId));
            menu.Items.Add(rotate);
        }

        if (OwnerPanel(registry, tabId) is { } ownerId)
        {
            var title = registry.TryGet(ownerId, out var descriptor) ? descriptor.Title : ownerId;
            var move = new MenuItem { Header = $"Move to {title}" };
            move.Click += (_, _) => workspace.Execute(s => MoveToOwner(s, tabId, ownerId, registry));
            menu.Items.Add(move);
        }

        return menu;

        MenuItem SplitItem(string header, SplitDirection direction)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                workspace.Execute(s => s.SplitTab(tabId, direction));
                // The gesture activation transfers focus into the moved tab (DA-6.4).
                workspace.FocusDockTab(tabId);
            };
            return item;
        }
    }

    /// <summary>Confirmed tool window owner of the tab, or null: unclaimed, a document, or a conflicted claim.</summary>
    private static string? OwnerPanel(ToolWindowRegistry registry, string tabId)
    {
        try
        {
            return registry.ResolveTabOwner(tabId)?.ToolWindowId;
        }
        catch (InvalidOperationException)
        {
            // A conflicted claim confirms nothing (TW-9.11); menu building is neither an
            // operation nor a materialization — the application error surfaces there.
            return null;
        }
    }

    /// <summary>Move into the owner's tree (DA-8.2): the first non-empty group, else the empty root group (DA-1.3).</summary>
    private static LayoutState MoveToOwner(LayoutState state, string tabId, string ownerId, ToolWindowRegistry registry)
    {
        var owner = state.ToolWindows.FirstOrDefault(w => string.Equals(w.Id, ownerId, StringComparison.Ordinal));
        if (owner is null)
        {
            // A registered owner always has a state (INV-1); guarded for a stale menu anyway.
            return state;
        }

        var target = DockTrees.FirstNonEmptyGroup(owner.ContentTree) is { } group
            ? DockGroupRef.AtTab(group.Tabs[0])
            : DockGroupRef.PanelRoot(ownerId);
        return state.MoveTab(tabId, target, int.MaxValue, registry);
    }
}
