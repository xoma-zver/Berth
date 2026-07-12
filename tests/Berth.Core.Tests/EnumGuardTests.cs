using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Enum-domain guard in programmatic core inputs (spec tool-windows, section 5): an
/// out-of-domain enum value — one cast past its defined members — at a command or a
/// state/descriptor constructor is a caller error (<see cref="ArgumentOutOfRangeException"/>),
/// the same precedent as the finite-bounds guard of TW-5.9. Without it the value survives to
/// serialization and breaks <see cref="LayoutPersistence.Serialize"/> on a state produced by a
/// regular operation; the guard rejects it at the input, so no normal operation can produce
/// such a state. The file path is protected by <see cref="LayoutPersistence.Deserialize"/>
/// (TW-10.5); values injected via <c>with</c> bypass these entry points and stay caller error.
/// </summary>
public class EnumGuardTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);

    private const ToolWindowSide BadSide = (ToolWindowSide)99;
    private const ToolWindowGroup BadGroup = (ToolWindowGroup)99;
    private const ToolWindowMode BadMode = (ToolWindowMode)99;
    private const QuickAccessSide BadQuickAccess = (QuickAccessSide)99;

    private static LayoutState OneWindow() => LayoutState.Empty with
    {
        ToolWindows = [new ToolWindowState("a", LeftPrimary, 0) with { IsOpen = true }],
    };

    // ---- commands ----

    [Fact]
    public void TW_5_7_move_rejects_out_of_domain_slot_members()
    {
        // Without the guard the bad slot lands in a ToolWindowState and Serialize throws on
        // SideName/ModeName — the state is produced by a normal operation but unserializable.
        var state = OneWindow();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.Move("a", new ToolWindowSlot(BadSide, ToolWindowGroup.Primary), 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.Move("a", new ToolWindowSlot(ToolWindowSide.Left, BadGroup), 0));
    }

    [Fact]
    public void TW_5_6_set_mode_rejects_out_of_domain_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OneWindow().SetMode("a", BadMode));
    }

    [Fact]
    public void TW_5_9_side_resizes_reject_out_of_domain_side()
    {
        var state = OneWindow();

        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetSideSize(BadSide, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetSideRatio(BadSide, 0.5));
    }

    [Fact]
    public void TW_5_15_set_quick_access_side_rejects_out_of_domain_side()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutState.Empty.SetQuickAccessSide(BadQuickAccess));
    }

    // ---- constructors (дескриптор и конструированные снапшоты) ----

    [Fact]
    public void Section_5_state_constructor_rejects_out_of_domain_slot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToolWindowState("a", new ToolWindowSlot(BadSide, ToolWindowGroup.Primary), 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToolWindowState("a", new ToolWindowSlot(ToolWindowSide.Left, BadGroup), 0));
    }

    [Fact]
    public void Section_5_descriptor_constructor_rejects_out_of_domain_slot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToolWindowDescriptor("a", "A", new ToolWindowSlot(BadSide, ToolWindowGroup.Primary)));
    }

    // ---- valid inputs are untouched ----

    [Fact]
    public void Valid_enum_inputs_produce_a_serializable_state()
    {
        // The guard rejects only out-of-domain values; a valid command sequence still works and
        // its result round-trips through persistence.
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));
        var state = OneWindow()
            .Move("a", new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Secondary), 0)
            .SetMode("a", ToolWindowMode.Undock)
            .SetSideSize(ToolWindowSide.Bottom, 0.4)
            .SetQuickAccessSide(QuickAccessSide.Right);

        Assert.Empty(LayoutInvariants.Validate(state, registry));
        var result = LayoutState.Empty.Apply(
            LayoutPersistence.Deserialize(LayoutPersistence.Serialize(state)), ApplyScope.Full, registry);
        Assert.Empty(result.Fixes);
    }
}
