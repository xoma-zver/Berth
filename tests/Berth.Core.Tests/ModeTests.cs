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

    // Full degradation table of TW-7.6: Window → Float without windowed hosting, further →
    // Undock without floating; internal modes never degrade, a full platform changes nothing.
    [Theory]
    // full platform: identity for every mode
    [InlineData(ToolWindowMode.DockPinned, true, true, ToolWindowMode.DockPinned)]
    [InlineData(ToolWindowMode.DockUnpinned, true, true, ToolWindowMode.DockUnpinned)]
    [InlineData(ToolWindowMode.Undock, true, true, ToolWindowMode.Undock)]
    [InlineData(ToolWindowMode.Float, true, true, ToolWindowMode.Float)]
    [InlineData(ToolWindowMode.Window, true, true, ToolWindowMode.Window)]
    // browser: no windowed hosting, floating via overlay pseudo-windows (TW-7.7)
    [InlineData(ToolWindowMode.DockPinned, true, false, ToolWindowMode.DockPinned)]
    [InlineData(ToolWindowMode.DockUnpinned, true, false, ToolWindowMode.DockUnpinned)]
    [InlineData(ToolWindowMode.Undock, true, false, ToolWindowMode.Undock)]
    [InlineData(ToolWindowMode.Float, true, false, ToolWindowMode.Float)]
    [InlineData(ToolWindowMode.Window, true, false, ToolWindowMode.Float)]
    // no floating at all: both floating modes collapse into the overlay layer
    [InlineData(ToolWindowMode.DockPinned, false, false, ToolWindowMode.DockPinned)]
    [InlineData(ToolWindowMode.DockUnpinned, false, false, ToolWindowMode.DockUnpinned)]
    [InlineData(ToolWindowMode.Undock, false, false, ToolWindowMode.Undock)]
    [InlineData(ToolWindowMode.Float, false, false, ToolWindowMode.Undock)]
    [InlineData(ToolWindowMode.Window, false, false, ToolWindowMode.Undock)]
    // degenerate capability set (windowed without floating): Window survives, Float degrades
    [InlineData(ToolWindowMode.Float, false, true, ToolWindowMode.Undock)]
    [InlineData(ToolWindowMode.Window, false, true, ToolWindowMode.Window)]
    public void TW_7_6_effective_mode_degrades_by_capabilities(
        ToolWindowMode stored, bool canFloat, bool canUseWindowed, ToolWindowMode effective)
    {
        Assert.Equal(effective, stored.GetEffectiveMode(canFloat, canUseWindowed));
    }
}
