using Xunit;

namespace Berth.Core.Tests;

/// <summary>Core operations of spec section 5 (TW-5.1…TW-5.15, excluding resizes and snapshot/apply) and the edge-case catalogue.</summary>
public class OperationsTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot LeftSecondary = new(ToolWindowSide.Left, ToolWindowGroup.Secondary);
    private static readonly ToolWindowSlot RightPrimary = new(ToolWindowSide.Right, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot BottomSecondary = new(ToolWindowSide.Bottom, ToolWindowGroup.Secondary);

    private static ToolWindowState Window(string id, ToolWindowSlot slot, int order) => new(id, slot, order);

    private static LayoutState Layout(params ToolWindowState[] windows) =>
        LayoutState.Empty with { ToolWindows = [.. windows] };

    private static ToolWindowState Get(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    // ---- TW-5.1 Open with same-layer eviction ----

    [Fact]
    public void TW_5_1_open_evicts_open_window_of_same_layer_in_slot() // E1
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.7 },
            Window("b", LeftPrimary, 1));

        var result = layout.Open("b");

        Assert.False(Get(result, "a").IsOpen);
        Assert.True(Get(result, "b").IsOpen);
        Assert.Equal("b", result.ActiveToolWindowId);
        // Evicted window keeps its other fields (TW-5.1).
        Assert.Equal(0.7, Get(result, "a").PairRatio);
        Assert.Equal(LeftPrimary, Get(result, "a").Slot);
    }

    [Fact]
    public void TW_5_1_open_does_not_evict_the_other_group() // E2
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true },
            Window("c", LeftPrimary, 1));

        var result = layout.Open("c");

        Assert.False(Get(result, "a").IsOpen);
        Assert.True(Get(result, "b").IsOpen);
        Assert.True(Get(result, "c").IsOpen);
    }

    [Fact]
    public void TW_5_1_open_undock_coexists_with_open_docked() // E16
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftPrimary, 1) with { Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock });

        var result = layout.Open("b");

        Assert.True(Get(result, "a").IsOpen);
        Assert.True(Get(result, "b").IsOpen);
    }

    [Fact]
    public void TW_5_1_open_evicting_active_without_activation_clears_active()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftPrimary, 1)) with
        { ActiveToolWindowId = "a" };

        var result = layout.Open("b", activate: false);

        Assert.False(Get(result, "a").IsOpen);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_5_13_open_hidden_icon_makes_it_visible()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsIconVisible = false });

        var result = layout.Open("a");

        Assert.True(Get(result, "a").IsOpen);
        Assert.True(Get(result, "a").IsIconVisible);
    }

    // ---- TW-5.2 Open of an already open window ----

    [Fact]
    public void TW_5_2_open_open_window_only_activates()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.6 });

        var activated = layout.Open("a");
        Assert.Equal("a", activated.ActiveToolWindowId);
        Assert.Equal(0.6, Get(activated, "a").PairRatio);

        var untouched = layout.Open("a", activate: false);
        Assert.Equal(layout, untouched);
    }

    // ---- TW-5.3 Close ----

    [Fact]
    public void TW_5_3_close_keeps_mode_weights_placement_and_clears_active() // E3
    {
        var layout = Layout(Window("a", LeftPrimary, 2) with
        {
            IsOpen = true,
            Mode = ToolWindowMode.DockUnpinned,
            LastInternalMode = ToolWindowMode.DockUnpinned,
            PairRatio = 0.8,
        }) with
        { ActiveToolWindowId = "a" };

        var result = layout.Close("a");

        var a = Get(result, "a");
        Assert.False(a.IsOpen);
        Assert.Equal(ToolWindowMode.DockUnpinned, a.Mode);
        Assert.Equal(LeftPrimary, a.Slot);
        Assert.Equal(2, a.Order);
        Assert.Equal(0.8, a.PairRatio);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_5_3_close_of_inactive_window_keeps_active()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", RightPrimary, 0) with { IsOpen = true }) with
        { ActiveToolWindowId = "a" };

        var result = layout.Close("b");

        Assert.Equal("a", result.ActiveToolWindowId);
    }

    // ---- TW-5.6 SetMode ----

    [Fact]
    public void TW_5_6_setmode_to_float_saves_bounds_and_keeps_openness()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true });
        var bounds = new FloatingBounds(10, 20, 300, 200);

        var result = layout.SetMode("a", ToolWindowMode.Float, bounds);

        var a = Get(result, "a");
        Assert.Equal(ToolWindowMode.Float, a.Mode);
        Assert.Equal(bounds, a.FloatingBounds);
        Assert.True(a.IsOpen);
        Assert.Equal(ToolWindowMode.DockPinned, a.LastInternalMode);
    }

    [Fact]
    public void TW_5_6_setmode_to_float_prefers_saved_bounds_over_screen_bounds()
    {
        var saved = new FloatingBounds(1, 2, 3, 4);
        var layout = Layout(Window("a", LeftPrimary, 0) with { FloatingBounds = saved });

        var result = layout.SetMode("a", ToolWindowMode.Float, new FloatingBounds(9, 9, 9, 9));

        Assert.Equal(saved, Get(result, "a").FloatingBounds);
    }

    [Fact]
    public void TW_5_6_setmode_rejects_non_finite_screen_bounds() // TW-5.9
    {
        var layout = Layout(Window("a", LeftPrimary, 0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.SetMode("a", ToolWindowMode.Float, new FloatingBounds(double.NaN, 0, 10, 10)));
    }

    [Fact]
    public void TW_5_6_return_to_dock_evicts_slot_occupant() // E6
    {
        var bounds = new FloatingBounds(0, 0, 100, 100);
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float, FloatingBounds = bounds },
            Window("b", LeftPrimary, 1) with { IsOpen = true });

        var result = layout.SetMode("a", ToolWindowMode.DockPinned);

        Assert.True(Get(result, "a").IsOpen);
        Assert.Equal(ToolWindowMode.DockPinned, Get(result, "a").Mode);
        Assert.False(Get(result, "b").IsOpen);
        // Bounds are kept for future floating transitions (TW-5.6).
        Assert.Equal(bounds, Get(result, "a").FloatingBounds);
    }

    [Fact]
    public void TW_5_6_records_last_internal_mode_and_returns_to_it() // E27
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            IsOpen = true,
            Mode = ToolWindowMode.DockUnpinned,
            LastInternalMode = ToolWindowMode.DockUnpinned,
        });

        var floated = layout.SetMode("a", ToolWindowMode.Float);
        Assert.Equal(ToolWindowMode.Float, Get(floated, "a").Mode);
        Assert.Equal(ToolWindowMode.DockUnpinned, Get(floated, "a").LastInternalMode);

        var returned = floated.SetMode("a", Get(floated, "a").LastInternalMode);
        Assert.Equal(ToolWindowMode.DockUnpinned, Get(returned, "a").Mode);
    }

    [Fact]
    public void TW_5_6_setmode_of_closed_window_just_changes_the_field()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftPrimary, 1));

        var result = layout.SetMode("b", ToolWindowMode.Undock);

        Assert.Equal(ToolWindowMode.Undock, Get(result, "b").Mode);
        Assert.False(Get(result, "b").IsOpen);
        Assert.True(Get(result, "a").IsOpen); // no eviction: b stayed closed
    }

    [Fact]
    public void TW_5_6_float_to_window_keeps_bounds()
    {
        var bounds = new FloatingBounds(5, 5, 50, 50);
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            IsOpen = true,
            Mode = ToolWindowMode.Float,
            FloatingBounds = bounds,
        });

        var result = layout.SetMode("a", ToolWindowMode.Window);

        Assert.Equal(ToolWindowMode.Window, Get(result, "a").Mode);
        Assert.Equal(bounds, Get(result, "a").FloatingBounds);
        Assert.Equal(ToolWindowMode.DockPinned, Get(result, "a").LastInternalMode);
    }

    // ---- TW-5.7 / TW-5.8 Move ----

    [Fact]
    public void TW_5_7_move_within_slot_reorders_densely()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0),
            Window("b", LeftPrimary, 1),
            Window("c", LeftPrimary, 2));

        var result = layout.Move("b", LeftPrimary, 0);

        Assert.Equal(0, Get(result, "b").Order);
        Assert.Equal(1, Get(result, "a").Order);
        Assert.Equal(2, Get(result, "c").Order);
    }

    [Fact]
    public void TW_5_7_move_across_slots_normalizes_both()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0),
            Window("b", LeftPrimary, 1),
            Window("c", LeftSecondary, 0));

        var result = layout.Move("b", LeftSecondary, 0);

        Assert.Equal(LeftSecondary, Get(result, "b").Slot);
        Assert.Equal(0, Get(result, "b").Order);
        Assert.Equal(1, Get(result, "c").Order);
        Assert.Equal(0, Get(result, "a").Order); // source slot compacted
    }

    [Fact]
    public void TW_5_7_moving_open_docked_window_wins_the_destination() // E4
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", RightPrimary, 0) with { IsOpen = true });

        var result = layout.Move("a", RightPrimary, 0);

        Assert.Equal(RightPrimary, Get(result, "a").Slot);
        Assert.True(Get(result, "a").IsOpen);
        Assert.False(Get(result, "b").IsOpen);
    }

    [Fact]
    public void TW_5_7_moving_floating_window_changes_only_placement() // E5
    {
        var bounds = new FloatingBounds(1, 1, 100, 100);
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float, FloatingBounds = bounds },
            Window("b", BottomSecondary, 0) with { IsOpen = true });

        var result = layout.Move("a", BottomSecondary, 0);

        var a = Get(result, "a");
        Assert.Equal(BottomSecondary, a.Slot);
        Assert.Equal(ToolWindowMode.Float, a.Mode);
        Assert.True(a.IsOpen);
        Assert.Equal(bounds, a.FloatingBounds);
        Assert.True(Get(result, "b").IsOpen); // a floats, so b is not evicted
    }

    [Fact]
    public void TW_5_7_moving_open_undock_window_evicts_only_the_overlay_occupant() // TW-5.7, INV-2
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock },
            Window("b", RightPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock },
            Window("c", RightPrimary, 1) with { IsOpen = true });

        var result = layout.Move("a", RightPrimary, 0);

        Assert.Equal(RightPrimary, Get(result, "a").Slot);
        Assert.True(Get(result, "a").IsOpen); // the mover stays open in the new slot
        Assert.False(Get(result, "b").IsOpen); // overlay occupant evicted («the mover wins»)
        Assert.True(Get(result, "c").IsOpen); // docked occupant is a different layer, untouched (INV-2)
    }

    [Fact]
    public void TW_5_7_move_index_is_clamped()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0),
            Window("b", LeftPrimary, 1));

        var result = layout.Move("a", LeftPrimary, 99);

        Assert.Equal(1, Get(result, "a").Order);
        Assert.Equal(0, Get(result, "b").Order);
    }

    [Fact]
    public void TW_5_8_move_keeps_pair_ratio()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { PairRatio = 0.7 });

        var result = layout.Move("a", BottomSecondary, 0);

        Assert.Equal(0.7, Get(result, "a").PairRatio);
    }

    // ---- TW-5.10 / TW-5.11 SetIconVisible ----

    [Fact]
    public void TW_5_10_hide_icon_closes_docked_window_and_keeps_order() // E7
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0),
            Window("a2", LeftPrimary, 1) with { IsOpen = true }) with
        { ActiveToolWindowId = "a2" };

        var result = layout.SetIconVisible("a2", false);

        var a2 = Get(result, "a2");
        Assert.False(a2.IsOpen);
        Assert.False(a2.IsIconVisible);
        Assert.Equal(1, a2.Order); // stays in the slot order (TW-5.10)
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_5_10_hide_icon_closes_windowed_tool_window() // E8
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Window });

        var result = layout.SetIconVisible("a", false);

        Assert.False(Get(result, "a").IsOpen);
        Assert.False(Get(result, "a").IsIconVisible);
        Assert.Equal(ToolWindowMode.Window, Get(result, "a").Mode);
    }

    [Fact]
    public void TW_5_11_show_icon_restores_position_after_reorder() // E21
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsIconVisible = false },
            Window("b", LeftPrimary, 1),
            Window("c", LeftPrimary, 2));

        // Neighbours are reordered while a's icon is hidden; a keeps its place in the dense order.
        var reordered = layout.Move("c", LeftPrimary, 0);
        Assert.Equal(1, Get(reordered, "a").Order);

        var shown = reordered.SetIconVisible("a", true);

        Assert.True(Get(shown, "a").IsIconVisible);
        Assert.Equal(1, Get(shown, "a").Order); // position preserved (TW-5.11)
    }

    // ---- TW-5.12 HideAll ----

    [Fact]
    public void TW_5_12_hideall_closes_internal_and_keeps_floating() // E28
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock },
            Window("c", RightPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float },
            Window("d", RightPrimary, 1) with { IsOpen = true, Mode = ToolWindowMode.Window }) with
        { ActiveToolWindowId = "a" };

        var result = layout.HideAll();

        Assert.False(Get(result, "a").IsOpen);
        Assert.False(Get(result, "b").IsOpen);
        Assert.True(Get(result, "c").IsOpen);
        Assert.True(Get(result, "d").IsOpen);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_5_12_hideall_keeps_active_floating_window()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("c", RightPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float }) with
        { ActiveToolWindowId = "c" };

        var result = layout.HideAll();

        Assert.True(Get(result, "c").IsOpen);
        Assert.Equal("c", result.ActiveToolWindowId);
    }

    // ---- TW-5.15 SetQuickAccessSide ----

    [Fact]
    public void TW_5_15_set_quick_access_side()
    {
        var result = LayoutState.Empty.SetQuickAccessSide(QuickAccessSide.Right);

        Assert.Equal(QuickAccessSide.Right, result.QuickAccessSide);
    }

    // TW-6.5: активацию документа теперь выполняет ActivateTab — тест в DockOperationsTests (DA-6.2).

    // ---- error handling ----

    [Fact]
    public void Operation_on_unknown_id_throws()
    {
        Assert.Throws<ArgumentException>(() => LayoutState.Empty.Open("ghost"));
        Assert.Throws<ArgumentException>(() => LayoutState.Empty.Close("ghost"));
        Assert.Throws<ArgumentException>(() => LayoutState.Empty.SetMode("ghost", ToolWindowMode.Float));
        Assert.Throws<ArgumentException>(() => LayoutState.Empty.Move("ghost", LeftPrimary, 0));
        Assert.Throws<ArgumentException>(() => LayoutState.Empty.SetIconVisible("ghost", false));
    }

    [Fact]
    public void Operations_keep_invariants_on_worked_examples()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("b", "B", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("c", "C", RightPrimary));

        var layout = Layout(
            Window("a", LeftPrimary, 0),
            Window("b", LeftPrimary, 1),
            Window("c", RightPrimary, 0));

        var result = layout
            .Open("a")
            .Open("b") // evicts a
            .SetMode("b", ToolWindowMode.Float)
            .Open("c")
            .Move("c", LeftPrimary, 0)
            .HideAll();

        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }
}
