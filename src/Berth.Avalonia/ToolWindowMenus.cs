using Avalonia.Controls;

namespace Berth.Controls;

/// <summary>
/// Builders of the tool window menus (spec TW-5.16) and the quick access menus (TW-8.3,
/// TW-5.15). Every item invokes a core command through <see cref="BerthWorkspace.Execute"/>
/// (ADR-0004). Menus are built when the projection is rebuilt, so they always reflect the
/// state they were built from, and the rebuild after each command replaces them wholesale.
/// View Mode is built from the platform capabilities (TW-7.6, TW-5.16): a platform with real
/// windows offers all five modes, the browser hides only Window — Float materializes as a
/// pseudo-window (TW-7.7) — and supplements a stored-Window record with «Dock», the return to
/// its last internal mode (TW-5.6); a detached workspace, where nothing floats yet, falls
/// back to the internal modes or the bare «Dock» item.
/// </summary>
internal static class ToolWindowMenus
{
    /// <summary>Slot names, index-aligned with <see cref="ToolWindowSlot.All"/> (spec TW-1.1).</summary>
    private static readonly string[] SlotHeaders =
        ["Left Top", "Left Bottom", "Right Top", "Right Bottom", "Bottom Left", "Bottom Right"];

    /// <summary>The compact stripe-icon context menu: Hide — icon hiding (TW-5.10) — then «Dock» for a floating record, then Move to (spec TW-5.16).</summary>
    public static MenuFlyout BuildIconMenu(ToolWindowState window, BerthWorkspace workspace)
    {
        var id = window.Id;
        var menu = new MenuFlyout();
        var hide = new MenuItem { Header = "Hide" };
        hide.Click += (_, _) => workspace.Execute(s => s.SetIconVisible(id, false));
        menu.Items.Add(hide);
        if (!window.Mode.IsInternal())
        {
            menu.Items.Add(DockItem(window, workspace));
        }

        menu.Items.Add(MoveToItem(window, workspace));
        return menu;
    }

    /// <summary>The full menu of the decorator's «⋮» button and title-bar context (spec TW-5.16).</summary>
    public static MenuFlyout BuildWindowMenu(ToolWindowState window, BerthWorkspace workspace)
    {
        var menu = new MenuFlyout();
        AppendWindowItems(menu, window, workspace);
        return menu;
    }

    /// <summary>
    /// The items of the full menu (spec TW-5.16): View Mode, Move to, «Remove from Sidebar» —
    /// icon hiding, the same command as «Hide» of the compact icon menu (TW-5.10) — and
    /// «Hide» — closing, the «—» button's command (TW-5.3). Shared with the tail of the panel
    /// tab menu: the reference's tab menu includes the window menu.
    /// </summary>
    public static void AppendWindowItems(MenuFlyout menu, ToolWindowState window, BerthWorkspace workspace)
    {
        var id = window.Id;
        menu.Items.Add(ViewModeItem(window, workspace));
        menu.Items.Add(MoveToItem(window, workspace));
        var remove = new MenuItem { Header = "Remove from Sidebar" };
        remove.Click += (_, _) => workspace.Execute(s => s.SetIconVisible(id, false));
        menu.Items.Add(remove);
        var hide = new MenuItem { Header = "Hide" };
        hide.Click += (_, _) => workspace.Execute(s => s.Close(id));
        menu.Items.Add(hide);
    }

    /// <summary>The quick access list (spec TW-8.2): selecting an item returns the icon and opens the window (TW-8.3).</summary>
    public static MenuFlyout BuildQuickAccessList(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        var menu = new MenuFlyout();
        foreach (var descriptor in QuickAccess.List(state, registry))
        {
            var id = descriptor.Id;
            var item = new MenuItem { Header = descriptor.Title };
            item.Click += (_, _) => workspace.Execute(s => s.SetIconVisible(id, true).Open(id));
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The «⋯» context menu: moving the button between the stripes (spec TW-5.15, TW-8.1, TW-5.16).</summary>
    public static MenuFlyout BuildQuickAccessSideMenu(QuickAccessSide current, BerthWorkspace workspace)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(SideItem("Move to Left", QuickAccessSide.Left));
        menu.Items.Add(SideItem("Move to Right", QuickAccessSide.Right));
        return menu;

        MenuItem SideItem(string header, QuickAccessSide side)
        {
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = side == current,
                IsEnabled = side != current,
            };
            item.Click += (_, _) => workspace.Execute(s => s.SetQuickAccessSide(side));
            return item;
        }
    }

    private static MenuItem ViewModeItem(ToolWindowState window, BerthWorkspace workspace)
    {
        var root = new MenuItem { Header = "View Mode" };
        if (workspace.CanFloat)
        {
            // Items of unavailable modes are hidden (TW-5.16, TW-7.6): a platform with real
            // windows offers all five, the browser offers four — Float is a pseudo-window
            // there (TW-7.7) and only Window stays hidden.
            root.Items.Add(ModeItem("Dock Pinned", ToolWindowMode.DockPinned));
            root.Items.Add(ModeItem("Dock Unpinned", ToolWindowMode.DockUnpinned));
            root.Items.Add(ModeItem("Undock", ToolWindowMode.Undock));
            root.Items.Add(ModeItem("Float", ToolWindowMode.Float));
            if (workspace.CanUseWindowed)
            {
                root.Items.Add(ModeItem("Window", ToolWindowMode.Window));
            }
            else if (window.Mode == ToolWindowMode.Window)
            {
                // The stored mode's own item is hidden: «Dock» supplements the section as
                // the way back (TW-5.16) — the record presents as an effective Float (TW-7.6).
                root.Items.Add(DockItem(window, workspace));
            }
        }
        else if (window.Mode.IsInternal())
        {
            root.Items.Add(ModeItem("Dock Pinned", ToolWindowMode.DockPinned));
            root.Items.Add(ModeItem("Dock Unpinned", ToolWindowMode.DockUnpinned));
            root.Items.Add(ModeItem("Undock", ToolWindowMode.Undock));
        }
        else
        {
            // A floating record on a detached workspace offers only the return; the same
            // DockItem is live and tested in the icon menu.
            root.Items.Add(DockItem(window, workspace));
        }

        return root;

        MenuItem ModeItem(string header, ToolWindowMode mode)
        {
            var id = window.Id;
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = window.Mode == mode,
                IsEnabled = window.Mode != mode,
            };
            // Entering Float/Window adopts the content's current screen bounds when none are
            // saved (TW-5.6): the UI supplies the pixels at click time (ADR-0002), computed
            // before the command so the lambda stays pure over its input state.
            item.Click += (_, _) =>
            {
                var screenBounds = mode.IsInternal() ? null : workspace.ScreenBoundsOf(id);
                workspace.Execute(s => s.SetMode(id, mode, screenBounds));
            };
            return item;
        }
    }

    /// <summary>«Dock» of a floating record — the return to the last internal mode (spec TW-5.6, E27).</summary>
    private static MenuItem DockItem(ToolWindowState window, BerthWorkspace workspace)
    {
        var id = window.Id;
        var item = new MenuItem { Header = "Dock" };
        // The return target is read from the command's input state, not captured — like the
        // stripe toggle, the lambda stays independent of the rebuild-per-command contract.
        item.Click += (_, _) => workspace.Execute(s => s.SetMode(
            id, s.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal)).LastInternalMode));
        return item;
    }

    private static MenuItem MoveToItem(ToolWindowState window, BerthWorkspace workspace)
    {
        var id = window.Id;
        var root = new MenuItem { Header = "Move to" };
        for (var i = 0; i < ToolWindowSlot.All.Length; i++)
        {
            var slot = ToolWindowSlot.All[i];
            var item = new MenuItem
            {
                Header = SlotHeaders[i],
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = slot == window.Slot,
                IsEnabled = slot != window.Slot,
            };
            // To the end of the receiving slot: Move clamps the index into range (TW-5.7).
            item.Click += (_, _) => workspace.Execute(s => s.Move(id, slot, int.MaxValue));
            root.Items.Add(item);
        }

        return root;
    }
}
