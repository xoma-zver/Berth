using Xunit;

namespace Berth.Core.Tests;

/// <summary>The quick access «⋯» list (spec TW-8.2): membership and ordering.</summary>
public class QuickAccessTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);

    private static ToolWindowState Window(string id, bool iconVisible) =>
        new ToolWindowState(id, LeftPrimary, 0) with { IsIconVisible = iconVisible };

    [Fact]
    public void TW_8_2_lists_registered_windows_with_hidden_icons_only()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("hidden", "Hidden", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("visible", "Visible", LeftPrimary));
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Window("hidden", iconVisible: false),
                Window("visible", iconVisible: true),
                Window("sleeper", iconVisible: false), // спит: без регистрации нет Title (ADR-0003)
            ],
        };

        var list = QuickAccess.List(state, registry);

        Assert.Equal(["hidden"], list.Select(d => d.Id));
    }

    [Fact]
    public void TW_8_2_sorts_by_title_case_insensitively_with_id_tie_break()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("b", "beta", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("a2", "Alpha", LeftPrimary));
        registry.Register(new ToolWindowDescriptor("a1", "Alpha", LeftPrimary));
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Window("b", iconVisible: false),
                Window("a2", iconVisible: false),
                Window("a1", iconVisible: false),
            ],
        };

        var list = QuickAccess.List(state, registry);

        // «beta» после «Alpha» несмотря на регистр; равные Title — по Id.
        Assert.Equal(["a1", "a2", "b"], list.Select(d => d.Id));
    }

    [Fact]
    public void TW_8_2_registered_window_without_a_state_is_not_listed()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("a", "A", LeftPrimary));

        Assert.Empty(QuickAccess.List(LayoutState.Empty, registry));
    }
}
