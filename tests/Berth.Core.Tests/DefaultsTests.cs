using Xunit;

namespace Berth.Core.Tests;

/// <summary>Spec TW-2.5 and TW-3.1: default fractions and modes.</summary>
public class DefaultsTests
{
    [Fact]
    public void TW_2_5_side_defaults_are_weight_033_and_ratio_05()
    {
        var side = new SideState();

        Assert.Equal(0.33, side.Weight);
        Assert.Equal(0.5, side.CurrentRatio);
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
        Assert.Equal(0.33, state.UndockWeight);
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
