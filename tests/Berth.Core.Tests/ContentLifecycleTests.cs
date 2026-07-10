using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Content lifecycle of tool windows and live registration (spec TW-9.2, TW-9.3, TW-9.4,
/// TW-10.3/E15; DA-8.3, DA-E11, DA-E12): the four policy combinations, release on every close
/// path including Apply, unregistration with dock-tab cleanup, and the teardown.
/// </summary>
public class ContentLifecycleTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot RightPrimary = new(ToolWindowSide.Right, ToolWindowGroup.Primary);
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static ToolWindowState Window(string id, ToolWindowSlot slot, int order) => new(id, slot, order);

    private static LayoutState Layout(params ToolWindowState[] windows) =>
        LayoutState.Empty with { ToolWindows = [.. windows] };

    private static ToolWindowState Get(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    // ---- TW-9.2 policy combinations ----

    [Fact]
    public void TW_9_2_on_first_open_keep_creates_once_and_survives_close()
    {
        var factory = new StubToolWindowFactory();
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            ContentFactory = factory,
        });

        Assert.Equal(0, factory.Created); // OnFirstOpen: регистрация контент не создаёт

        var content = lifecycle.GetOrCreateToolWindowContent("a");
        Assert.NotNull(content);
        Assert.Same(content, lifecycle.GetOrCreateToolWindowContent("a"));
        Assert.Equal(1, factory.Created);

        var opened = state.Open("a");
        lifecycle.NotifyTransition(state, opened);
        var closed = opened.Close("a");
        lifecycle.NotifyTransition(opened, closed);

        Assert.Equal(0, factory.Released); // KeepWhileRegistered: закрытие не освобождает
        Assert.Same(content, lifecycle.GetOrCreateToolWindowContent("a"));
    }

    [Fact]
    public void TW_9_2_eager_creates_at_registration_even_for_a_closed_window()
    {
        var factory = new StubToolWindowFactory();
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            CreationPolicy = ContentCreationPolicy.Eager,
            ContentFactory = factory,
        });

        Assert.False(Get(state, "a").IsOpen);
        Assert.Equal(1, factory.Created); // Eager: создан при регистрации, панель закрыта

        Assert.NotNull(lifecycle.GetOrCreateToolWindowContent("a"));
        Assert.Equal(1, factory.Created); // GetOrCreate вернул созданное, не пересоздал
    }

    [Fact]
    public void TW_9_2_dispose_on_close_releases_and_recreates()
    {
        var factory = new StubToolWindowFactory();
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = factory,
        });

        state = state.Open("a");
        var first = lifecycle.GetOrCreateToolWindowContent("a");
        var closed = state.Close("a");
        lifecycle.NotifyTransition(state, closed);

        Assert.Equal(1, factory.Released);

        var second = lifecycle.GetOrCreateToolWindowContent("a");
        Assert.Equal(2, factory.Created); // пересоздан при следующей материализации
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TW_9_2_eager_dispose_on_close_content_of_a_never_opened_window_stays()
    {
        // Угол Eager × DisposeOnClose (спека v0.8): перехода «из открытости» не было —
        // контент живёт до первого закрытия из открытого состояния либо Unregister.
        var factory = new StubToolWindowFactory();
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            CreationPolicy = ContentCreationPolicy.Eager,
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = factory,
        });

        // Переход, не касающийся панели (закрыта в before и after), — не освобождает.
        var other = state.SetQuickAccessSide(QuickAccessSide.Right);
        lifecycle.NotifyTransition(state, other);
        Assert.Equal(0, factory.Released);

        var opened = other.Open("a");
        lifecycle.NotifyTransition(other, opened);
        var closed = opened.Close("a");
        lifecycle.NotifyTransition(opened, closed);
        Assert.Equal(1, factory.Released); // первый переход из открытости — освобождение
    }

    [Fact]
    public void TW_9_2_dispose_on_close_reacts_to_every_close_path()
    {
        // Пути закрытия помимо Close: вытеснение TW-5.1, скрытие иконки TW-5.10, HideAll TW-5.12.
        foreach (var (path, close) in new (string Path, Func<LayoutState, LayoutState> Close)[]
        {
            ("eviction TW-5.1", s => s.Open("b")),
            ("icon hiding TW-5.10", s => s.SetIconVisible("a", false)),
            ("hide all TW-5.12", s => s.HideAll()),
        })
        {
            var factory = new StubToolWindowFactory();
            var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
            var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
            {
                RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
                ContentFactory = factory,
            });
            state = lifecycle.Register(state, new ToolWindowDescriptor("b", "B", LeftPrimary));
            state = state.Open("a");
            lifecycle.GetOrCreateToolWindowContent("a");

            var after = close(state);
            lifecycle.NotifyTransition(state, after);

            Assert.False(Get(after, "a").IsOpen);
            Assert.True(factory.Released == 1, $"close path '{path}' must release the content");
        }
    }

    [Fact]
    public void TW_9_2_arrangement_apply_closing_the_window_releases_dispose_on_close()
    {
        // Пятый путь закрытия — применение макета (TW-10.7): Apply тоже переход.
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
            ContentFactory = factory,
        });
        state = state.Open("a");
        lifecycle.GetOrCreateToolWindowContent("a");

        var macro = Layout(Window("a", LeftPrimary, 0)); // панель в макете закрыта
        var result = state.Apply(macro, ApplyScope.Arrangement, registry);
        lifecycle.NotifyTransition(state, result.State);

        Assert.False(Get(result.State, "a").IsOpen);
        Assert.Equal(1, factory.Released);
    }

    // ---- TW-9.3 independence of state and content ----

    [Fact]
    public void TW_9_3_state_and_content_are_independent()
    {
        var factory = new StubToolWindowFactory();
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            ContentFactory = factory,
        });

        // Открытая панель без созданного контента — операции состояния его не требуют.
        var opened = state.Open("a");
        lifecycle.NotifyTransition(state, opened);
        var moved = opened.Move("a", RightPrimary, 0);
        lifecycle.NotifyTransition(opened, moved);
        var floated = moved.SetMode("a", ToolWindowMode.Float, Bounds);
        lifecycle.NotifyTransition(moved, floated);
        Assert.Equal(0, factory.Created);

        // Закрытая панель с живым контентом — тоже легальное сочетание.
        var closed = floated.Close("a");
        lifecycle.NotifyTransition(floated, closed);
        Assert.NotNull(lifecycle.GetOrCreateToolWindowContent("a"));
        Assert.Equal(1, factory.Created);
        Assert.Equal(0, factory.Released);
    }

    // ---- TW-10.3 / E15 live registration ----

    [Fact]
    public void TW_10_3_E15_live_registration_lands_at_the_end_of_the_slot_closed()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = Layout(
            Window("x", LeftPrimary, 0) with { IsOpen = true },
            Window("y", LeftPrimary, 1));

        var result = lifecycle.Register(state, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            DefaultPairRatio = 0.6,
        });

        var a = Get(result, "a");
        Assert.Equal(2, a.Order); // в конец слота (TW-10.3)
        Assert.False(a.IsOpen); // дескриптор открытость не задаёт (E15)
        Assert.Equal(0.6, a.PairRatio);
        Assert.True(Get(result, "x").IsOpen); // соседи не тронуты
        Assert.Empty(LayoutInvariants.Validate(result, registry)); // атомарно: нет окна INV-1
    }

    [Fact]
    public void TW_10_3_live_registration_picks_up_the_sleeping_record()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = Layout(Window("a", LeftPrimary, 0) with { PairRatio = 0.7 });

        var result = lifecycle.Register(state, new ToolWindowDescriptor("a", "A", RightPrimary)
        {
            DefaultPairRatio = 0.2,
        });

        var a = Get(result, "a");
        Assert.Equal(LeftPrimary, a.Slot); // сохранёнка побеждает дескриптор
        Assert.Equal(0.7, a.PairRatio);
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void TW_9_2_throwing_eager_factory_leaves_everything_untouched()
    {
        var factory = new StubToolWindowFactory { ThrowOnCreate = true };
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var descriptor = new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            CreationPolicy = ContentCreationPolicy.Eager,
            ContentFactory = factory,
        };

        Assert.Throws<InvalidOperationException>(() => lifecycle.Register(LayoutState.Empty, descriptor));

        // Транзакционность: реестр не тронут — починить фабрику и зарегистрировать заново.
        Assert.False(registry.TryGet("a", out _));
        factory.ThrowOnCreate = false;
        var state = lifecycle.Register(LayoutState.Empty, descriptor);
        Assert.Equal(1, factory.Created);
        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }

    [Fact]
    public void Register_of_a_duplicate_id_throws_and_changes_nothing()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary));

        Assert.Throws<ArgumentException>(
            () => lifecycle.Register(state, new ToolWindowDescriptor("a", "A2", RightPrimary)));

        Assert.True(registry.TryGet("a", out var descriptor));
        Assert.Equal("A", descriptor!.Title);
    }

    [Fact]
    public void GetOrCreate_of_an_unregistered_id_throws_and_a_null_factory_yields_null()
    {
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());

        Assert.Throws<ArgumentException>(() => lifecycle.GetOrCreateToolWindowContent("ghost"));

        lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary));
        Assert.Null(lifecycle.GetOrCreateToolWindowContent("a"));
    }

    // ---- TW-9.4 unregistration ----

    [Fact]
    public void TW_9_4_unregister_closes_releases_and_leaves_a_sleeping_state()
    {
        var factory = new StubToolWindowFactory();
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(
            Layout(Window("a", LeftPrimary, 0) with { PairRatio = 0.7 }),
            new ToolWindowDescriptor("a", "A", LeftPrimary) { ContentFactory = factory });
        state = state.Open("a");
        lifecycle.GetOrCreateToolWindowContent("a");

        var result = lifecycle.Unregister(state, "a");

        Assert.False(registry.TryGet("a", out _));
        var sleeping = Get(result, "a");
        Assert.False(sleeping.IsOpen); // закрыта…
        Assert.Equal(0.7, sleeping.PairRatio); // …поля спящей записи сохранены (TW-10.2)
        Assert.Null(result.ActiveToolWindowId);
        Assert.Equal(1, factory.Released); // KeepWhileRegistered — освобождение всё равно происходит (TW-9.4)

        // Повторная регистрация подхватывает спящую запись (TW-10.3).
        var registered = lifecycle.Register(result, new ToolWindowDescriptor("a", "A", RightPrimary));
        Assert.Equal(LeftPrimary, Get(registered, "a").Slot);
        Assert.Empty(LayoutInvariants.Validate(registered, registry));
    }

    [Fact]
    public void TW_9_4_unregister_of_an_unknown_id_throws()
    {
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());

        Assert.Throws<ArgumentException>(() => lifecycle.Unregister(LayoutState.Empty, "ghost"));
    }

    [Fact]
    public void TW_9_4_notify_after_unregister_releases_nothing_more()
    {
        // Переходы, произведённые координатором, доложены быть не должны; ошибочный доклад —
        // no-op, двойного освобождения нет (контракт NotifyTransition).
        var panelFactory = new StubToolWindowFactory();
        var tabFactory = new StubTabFactory("p:");
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var restored = LayoutState.Empty.Apply(
            Layout(Window("p", LeftPrimary, 0)) with
            {
                DockArea = new DockAreaState { Root = Group("p:t1"), CurrentTabId = "p:t1" },
            },
            ApplyScope.Full, registry).State;
        var state = lifecycle.Register(restored, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            ContentFactory = panelFactory,
            TabFactory = tabFactory,
        });
        state = state.Open("p");
        lifecycle.GetOrCreateToolWindowContent("p");
        lifecycle.MaterializeTab(state, "p:t1");

        var after = lifecycle.Unregister(state, "p");
        lifecycle.NotifyTransition(state, after); // ошибочный доклад того же перехода

        Assert.Equal(1, panelFactory.Released);
        Assert.Equal(1, tabFactory.Released);
    }

    // ---- DA-E11 / DA-E12: panel tabs in the dock area ----

    [Fact]
    public void DA_E11_dock_tab_of_a_closed_panel_lives_but_unregister_closes_it()
    {
        var tabFactory = new StubTabFactory("p:");
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);

        // Единственный путь панельной вкладки в док в 1.7: restore спящей + регистрация владельца.
        var restored = LayoutState.Empty.Apply(
            Layout(Window("p", LeftPrimary, 0)) with
            {
                DockArea = new DockAreaState { Root = Group("p:t1"), CurrentTabId = "p:t1" },
            },
            ApplyScope.Full, registry).State;
        var state = lifecycle.Register(restored, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = tabFactory,
        });
        state = state.Open("p");
        Assert.Equal(TabMaterializationKind.Materialized, lifecycle.MaterializeTab(state, "p:t1").Kind);

        // Закрытие панели вкладку не отзывает и контент не трогает (DA-8.3, TW-9.10).
        var closed = state.Close("p");
        lifecycle.NotifyTransition(state, closed);
        Assert.Equal(["p:t1"], Assert.IsType<TabGroupNode>(closed.DockArea.Root).Tabs);
        Assert.Equal(0, tabFactory.Released);

        // Unregister — отзывает безвозвратно (TW-9.4, DA-9.4).
        var result = lifecycle.Unregister(closed, "p");
        Assert.Empty(Assert.IsType<TabGroupNode>(result.DockArea.Root).Tabs);
        Assert.Equal(1, tabFactory.Released);

        // Повторная регистрация вкладки не восстанавливает (TW-9.10).
        var registered = lifecycle.Register(result, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"),
        });
        Assert.Empty(Assert.IsType<TabGroupNode>(registered.DockArea.Root).Tabs);
        Assert.Equal(LeftPrimary, Get(registered, "p").Slot); // спящая запись панели при этом жива
    }

    [Fact]
    public void DA_E12_unregister_removes_owner_tabs_from_all_dock_hosts()
    {
        var tabFactory = new StubTabFactory("p:");
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var snapshot = Layout(Window("p", LeftPrimary, 0)) with
        {
            DockArea = new DockAreaState
            {
                Root = new TabGroupNode { Tabs = ["m", "p:t1"], ActiveTabId = "m" },
                CurrentTabId = "m",
                Windows = [new DocumentWindowState(Bounds, Group("p:t2"), "p:t2")],
            },
        };
        var restored = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry).State;
        var state = lifecycle.Register(restored, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = tabFactory,
        });

        var result = lifecycle.Unregister(state, "p");

        // Вкладки владельца удалены из обоих деревьев; опустевшее окно исчезло (INV-D6).
        Assert.Equal(["m"], Assert.IsType<TabGroupNode>(result.DockArea.Root).Tabs);
        Assert.Empty(result.DockArea.Windows);
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    // ---- teardown ----

    [Fact]
    public void ReleaseAll_releases_every_live_object_once()
    {
        var panelFactory = new StubToolWindowFactory();
        var docs = new StubTabFactory("d");
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(docs);
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor("a", "A", LeftPrimary)
        {
            ContentFactory = panelFactory,
        });
        state = state.OpenDocument("d1").OpenDocument("d2");
        lifecycle.GetOrCreateToolWindowContent("a");
        lifecycle.MaterializeTab(state, "d1");
        lifecycle.MaterializeTab(state, "d2");

        lifecycle.ReleaseAll();

        Assert.Equal(1, panelFactory.Released);
        Assert.Equal(2, docs.Released);
        Assert.Equal(0, panelFactory.LiveCount);
        Assert.Equal(0, docs.LiveCount);

        lifecycle.ReleaseAll(); // повторный teardown — no-op
        Assert.Equal(1, panelFactory.Released);
        Assert.Equal(2, docs.Released);
    }
}
