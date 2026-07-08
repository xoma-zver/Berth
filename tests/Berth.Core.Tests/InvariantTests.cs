using Xunit;

namespace Berth.Core.Tests;

/// <summary>Layout invariants INV-1…INV-7 (spec section 4): valid layouts pass, each violation is reported with its id.</summary>
public class InvariantTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot LeftSecondary = new(ToolWindowSide.Left, ToolWindowGroup.Secondary);

    private static ToolWindowState Window(string id, ToolWindowSlot slot, int order) => new(id, slot, order);

    private static LayoutState Layout(params ToolWindowState[] windows) =>
        LayoutState.Empty with { ToolWindows = [.. windows] };

    private static string[] ViolatedInvariants(LayoutState state, ToolWindowRegistry? registry = null) =>
        LayoutInvariants.Validate(state, registry ?? new ToolWindowRegistry())
            .Select(v => v.InvariantId)
            .Distinct()
            .ToArray();

    [Fact]
    public void Valid_layout_produces_no_violations()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("b", "B", LeftPrimary));

        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftPrimary, 1) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock },
            Window("c", LeftSecondary, 0) with { Mode = ToolWindowMode.Float });

        Assert.Empty(LayoutInvariants.Validate(layout, registry));
    }

    [Fact]
    public void INV_1_duplicate_state_id_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0), Window("a", LeftSecondary, 0));

        Assert.Equal(["INV-1"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_1_registered_window_without_state_is_reported()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));

        Assert.Equal(["INV-1"], ViolatedInvariants(LayoutState.Empty, registry));
    }

    [Fact]
    public void INV_1_sleeping_state_without_descriptor_is_legal()
    {
        // TW-10.2: a state whose id has no registered descriptor "sleeps" and is not a violation.
        var layout = Layout(Window("sleeper", LeftPrimary, 0));

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_2_two_open_docked_in_one_slot_are_reported()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.DockPinned },
            Window("b", LeftPrimary, 1) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.DockUnpinned,
                LastInternalMode = ToolWindowMode.DockUnpinned,
            });

        Assert.Equal(["INV-2"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_2_open_undock_coexists_with_open_docked_in_one_slot()
    {
        // The docked and overlay layers of a slot are independent (E16).
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftPrimary, 1) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
            });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_2_two_open_undock_in_one_slot_are_reported()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock },
            Window("b", LeftPrimary, 1) with { IsOpen = true, Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock });

        Assert.Equal(["INV-2"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_2_open_floating_windows_are_unbounded()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float },
            Window("b", LeftPrimary, 1) with { IsOpen = true, Mode = ToolWindowMode.Window },
            Window("c", LeftPrimary, 2) with { IsOpen = true, Mode = ToolWindowMode.Float });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_3_order_gap_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0), Window("b", LeftPrimary, 2));

        Assert.Equal(["INV-3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_3_duplicate_order_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0), Window("b", LeftPrimary, 0));

        Assert.Equal(["INV-3"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_3_density_counts_closed_and_hidden_windows_too()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsIconVisible = false },
            Window("b", LeftPrimary, 1) with { IsOpen = true },
            Window("c", LeftPrimary, 2));

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_3_slots_are_numbered_independently()
    {
        var layout = Layout(Window("a", LeftPrimary, 0), Window("b", LeftSecondary, 0));

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void INV_4_out_of_range_side_weight_is_reported(double weight)
    {
        var layout = LayoutState.Empty with { Left = new SideState(Weight: weight) };

        Assert.Equal(["INV-4"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_4_out_of_range_pair_ratio_and_undock_weight_are_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { PairRatio = 1.0, UndockWeight = 0.0 });

        var violations = LayoutInvariants.Validate(layout, new ToolWindowRegistry());
        Assert.Equal(2, violations.Count(v => v.InvariantId == "INV-4"));
        Assert.All(violations, v => Assert.Equal("a", v.ToolWindowId));
    }

    [Fact]
    public void INV_5_active_id_of_unknown_window_is_reported()
    {
        var layout = LayoutState.Empty with { ActiveToolWindowId = "ghost" };

        Assert.Equal(["INV-5"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_5_active_id_of_closed_window_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0)) with { ActiveToolWindowId = "a" };

        Assert.Equal(["INV-5"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_5_active_open_window_and_null_are_valid()
    {
        var open = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true }) with { ActiveToolWindowId = "a" };

        Assert.Empty(LayoutInvariants.Validate(open, new ToolWindowRegistry()));
        Assert.Empty(LayoutInvariants.Validate(LayoutState.Empty, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_6_open_window_with_hidden_icon_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true, IsIconVisible = false });

        Assert.Equal(["INV-6"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_6_closed_window_may_hide_its_icon()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsIconVisible = false });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }

    [Fact]
    public void INV_7_internal_mode_must_equal_last_internal_mode()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            Mode = ToolWindowMode.DockUnpinned,
            LastInternalMode = ToolWindowMode.DockPinned,
        });

        Assert.Equal(["INV-7"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_7_non_internal_last_internal_mode_is_reported()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            Mode = ToolWindowMode.Float,
            LastInternalMode = ToolWindowMode.Window,
        });

        Assert.Equal(["INV-7"], ViolatedInvariants(layout));
    }

    [Fact]
    public void INV_7_floating_mode_keeps_the_return_target()
    {
        // While floating, LastInternalMode legitimately differs from Mode (E27).
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            Mode = ToolWindowMode.Float,
            LastInternalMode = ToolWindowMode.DockUnpinned,
        });

        Assert.Empty(LayoutInvariants.Validate(layout, new ToolWindowRegistry()));
    }
}
