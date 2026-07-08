using Xunit;

namespace Berth.Core.Tests;

/// <summary>Spec TW-3.2: mode classification (internal vs floating, slot layers).</summary>
public class ModeTests
{
    [Theory]
    [InlineData(ToolWindowMode.DockPinned, true)]
    [InlineData(ToolWindowMode.DockUnpinned, true)]
    [InlineData(ToolWindowMode.Undock, true)]
    [InlineData(ToolWindowMode.Float, false)]
    [InlineData(ToolWindowMode.Window, false)]
    public void TW_3_2_internal_modes_are_the_dock_and_undock_ones(ToolWindowMode mode, bool isInternal)
    {
        Assert.Equal(isInternal, mode.IsInternal());
    }

    [Theory]
    [InlineData(ToolWindowMode.DockPinned, ToolWindowLayer.Docked)]
    [InlineData(ToolWindowMode.DockUnpinned, ToolWindowLayer.Docked)]
    [InlineData(ToolWindowMode.Undock, ToolWindowLayer.Overlay)]
    [InlineData(ToolWindowMode.Float, ToolWindowLayer.Floating)]
    [InlineData(ToolWindowMode.Window, ToolWindowLayer.Floating)]
    public void TW_3_2_modes_map_to_slot_layers(ToolWindowMode mode, ToolWindowLayer layer)
    {
        Assert.Equal(layer, mode.GetLayer());
    }
}
