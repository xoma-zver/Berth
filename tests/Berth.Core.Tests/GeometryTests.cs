using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Workspace geometry: side weight inheritance (TW-2.6), the pair ratio rules R1–R4 (TW-2.7)
/// and the resize commands SetSideSize/SetSideRatio/SetFloatingBounds (TW-5.9). R1 is a
/// derived value — the normalization of the pair's preferences — so the tests read it through
/// GetPairRatio instead of asserting stored state. The render-time min-size clamp (TW-2.8) is
/// [UI] and out of core scope.
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

    // ---- TW-2.7 R1: the pair ratio derives from the normalized preferences ----

    [Fact]
    public void TW_2_7_R1_consistent_pair_degenerates_to_the_primary_preference() // E19
    {
        // The pair was taught together (preferences sum to 1): «Primary диктует» exactly.
        var layout = Layout(
            Window("p", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.7 },
            Window("s", LeftSecondary, 0) with { IsOpen = true, PairRatio = 0.3 });

        Assert.Equal(0.7, layout.GetPairRatio(ToolWindowSide.Left));
    }

    [Fact]
    public void TW_2_7_R1_inconsistent_pair_is_normalized()
    {
        // A learned Primary meets a default newcomer: the arrangement is blended, not nuked
        // to the newcomer's 0.5 and not frozen at the incumbent's 0.7.
        var layout = Layout(
            Window("p", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.7 },
            Window("s", LeftSecondary, 0) with { IsOpen = true, PairRatio = 0.5 });

        Assert.Equal(0.7 / 1.2, layout.GetPairRatio(ToolWindowSide.Left)!.Value, precision: 12);
    }

    [Fact]
    public void TW_2_7_R1_pair_ratio_is_independent_of_the_arrival_order()
    {
        var closedPair = Layout(
            Window("p", LeftPrimary, 0) with { PairRatio = 0.7 },
            Window("s", LeftSecondary, 0) with { PairRatio = 0.5 });

        var primaryLast = closedPair.Open("s").Open("p");
        var secondaryLast = closedPair.Open("p").Open("s");

        Assert.Equal(
            primaryLast.GetPairRatio(ToolWindowSide.Left),
            secondaryLast.GetPairRatio(ToolWindowSide.Left));
    }

    [Fact]
    public void TW_2_7_R1_applies_to_a_pair_formed_by_move() // TW-5.8
    {
        // An open panel moved into the pair participates immediately: no «open vs move» gap.
        var layout = Layout(
            Window("p", LeftPrimary, 0) with { IsOpen = true, PairRatio = 0.6 },
            Window("x", RightPrimary, 0) with { IsOpen = true, PairRatio = 0.6 });

        var moved = layout.Move("x", LeftSecondary, 0);

        Assert.Equal(0.5, moved.GetPairRatio(ToolWindowSide.Left)!.Value, precision: 12);
    }

    [Fact]
    public void TW_2_7_R4_single_docked_open_has_no_pair_ratio()
    {
        var layout = Layout(Window("a", LeftPrimary, 0) with { PairRatio = 0.7 });

        Assert.Null(layout.Open("a").GetPairRatio(ToolWindowSide.Left)); // no neighbour → R4
    }

    [Fact]
    public void TW_2_7_R1_overlay_neighbour_does_not_form_a_pair()
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

        Assert.Null(result.GetPairRatio(ToolWindowSide.Left)); // overlay does not participate (TW-3.3)
        Assert.True(Get(result, "a").IsOpen); // and a different layer is not evicted
    }

    // ---- TW-2.7 R3: close from a pair keeps the preferences ----

    [Fact]
    public void TW_2_7_R3_close_and_reopen_restore_the_pair_ratio()
    {
        var pair = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true })
            .SetSideRatio(ToolWindowSide.Left, 0.75);

        var closed = pair.Close("b");
        Assert.Null(closed.GetPairRatio(ToolWindowSide.Left)); // the survivor takes the whole side
        Assert.Equal(0.75, Get(closed, "a").PairRatio); // preferences untouched (R3)
        Assert.Equal(0.25, Get(closed, "b").PairRatio);

        Assert.Equal(0.75, closed.Open("b").GetPairRatio(ToolWindowSide.Left)!.Value, precision: 12);
    }

    // ---- TW-2.6 / TW-5.9: SetSideSize and width inheritance ----

    [Fact]
    public void TW_5_9_set_side_size_writes_weight_only()
    {
        var result = LayoutState.Empty.SetSideSize(ToolWindowSide.Bottom, 0.4);

        Assert.Equal(0.4, result.Bottom.Weight);
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
    public void TW_2_7_R2_set_side_ratio_teaches_both_and_r1_reproduces_it()
    {
        var layout = Layout(
            Window("a", LeftPrimary, 0) with { IsOpen = true },
            Window("b", LeftSecondary, 0) with { IsOpen = true });

        var result = layout.SetSideRatio(ToolWindowSide.Left, 0.75);

        Assert.Equal(0.75, Get(result, "a").PairRatio); // Primary learns p
        Assert.Equal(0.25, Get(result, "b").PairRatio); // Secondary learns 1 − p (INV-4: pair sums to 1)
        Assert.Equal(0.75, result.GetPairRatio(ToolWindowSide.Left)!.Value, precision: 12); // R1 reproduces the drag
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
            .Open("b")
            .SetSideSize(ToolWindowSide.Left, 0.5)
            .SetSideRatio(ToolWindowSide.Left, 0.6);

        Assert.Empty(LayoutInvariants.Validate(result, registry));
        Assert.Equal(0.6, result.GetPairRatio(ToolWindowSide.Left)!.Value, precision: 12);
    }
}
