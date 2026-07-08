namespace Berth;

/// <summary>
/// Core commands over <see cref="LayoutState"/> (spec section 5). Every command is a pure
/// transition producing a new immutable state; the same command backs menu items, keyboard
/// shortcuts and completed drag gestures (ADR-0004). A command applied to an id absent from the
/// layout throws — sleeping states exist, but operations act on known tool windows.
/// Resizes (TW-5.9) and snapshot/apply (TW-5.14) are separate tasks (backlog 1.3, 1.6).
/// </summary>
public static class LayoutOperations
{
    /// <summary>
    /// Opens (shows) a tool window (spec TW-5.1, TW-5.2, TW-5.13). If another window of the same
    /// layer — docked ({<see cref="ToolWindowMode.DockPinned"/>, <see cref="ToolWindowMode.DockUnpinned"/>})
    /// or overlay ({<see cref="ToolWindowMode.Undock"/>}) — is open in the same slot, that one is
    /// closed with its other fields untouched (TW-5.1); a window of the other layer is left alone
    /// (INV-2). Opening always makes the stripe icon visible (TW-5.13). Opening an already open
    /// window changes nothing except activation (TW-5.2).
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tool window present in the layout.</param>
    /// <param name="activate">Whether the window becomes the active tool window (spec TW-6.5).</param>
    /// <exception cref="ArgumentException">No tool window with the given id exists in the layout.</exception>
    public static LayoutState Open(this LayoutState state, string id, bool activate = true)
    {
        var target = state.Require(id);
        if (target.IsOpen)
        {
            return activate ? state with { ActiveToolWindowId = id } : state;
        }

        var opened = target with { IsOpen = true, IsIconVisible = true };
        var result = state
            .EvictLayer(opened.Slot, opened.Mode.GetLayer(), exceptId: id)
            .MapWindow(id, _ => opened);
        return activate ? result with { ActiveToolWindowId = id } : result;
    }

    /// <summary>
    /// Closes (hides) a tool window (spec TW-5.3). Mode, weights and placement are kept; if the
    /// window was active, <see cref="LayoutState.ActiveToolWindowId"/> becomes null. Closing an
    /// already closed window is a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">No tool window with the given id exists in the layout.</exception>
    public static LayoutState Close(this LayoutState state, string id)
    {
        var target = state.Require(id);
        if (!target.IsOpen)
        {
            return state;
        }

        var result = state.MapWindow(id, w => w with { IsOpen = false });
        return string.Equals(state.ActiveToolWindowId, id, StringComparison.Ordinal)
            ? result with { ActiveToolWindowId = null }
            : result;
    }

    /// <summary>
    /// Activates a document in the dock area, which clears the active tool window (spec TW-6.5).
    /// </summary>
    public static LayoutState ActivateDocument(this LayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ActiveToolWindowId is null ? state : state with { ActiveToolWindowId = null };
    }

    /// <summary>
    /// Changes the presentation mode of a tool window (spec TW-5.6). Openness is not affected.
    /// Entering <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/> keeps the saved
    /// <see cref="ToolWindowState.FloatingBounds"/>, or adopts <paramref name="screenBounds"/> when none
    /// are saved. Entering any internal mode records it in <see cref="ToolWindowState.LastInternalMode"/>
    /// (INV-7). When the window is open, the destination layer is evicted per TW-5.1.
    /// </summary>
    /// <param name="state">Current layout.</param>
    /// <param name="id">Id of a tool window present in the layout.</param>
    /// <param name="mode">Target mode.</param>
    /// <param name="screenBounds">
    /// Current on-screen content bounds supplied by the UI; used only when moving to a floating mode
    /// with no saved bounds (spec TW-5.6). The core never invents pixels (ADR-0002).
    /// </param>
    /// <exception cref="ArgumentException">No tool window with the given id exists in the layout.</exception>
    public static LayoutState SetMode(
        this LayoutState state, string id, ToolWindowMode mode, FloatingBounds? screenBounds = null)
    {
        var target = state.Require(id);

        var updated = target with { Mode = mode };
        if (mode.IsInternal())
        {
            updated = updated with { LastInternalMode = mode };
        }
        else if ((target.FloatingBounds ?? screenBounds) is { } bounds)
        {
            updated = updated with { FloatingBounds = bounds };
        }

        var result = state.MapWindow(id, _ => updated);
        return updated.IsOpen
            ? result.EvictLayer(updated.Slot, mode.GetLayer(), exceptId: id)
            : result;
    }

    /// <summary>
    /// Moves a tool window to <paramref name="slot"/> at position <paramref name="index"/>, renumbering
    /// the orders of both the source and destination slots to a dense sequence (spec TW-5.7, INV-3).
    /// The index is clamped into range. A window open in a docked/overlay mode stays open in the new slot
    /// and evicts the window of its layer there («the mover wins», TW-5.1); a
    /// <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/> window only changes its
    /// placement (TW-5.7). Moving into the current slot is a reorder. Side geometry and
    /// <see cref="ToolWindowState.PairRatio"/> are untouched (TW-5.8).
    /// </summary>
    /// <exception cref="ArgumentException">No tool window with the given id exists in the layout.</exception>
    public static LayoutState Move(this LayoutState state, string id, ToolWindowSlot slot, int index)
    {
        var oldSlot = state.Require(id).Slot;

        var destinationIds = state.ToolWindows
            .Where(w => w.Slot == slot && !string.Equals(w.Id, id, StringComparison.Ordinal))
            .OrderBy(w => w.Order)
            .Select(w => w.Id)
            .ToList();
        destinationIds.Insert(Math.Clamp(index, 0, destinationIds.Count), id);

        var orders = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < destinationIds.Count; i++)
        {
            orders[destinationIds[i]] = i;
        }

        if (oldSlot != slot)
        {
            var sourceIds = state.ToolWindows
                .Where(w => w.Slot == oldSlot && !string.Equals(w.Id, id, StringComparison.Ordinal))
                .OrderBy(w => w.Order)
                .Select(w => w.Id)
                .ToList();
            for (var i = 0; i < sourceIds.Count; i++)
            {
                orders[sourceIds[i]] = i;
            }
        }

        var windows = state.ToolWindows.Select(w =>
        {
            var moved = string.Equals(w.Id, id, StringComparison.Ordinal) ? w with { Slot = slot } : w;
            return orders.TryGetValue(w.Id, out var order) ? moved with { Order = order } : moved;
        });

        var result = state with { ToolWindows = [.. windows] };
        var target = result.Require(id);
        return target.IsOpen
            ? result.EvictLayer(slot, target.Mode.GetLayer(), exceptId: id)
            : result;
    }

    /// <summary>
    /// Shows or hides the stripe icon of a tool window (spec TW-5.10, TW-5.11). Hiding also closes the
    /// window in any mode — including <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/>
    /// — and clears activation if it was active; the window keeps its place in the slot order (INV-3), so
    /// showing the icon again restores its position without any bookkeeping.
    /// </summary>
    /// <exception cref="ArgumentException">No tool window with the given id exists in the layout.</exception>
    public static LayoutState SetIconVisible(this LayoutState state, string id, bool visible)
    {
        var target = state.Require(id);
        if (visible)
        {
            return target.IsIconVisible ? state : state.MapWindow(id, w => w with { IsIconVisible = true });
        }

        var result = state.MapWindow(id, w => w with { IsIconVisible = false, IsOpen = false });
        return string.Equals(state.ActiveToolWindowId, id, StringComparison.Ordinal)
            ? result with { ActiveToolWindowId = null }
            : result;
    }

    /// <summary>
    /// Closes every open tool window in an internal mode ({<see cref="ToolWindowMode.DockPinned"/>,
    /// <see cref="ToolWindowMode.DockUnpinned"/>, <see cref="ToolWindowMode.Undock"/>}); open
    /// <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/> windows are left alone
    /// (spec TW-5.12). Clears activation only if the active window was closed.
    /// </summary>
    public static LayoutState HideAll(this LayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var windows = state.ToolWindows.Select(w =>
            w.IsOpen && w.Mode.IsInternal() ? w with { IsOpen = false } : w);
        var result = state with { ToolWindows = [.. windows] };

        return state.ActiveToolWindowId is { } activeId
            && result.ToolWindows.Any(w =>
                string.Equals(w.Id, activeId, StringComparison.Ordinal) && !w.IsOpen)
            ? result with { ActiveToolWindowId = null }
            : result;
    }

    /// <summary>Moves the quick access «⋯» button to the given stripe (spec TW-5.15, TW-8.1).</summary>
    public static LayoutState SetQuickAccessSide(this LayoutState state, QuickAccessSide side)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.QuickAccessSide == side ? state : state with { QuickAccessSide = side };
    }

    /// <summary>
    /// Closes the open window (if any) of the given layer in the given slot, leaving its other fields
    /// untouched (spec TW-5.1). The floating layer is never evicted — floating windows coexist (INV-2).
    /// </summary>
    private static LayoutState EvictLayer(
        this LayoutState state, ToolWindowSlot slot, ToolWindowLayer layer, string exceptId)
    {
        if (layer == ToolWindowLayer.Floating)
        {
            return state;
        }

        string? evictedActive = null;
        var windows = state.ToolWindows.Select(w =>
        {
            if (w.IsOpen
                && w.Slot == slot
                && w.Mode.GetLayer() == layer
                && !string.Equals(w.Id, exceptId, StringComparison.Ordinal))
            {
                if (string.Equals(state.ActiveToolWindowId, w.Id, StringComparison.Ordinal))
                {
                    evictedActive = w.Id;
                }

                return w with { IsOpen = false };
            }

            return w;
        });

        var result = state with { ToolWindows = [.. windows] };
        return evictedActive is null ? result : result with { ActiveToolWindowId = null };
    }

    private static LayoutState MapWindow(
        this LayoutState state, string id, Func<ToolWindowState, ToolWindowState> map)
    {
        var windows = state.ToolWindows;
        for (var i = 0; i < windows.Length; i++)
        {
            if (string.Equals(windows[i].Id, id, StringComparison.Ordinal))
            {
                return state with { ToolWindows = windows.SetItem(i, map(windows[i])) };
            }
        }

        throw NotInLayout(id);
    }

    private static ToolWindowState Require(this LayoutState state, string id)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (var window in state.ToolWindows)
        {
            if (string.Equals(window.Id, id, StringComparison.Ordinal))
            {
                return window;
            }
        }

        throw NotInLayout(id);
    }

    private static ArgumentException NotInLayout(string id) =>
        new($"No tool window '{id}' exists in the layout.", nameof(id));
}
