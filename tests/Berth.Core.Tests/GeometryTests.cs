using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Workspace geometry: side weight inheritance (TW-2.6), the pair ratio rules R1–R3 (TW-2.7) and the
/// resize commands SetSideSize/SetSideRatio/SetUndockWeight/SetFloatingBounds (TW-5.9). The render-time
/// min-size clamp (TW-2.8) is [UI] and out of core scope.
/// </summary>
public class GeometryTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot LeftSecondary = new(ToolWindowSide.Left, ToolWindowGroup.Secondary);
    private static readonly ToolWindowSlot RightPrimary = new(ToolWindowSide.Right, ToolWindowGroup.Primary);

    private static ToolWindowState Window(string id, ToolWindowSlot slot, int order) => new(id, slot, order);

    private static LayoutState Layout(params ToolWindowState[] windows) =>
        LayoutState.Empty with { ToolWindows = [.. windows] };

    private static ToolWindowState Get(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    // ---- TW-2.7 R1: open into a pair sets CurrentRatio from the opened window's preference ----

    [Fact]
    public void TW_2_7_R1_open_into_pair_sets_current_ratio_from_primary_preference() // E19
    {
        // The side pair is open (P in Primary, S in Secondary); X is opened into the Primary group.
        var layout = Layout(
            Window("p", LeftPrimary, 0) with { IsOpen = true },
            Window("x", LeftPrimary, 1) with { PairRatio = 0.7 },
            Window("s", LeftSecondary, 0) with { IsOpen = true });

        var result = layout.Open("x");

        Assert.False(Get(result, "p").IsOpen); // X evicts the Primary occupant
        Assert.Equal(0.7, result.Left.CurrentRatio); // pair takes X's preference (R1)
    }

    [Fact]
    public void TW_2_7_R1_open_secondary_into_pair_maps_preference_to_primary_share()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("x", LeftSecondary, 0) with { PairRatio = 0.75 });

        var result = layout.Open("x");

        // X is Secondary with own share 0.75 → Primary share = 0.25.
        Assert.Equal(0.25, result.Left.CurrentRatio);
    }

    [Fact]
    public void TW_2_7_R4_single_docked_open_leaves_current_ratio_dormant()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { PairRatio = 0.7 });

        var result = layout.Open("a");

        Assert.Equal(LayoutDefaults.CurrentRatio, result.Left.CurrentRatio); // no neighbour → dormant
    }

    [Fact]
    public void TW_2_7_R1_overlay_open_does_not_form_a_pair()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("x", LeftSecondary, 0) with
            {
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
                PairRatio = 0.7,
            });

        var result = layout.Open("x");

        Assert.Equal(LayoutDefaults.CurrentRatio, result.Left.CurrentRatio); // overlay does not participate (TW-3.3)
        Assert.True(Get(result, "a").IsOpen); // and a different layer is not evicted
    }

    [Fact]
    public void TW_2_7_R1_docked_open_with_only_overlay_neighbour_stays_dormant()
    {
        var layout = Layout(
            Window("x", LeftPrimary, 0) with { PairRatio = 0.7 },
            Window("b", LeftSecondary, 0) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
            });

        var result = layout.Open("x");

        Assert.Equal(LayoutDefaults.CurrentRatio, result.Left.CurrentRatio); // neighbour is overlay, not a docked pair
    }

    // ---- TW-2.7 R3: close from a pair keeps CurrentRatio ----

    [Fact]
    public void TW_2_7_R3_close_from_pair_keeps_current_ratio()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true })
            with { Left = new SideState(CurrentRatio: 0.75) };

        var result = layout.Close("b");

        Assert.Equal(0.75, result.Left.CurrentRatio);
    }

    // ---- TW-2.6 / TW-5.9: SetSideSize and width inheritance ----

    [Fact]
    public void TW_5_9_set_side_size_writes_weight_only()
    {
        var result = LayoutState.Empty.SetSideSize(ToolWindowSide.Bottom, 0.4);

        Assert.Equal(0.4, result.Bottom.Weight);
        Assert.Equal(LayoutDefaults.CurrentRatio, result.Bottom.CurrentRatio); // ratio untouched
        Assert.Equal(LayoutDefaults.SideWeight, result.Left.Weight); // other sides untouched
    }

    [Fact]
    public void TW_2_6_open_inherits_side_width_after_resize_and_close() // E18
    {
        var stretched = Layout(
                Window("a", LeftPrimary, 0) with { IsOpen = true },
                Window("b", LeftPrimary, 1))
            .SetSideSize(ToolWindowSide.Left, 0.6);

        var reopened = stretched.Close("a").Open("b");

        Assert.Equal(0.6, reopened.Left.Weight); // B opens in the inherited width (TW-2.6)
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void TW_5_9_set_side_size_rejects_out_of_range(double weight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutState.Empty.SetSideSize(ToolWindowSide.Left, weight));
    }

    // ---- TW-2.7 R2 / TW-5.9: SetSideRatio teaches both ----

    [Fact]
    public void TW_2_7_R2_set_side_ratio_sets_current_ratio_and_teaches_both()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true });

        var result = layout.SetSideRatio(ToolWindowSide.Left, 0.75);

        Assert.Equal(0.75, result.Left.CurrentRatio);
        Assert.Equal(0.75, Get(result, "a").PairRatio); // Primary learns p
        Assert.Equal(0.25, Get(result, "b").PairRatio); // Secondary learns 1 − p (INV-4: pair sums to 1)
    }

    [Fact]
    public void TW_2_7_R2_set_side_ratio_ignores_overlay_and_other_sides()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with
            {
                IsOpen = true,
                Mode = ToolWindowMode.Undock,
                LastInternalMode = ToolWindowMode.Undock,
            },
            Window("b", RightPrimary, 0) with { IsOpen = true });

        var result = layout.SetSideRatio(ToolWindowSide.Left, 0.75);

        Assert.Equal(LayoutDefaults.PairRatio, Get(result, "a").PairRatio); // overlay panel not taught
        Assert.Equal(LayoutDefaults.PairRatio, Get(result, "b").PairRatio); // other side not taught
        Assert.Equal(LayoutDefaults.CurrentRatio, result.Right.CurrentRatio); // right side untouched
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.2)]
    [InlineData(2.0)]
    public void TW_5_9_set_side_ratio_rejects_out_of_range(double primaryShare)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutState.Empty.SetSideRatio(ToolWindowSide.Left, primaryShare));
    }

    // ---- TW-5.9: SetUndockWeight ----

    [Fact]
    public void TW_5_9_set_undock_weight_writes_thickness_only()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with
        {
            Mode = ToolWindowMode.Undock,
            LastInternalMode = ToolWindowMode.Undock,
        });

        var result = layout.SetUndockWeight("a", 0.4);

        Assert.Equal(0.4, Get(result, "a").UndockWeight);
        Assert.Equal(LayoutDefaults.SideWeight, result.Left.Weight); // side geometry untouched
    }

    [Fact]
    public void TW_5_9_set_undock_weight_validates_range_and_id()
    {
        var layout = Layout(Window("a", LeftPrimary, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SetUndockWeight("a", 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SetUndockWeight("a", 1.0));
        Assert.Throws<ArgumentException>(() => layout.SetUndockWeight("ghost", 0.4));
    }

    // ---- TW-5.9: SetFloatingBounds ----

    [Fact]
    public void TW_5_9_set_floating_bounds_writes_bounds()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { IsOpen = true, Mode = ToolWindowMode.Float });
        var bounds = new FloatingBounds(100, 200, 400, 300);

        var result = layout.SetFloatingBounds("a", bounds);

        Assert.Equal(bounds, Get(result, "a").FloatingBounds);
    }

    [Fact]
    public void TW_5_9_set_floating_bounds_of_unknown_id_throws()
    {
        Assert.Throws<ArgumentException>(
            () => LayoutState.Empty.SetFloatingBounds("ghost", new FloatingBounds(0, 0, 1, 1)));
    }

    [Theory]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, double.PositiveInfinity, 10, 10)]
    [InlineData(0, 0, double.NegativeInfinity, 10)]
    [InlineData(0, 0, 10, double.NaN)]
    public void TW_5_9_set_floating_bounds_rejects_non_finite_components(
        double x, double y, double width, double height)
    {
        // A non-finite component is a caller error: it would make the state unserializable (TW-10.1).
        var layout = Layout(Window("a", LeftPrimary, 0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.SetFloatingBounds("a", new FloatingBounds(x, y, width, height)));
    }

    // ---- invariants preserved across a geometry sequence ----

    [Fact]
    public void Geometry_operations_keep_invariants()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("b", "B", LeftSecondary));

        var result = Layout(
                Window("a", LeftPrimary, 0),
                Window("b", LeftSecondary, 0) with { PairRatio = 0.7 })
            .Open("a")
            .Open("b") // R1 sets CurrentRatio from b's preference
            .SetSideSize(ToolWindowSide.Left, 0.5)
            .SetSideRatio(ToolWindowSide.Left, 0.6)
            .SetUndockWeight("a", 0.4);

        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }
}
