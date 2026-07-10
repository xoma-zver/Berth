using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// The body tab and its lifecycle (spec TW-9.5, TW-9.2, TW-10.3; DA-E35, DA-E37, DA-E38):
/// seeding at registration, ResetToDefaults and Apply; the factory bridge sharing one content
/// object; retention of a body living in a dock host; the INV-D5 relocation at live
/// registration; moves across the panel ↔ dock boundary never touching content (DA-5.4).
/// </summary>
public class PanelLifecycleTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static ToolWindowState Panel(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabGroupNode AssertGroup(TabTreeNode node, string? active, params string[] tabs)
    {
        var group = Assert.IsType<TabGroupNode>(node);
        Assert.Equal(tabs, group.Tabs);
        Assert.Equal(active, group.ActiveTabId);
        return group;
    }

    // ---- TW-9.5 seeding ----

    [Fact]
    public void TW_9_5_registration_seeds_the_body_without_materializing()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);

        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = factory,
        });

        AssertGroup(Panel(state, "p").ContentTree, "p", "p");
        Assert.Equal(0, factory.Created); // посев — состояние, не материализация (TW-9.3)
        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }

    [Fact]
    public void TW_9_5_panel_without_a_body_factory_gets_no_body()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);

        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary));

        AssertGroup(Panel(state, "p").ContentTree, null);
    }

    [Fact]
    public void TW_9_5_non_empty_tree_without_the_body_is_not_reseeded()
    {
        // Пользователь закрыл тело осознанно, оставив другие вкладки, — посев его не воскрешает.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var sleeping = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("p", LeftPrimary, 0) with { ContentTree = Group("p:t1") }],
        };

        var state = lifecycle.Register(sleeping, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
            TabFactory = new StubTabFactory("p:"),
        });

        AssertGroup(Panel(state, "p").ContentTree, "p:t1", "p:t1");
    }

    [Fact]
    public void TW_9_5_body_living_in_a_dock_host_cancels_the_seed()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var factory = new StubToolWindowFactory();
        var sleeping = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("p", LeftPrimary, 0)],
            DockArea = new DockAreaState { Root = Group("p"), CurrentTabId = "p" },
        };

        var state = lifecycle.Register(sleeping, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = factory,
        });

        AssertGroup(Panel(state, "p").ContentTree, null); // INV-D2: второй копии не бывает
        // Тело в доке материализуется мостом (TW-9.5) — тем же объектом, что и GetOrCreate.
        var materialized = lifecycle.MaterializeTab(state, "p");
        Assert.Equal(TabMaterializationKind.Materialized, materialized.Kind);
        Assert.Same(materialized.Content, lifecycle.GetOrCreateToolWindowContent("p"));
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public void TW_9_5_reset_to_defaults_seeds_bodies()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
        });
        registry.Register(new ToolWindowDescriptor("plain", "Plain", LeftPrimary));

        var state = LayoutApply.ResetToDefaults(registry);

        AssertGroup(Panel(state, "p").ContentTree, "p", "p");
        AssertGroup(Panel(state, "plain").ContentTree, null);
        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }

    [Fact]
    public void TW_9_5_apply_full_seeds_without_a_report()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
        });
        var snapshot = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("p", LeftPrimary, 0)], // сохранёнка без дерева
        };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        AssertGroup(Panel(result.State, "p").ContentTree, "p", "p");
        Assert.Empty(result.Fixes); // посев — примирение, не починка
    }

    [Fact]
    public void TW_10_6_arrangement_does_not_seed()
    {
        // Закрытое в живой сессии тело не воскресает до следующего Full-примирения (TW-9.5).
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = new StubToolWindowFactory(),
        });
        var current = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("p", LeftPrimary, 0)], // тело закрыто в сессии
        };
        var macro = LayoutState.Empty with
        {
            ToolWindows = [new ToolWindowState("p", LeftPrimary, 0)],
        };

        var result = current.Apply(macro, ApplyScope.Arrangement, registry);

        AssertGroup(Panel(result.State, "p").ContentTree, null);
        Assert.Empty(result.Fixes);
    }

    // ---- TW-9.5 bridge ----

    [Fact]
    public void TW_9_5_bridge_shares_one_content_object_between_the_paths()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = factory,
        });

        var viaTab = lifecycle.MaterializeTab(state, "p");
        var viaBody = lifecycle.GetOrCreateToolWindowContent("p");

        Assert.Equal(TabMaterializationKind.Materialized, viaTab.Kind);
        Assert.Same(viaTab.Content, viaBody);
        Assert.Equal(1, factory.Created); // у моста нет второго пути создания
    }

    // ---- TW-9.2: retention of the body across hosts (DA-E37, DA-E38) ----

    [Fact]
    public void DA_E37_body_in_the_dock_survives_the_panel_close_and_dies_with_its_tab()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = factory,
        });
        state = state.Open("p");
        lifecycle.GetOrCreateToolWindowContent("p");

        // Тело переезжает в док-зону (перенос ≠ закрытие, DA-5.4).
        var anchored = state.OpenDocument("d1", registry);
        lifecycle.NotifyTransition(state, anchored);
        var moved = anchored.MoveTab("p", DockGroupRef.AtTab("d1"), 1, registry);
        lifecycle.NotifyTransition(anchored, moved);
        Assert.Equal(0, factory.Released);

        // Закрытие панели тело в доке не освобождает (DA-8.3)…
        var closed = moved.Close("p");
        lifecycle.NotifyTransition(moved, closed);
        Assert.Equal(0, factory.Released);

        // …а закрытие самой вкладки освобождает при любой политике (TW-9.2).
        var gone = closed.CloseTab("p");
        lifecycle.NotifyTransition(closed, gone);
        Assert.Equal(1, factory.Released);
    }

    [Fact]
    public void TW_9_2_close_tab_of_the_body_releases_under_the_keep_policy_too()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = factory, // KeepWhileRegistered — дефолт
        });
        lifecycle.GetOrCreateToolWindowContent("p");

        var closed = state.CloseTab("p");
        lifecycle.NotifyTransition(state, closed);

        Assert.Equal(1, factory.Released); // «контента без вкладки» не существует (TW-9.2)
    }

    [Fact]
    public void DA_E38_body_moved_back_into_a_closed_panel_waits_for_the_next_cycle()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = factory,
        });
        state = state.Open("p").OpenDocument("d1", registry);
        lifecycle.GetOrCreateToolWindowContent("p");
        var inDock = state.MoveTab("p", DockGroupRef.AtTab("d1"), 1, registry);
        lifecycle.NotifyTransition(state, inDock);
        var closed = inDock.Close("p");
        lifecycle.NotifyTransition(inDock, closed);
        Assert.Equal(0, factory.Released); // тело в доке пережило закрытие (DA-E37)

        // Перенос обратно в дерево уже закрытой панели — не переход из открытости (DA-5.4).
        var back = closed.MoveTab("p", DockGroupRef.PanelRoot("p"), 0, registry);
        lifecycle.NotifyTransition(closed, back);
        Assert.Equal(0, factory.Released);

        // Освобождает следующий цикл открытие → закрытие (TW-9.2).
        var reopened = back.Open("p");
        lifecycle.NotifyTransition(back, reopened);
        var recl = reopened.Close("p");
        lifecycle.NotifyTransition(reopened, recl);
        Assert.Equal(1, factory.Released);
    }

    // ---- DA-5.4: cross-boundary moves never touch tab content ----

    [Fact]
    public void DA_5_4_moves_across_the_panel_dock_boundary_never_touch_content()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var tabs = new StubTabFactory("p:");
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = tabs,
        });
        state = state.OpenPanelTab("p:t1", registry).OpenDocument("d1", registry);
        var content = lifecycle.MaterializeTab(state, "p:t1").Content;

        var toDock = state.MoveTab("p:t1", DockGroupRef.AtTab("d1"), 1, registry);
        lifecycle.NotifyTransition(state, toDock);
        var toWindow = toDock.MoveTabToNewWindow("p:t1", Bounds);
        lifecycle.NotifyTransition(toDock, toWindow);
        var back = toWindow.MoveTab("p:t1", DockGroupRef.PanelRoot("p"), 0, registry);
        lifecycle.NotifyTransition(toWindow, back);

        Assert.Equal(1, tabs.Created); // ни одного пересоздания…
        Assert.Equal(0, tabs.Released); // …и ни одного освобождения (DA-5.4)
        Assert.Same(content, lifecycle.MaterializeTab(back, "p:t1").Content);

        var closed = back.CloseTab("p:t1");
        lifecycle.NotifyTransition(back, closed);
        Assert.Equal(1, tabs.Released); // закрытие в дереве панели — освобождает
    }

    // ---- INV-D5 relocation at live registration (DA-E35, TW-10.3) ----

    [Fact]
    public void DA_E35_dock_content_registration_relocates_the_confirmed_tab()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with
                {
                    // X спит в дереве спящей панели — легально (INV-D5, TW-9.4).
                    ContentTree = new TabGroupNode { Tabs = ["p:t1", "doc1"], ActiveTabId = "p:t1" },
                },
            ],
            DockArea = new DockAreaState
            {
                Root = new TabGroupNode { Tabs = ["m1", "m2"], ActiveTabId = "m1" },
                CurrentTabId = "m1",
            },
        };

        var result = lifecycle.RegisterDockContent(state, new StubTabFactory("doc"));

        // doc1 подтверждён документом → переехал в конец текущей группы главного окна.
        AssertGroup(result.DockArea.Root, "m1", "m1", "m2", "doc1");
        Assert.Equal("m1", result.DockArea.CurrentTabId); // активность не украдена
        AssertGroup(Panel(result, "p").ContentTree, "p:t1", "p:t1");
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void TW_10_3_panel_registration_relocates_tabs_it_confirms_in_foreign_trees()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("q", LeftPrimary, 0) with { ContentTree = Group("p:t1") },
            ],
            DockArea = new DockAreaState { Root = Group("m"), CurrentTabId = "m" },
        };

        var result = lifecycle.Register(state, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"),
        });

        AssertGroup(Panel(result, "q").ContentTree, null); // чужое дерево очищено переездом
        AssertGroup(result.DockArea.Root, "m", "m", "p:t1");
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }
}
