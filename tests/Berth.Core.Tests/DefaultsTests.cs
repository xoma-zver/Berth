using Xunit;

namespace Berth.Core.Tests;

/// <summary>Spec TW-2.5 and TW-3.1: default fractions and modes.</summary>
public class DefaultsTests
{
    [Fact]
    public void TW_2_5_side_default_weight_is_033()
    {
        Assert.Equal(0.33, new SideState().Weight);
    }

    [Fact]
    public void TW_2_5_default_preferences_form_a_consistent_pair()
    {
        // 0.5 + 0.5 = 1: the derived pair ratio of two untaught windows is exactly 0.5 (R1).
        var layout = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), 0) with { IsOpen = true },
                new ToolWindowState("s", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary), 0) with { IsOpen = true },
            ],
        };

        Assert.Equal(0.5, layout.GetPairRatio(ToolWindowSide.Left));
    }

    [Fact]
    public void TW_3_1_new_state_has_spec_defaults()
    {
        var state = new ToolWindowState("tw", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), 0);

        Assert.Equal(ToolWindowMode.DockPinned, state.Mode);
        Assert.Equal(ToolWindowMode.DockPinned, state.LastInternalMode);
        Assert.False(state.IsOpen);
        Assert.True(state.IsIconVisible);
        Assert.Equal(0.5, state.PairRatio);
        Assert.Null(state.FloatingBounds);
    }

    [Fact]
    public void TW_3_1_state_rejects_empty_id_and_negative_order()
    {
        var slot = new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary);

        Assert.Throws<ArgumentException>(() => new ToolWindowState(" ", slot, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolWindowState("tw", slot, -1));
    }
}
