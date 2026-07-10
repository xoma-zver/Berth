using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Claim-based tab ownership (spec TW-9.11, TW-9.7, DA-1): resolution to the dock area, a tool
/// window or a sleeping owner; the resolve-time uniqueness contract; claims disappearing with
/// unregistration.
/// </summary>
public class TabOwnershipTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);

    [Fact]
    public void TW_9_11_dock_claim_resolves_to_the_dock_area()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc:"));

        var owner = registry.ResolveTabOwner("doc:a");

        Assert.Equal(TabOwner.DockArea, owner);
        Assert.True(owner!.Value.IsDockArea);
        Assert.Null(owner.Value.ToolWindowId);
    }

    [Fact]
    public void TW_9_11_panel_claim_resolves_to_the_tool_window()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"),
        });

        var owner = registry.ResolveTabOwner("p:t1");

        Assert.Equal(TabOwner.ToolWindow("p"), owner);
        Assert.False(owner!.Value.IsDockArea);
        Assert.Equal("p", owner.Value.ToolWindowId);
    }

    [Fact]
    public void TW_9_11_unclaimed_id_has_a_sleeping_owner()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc:"));

        Assert.Null(registry.ResolveTabOwner("elsewhere"));
    }

    [Fact]
    public void TW_9_11_panel_without_a_tab_factory_claims_nothing()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary));

        // Регистрация панели без TabFactory владения не даёт — вкладки владельца спят дальше.
        Assert.Null(registry.ResolveTabOwner("p:t1"));
    }

    [Fact]
    public void TW_9_11_conflicting_claims_throw_at_resolve()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("x"));
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("x"),
        });

        // Конфликт — ошибка приложения, обнаруживаемая при резолве конкретного id (TW-9.11)…
        Assert.Throws<InvalidOperationException>(() => registry.ResolveTabOwner("x1"));
        // …а неконфликтные id того же реестра резолвятся штатно.
        Assert.Null(registry.ResolveTabOwner("y1"));
    }

    [Fact]
    public void TW_9_11_two_dock_registrations_with_disjoint_claims_coexist()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("a:"));
        registry.RegisterDockContent(new StubTabFactory("b:"));

        Assert.Equal(TabOwner.DockArea, registry.ResolveTabOwner("a:1"));
        Assert.Equal(TabOwner.DockArea, registry.ResolveTabOwner("b:1"));
    }

    [Fact]
    public void TW_9_11_overlapping_dock_claims_throw_at_resolve()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("d:"));
        registry.RegisterDockContent(new StubTabFactory("d:"));

        Assert.Throws<InvalidOperationException>(() => registry.ResolveTabOwner("d:1"));
    }

    [Fact]
    public void TW_9_11_unregistration_removes_the_claim()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"),
        });

        registry.Unregister("p");

        Assert.Null(registry.ResolveTabOwner("p:t1"));
        Assert.Empty(registry.Descriptors);
    }

    [Fact]
    public void TW_9_4_unregister_of_an_unknown_id_throws()
    {
        Assert.Throws<ArgumentException>(() => new ToolWindowRegistry().Unregister("ghost"));
    }

    // ---- implicit body claims (TW-9.5, задача 1.8) ----

    [Fact]
    public void TW_9_5_registration_with_a_body_factory_claims_its_own_id()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
        });

        Assert.Equal(TabOwner.ToolWindow("p"), registry.ResolveTabOwner("p"));
        Assert.Null(registry.ResolveTabOwner("p2")); // заявка — только на сам id
    }

    [Fact]
    public void TW_9_5_registration_without_a_body_factory_claims_no_body()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary));

        Assert.Null(registry.ResolveTabOwner("p"));
    }

    [Fact]
    public void TW_9_11_claims_of_one_registration_unite_without_a_conflict()
    {
        // Неявная заявка тела и собственный предикат TabFactory накрывают один id —
        // конфликт считается только между разными регистрациями (TW-9.11).
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
            TabFactory = new StubTabFactory("p"), // заявляет и сам id "p"
        });

        Assert.Equal(TabOwner.ToolWindow("p"), registry.ResolveTabOwner("p"));
        Assert.Equal(TabOwner.ToolWindow("p"), registry.ResolveTabOwner("p:t1"));
    }

    [Fact]
    public void TW_9_11_foreign_claim_on_a_body_id_conflicts()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("p"));
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
        });

        Assert.Throws<InvalidOperationException>(() => registry.ResolveTabOwner("p"));
    }
}
