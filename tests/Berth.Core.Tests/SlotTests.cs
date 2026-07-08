using Xunit;

namespace Berth.Core.Tests;

/// <summary>Spec TW-1.1: exactly six slots.</summary>
public class SlotTests
{
    [Fact]
    public void TW_1_1_there_are_exactly_six_slots()
    {
        var expected = new[]
        {
            new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary),
            new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary),
            new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary),
            new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Secondary),
            new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary),
            new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Secondary),
        };

        Assert.Equal(expected, ToolWindowSlot.All);
    }

    [Fact]
    public void TW_1_1_slots_cover_every_side_group_combination()
    {
        var combinations =
            from side in Enum.GetValues<ToolWindowSide>()
            from half in Enum.GetValues<ToolWindowGroup>()
            select new ToolWindowSlot(side, half);

        Assert.Equal(combinations.ToHashSet(), ToolWindowSlot.All.ToHashSet());
    }
}
