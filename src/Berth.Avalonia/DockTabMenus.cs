using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Builder of the tab context menus (spec TW-5.16, DA-5.2, DA-5.5, DA-5.9, DA-8.2): every item
/// invokes a core command through <see cref="BerthWorkspace.Execute"/> (ADR-0004). Menus are
/// leaf chrome, rebuilt with the strip on every update, so they always reflect the state they
/// were built from. The split items are named «Split and Move …» — our split transfers the tab
/// (DA-5.5), while the reference's plain «Split …» copies content; the short label stays
/// reserved for a possible «Split and Duplicate» (v2). Moves between hosts are followed by
/// activation and focus (DA-5.4, DA-6.4): a closed receiving panel opens by DA-E39. A dock tab
/// offers «Move to the owner» only when its single confirmed owner is a tool window (canHost,
/// INV-D5); a conflicted claim confirms nothing (TW-9.11) and yields no item. A panel tab
/// offers «Move to Document Area» and then the tail of the full window menu — the reference's
/// tab menu includes the window menu (TW-5.16). On a platform with real windows any tab
/// offers «Move to New Window» (DA-5.7), and a tab living in a document window offers «Move
/// to Document Area» back — the command channel stays complete without DnD (ADR-0004).
/// </summary>
internal static class DockTabMenus
{
    /// <summary>The context menu of one tab header; <paramref name="panelId"/> is the hosting panel, or null in the dock area.</summary>
    public static MenuFlyout BuildTabMenu(
        LayoutState state,
        string tabId,
        ToolWindowRegistry registry,
        BerthWorkspace workspace,
        bool canRotate,
        string? panelId)
    {
        var menu = new MenuFlyout();
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => workspace.Execute(s => s.CloseTab(tabId));
        menu.Items.Add(close);

        menu.Items.Add(SplitItem("Split and Move Right", SplitDirection.Right));
        menu.Items.Add(SplitItem("Split and Move Down", SplitDirection.Down));
        menu.Items.Add(SplitItem("Split and Move Left", SplitDirection.Left));
        menu.Items.Add(SplitItem("Split and Move Up", SplitDirection.Up));

        if (canRotate)
        {
            // Only a group under a split parent rotates — a root group has no item (DA-5.9).
            var rotate = new MenuItem { Header = "Rotate Split" };
            rotate.Click += (_, _) => workspace.Execute(s => s.RotateSplit(tabId));
            menu.Items.Add(rotate);
        }

        if (panelId is null)
        {
            if (OwnerPanel(registry, tabId) is { } ownerId)
            {
                var title = registry.TryGet(ownerId, out var descriptor) ? descriptor.Title : ownerId;
                menu.Items.Add(MoveItem($"Move to {title}", s => MoveToOwner(s, tabId, ownerId, registry)));
            }

            if (!DockTrees.ContainsTab(state.DockArea.Root, tabId))
            {
                // A tab living in a document window returns to the main window by menu — the
                // command channel stays functionally complete without DnD (ADR-0004); the
                // mirror of the panel-tab item (TW-5.16).
                menu.Items.Add(MoveItem("Move to Document Area", s => MoveToMainWindow(s, tabId, registry)));
            }

            AddMoveToNewWindow();
        }
        else
        {
            menu.Items.Add(MoveItem("Move to Document Area", s => MoveToMainWindow(s, tabId, registry)));
            AddMoveToNewWindow();

            // The tail of the full window menu (TW-5.16): the reference's tab menu includes
            // the window menu below the tab section.
            var window = state.ToolWindows.FirstOrDefault(
                w => string.Equals(w.Id, panelId, StringComparison.Ordinal));
            if (window is not null)
            {
                ToolWindowMenus.AppendWindowItems(menu, window, workspace);
            }
        }

        return menu;

        void AddMoveToNewWindow()
        {
            if (!workspace.CanFloat)
            {
                return; // document windows do not materialize on this platform (TW-5.16, DA-7.5)
            }

            // Dock hosts accept every tab (INV-D5), so any tab may leave into a new document
            // window — real or pseudo (DA-5.7, DA-7.5); the default bounds come from the main
            // window (the workspace on the overlay platform) with an inset — the UI supplies
            // the pixels (ADR-0002), computed at click time.
            menu.Items.Add(MoveItem("Move to New Window", s =>
                s.MoveTabToNewWindow(tabId, workspace.DefaultFloatingBounds())));
        }

        MenuItem SplitItem(string header, SplitDirection direction)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                workspace.Execute(s => s.SplitTab(tabId, direction));
                // The gesture activation transfers focus into the moved tab (DA-6.4).
                workspace.FocusTab(tabId);
            };
            return item;
        }

        MenuItem MoveItem(string header, Func<LayoutState, LayoutState> move)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                workspace.Execute(move);
                // Activity follows the move (DA-5.4): the UI assigns it with ActivateTab and
                // transfers focus after the gesture (DA-6.4) — a closed receiving panel opens
                // by DA-E39, and the activation's own focus transfer (TW-6.6) covers the
                // panel direction; FocusTab completes the dock direction and no-ops when the
                // focus already arrived.
                workspace.Execute(s => s.ActivateTab(tabId));
                workspace.FocusTab(tabId);
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

    /// <summary>Move to the end of the main window's current group (DA-8.2) — the recipe of the DA-9.2 relocation.</summary>
    private static LayoutState MoveToMainWindow(LayoutState state, string tabId, ToolWindowRegistry registry)
    {
        var target = state.DockArea.CurrentTabId is { } current
            ? DockGroupRef.AtTab(current)
            : DockGroupRef.HostRoot(DockHost.MainWindow);
        return state.MoveTab(tabId, target, int.MaxValue, registry);
    }
}
