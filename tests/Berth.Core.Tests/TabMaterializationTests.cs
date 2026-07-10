using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Tab materialization (spec DA-9.3, DA-9.4, DA-5.4, DA-9.1): lazy creation via the owning
/// claim, sleeping tabs, factory refusal closing the tab uniformly for restored and fresh
/// tabs, the re-read discipline after a refusal, moves never touching content, and Apply as
/// a content-affecting transition.
/// </summary>
public class TabMaterializationTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static LayoutState DockLayout(DockAreaState area) => LayoutState.Empty with { DockArea = area };

    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, StubTabFactory Docs) DockSetup()
    {
        var registry = new ToolWindowRegistry();
        var docs = new StubTabFactory("d");
        registry.RegisterDockContent(docs);
        return (registry, new ContentLifecycle(registry), docs);
    }

    // ---- DA-9.3 lazy materialization ----

    [Fact]
    public void DA_9_3_materialization_is_lazy_and_returns_the_same_content()
    {
        var (_, lifecycle, docs) = DockSetup();
        var state = LayoutState.Empty.OpenDocument("d1");
        Assert.Equal(0, docs.Created); // открытие вкладки контент не создаёт (TW-9.3)

        var first = lifecycle.MaterializeTab(state, "d1");
        Assert.Equal(TabMaterializationKind.Materialized, first.Kind);
        Assert.NotNull(first.Content);
        Assert.Same(state, first.State); // состояние не тронуто

        var second = lifecycle.MaterializeTab(state, "d1");
        Assert.Same(first.Content, second.Content);
        Assert.Equal(1, docs.Created);
    }

    // ---- DA-9.4 sleeping tabs ----

    [Fact]
    public void DA_9_4_unclaimed_tab_sleeps_untouched()
    {
        // Регистраций нет вовсе — незаявленный id спит и в живой сессии (TW-9.11).
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = LayoutState.Empty.OpenDocument("mystery");

        var result = lifecycle.MaterializeTab(state, "mystery");

        Assert.Equal(TabMaterializationKind.Sleeping, result.Kind);
        Assert.Null(result.Content);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void DA_9_4_registration_of_the_owner_materializes_lazily()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var restored = LayoutState.Empty.Apply(
            DockLayout(new DockAreaState { Root = Group("p:t1"), CurrentTabId = "p:t1" }),
            ApplyScope.Full, registry).State;
        Assert.Equal(TabMaterializationKind.Sleeping, lifecycle.MaterializeTab(restored, "p:t1").Kind);

        var tabs = new StubTabFactory("p:");
        var state = lifecycle.Register(restored, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = tabs,
        });
        Assert.Equal(0, tabs.Created); // сама регистрация ничего не материализует — лениво (DA-9.4)

        var result = lifecycle.MaterializeTab(state, "p:t1");
        Assert.Equal(TabMaterializationKind.Materialized, result.Kind);
        Assert.NotNull(result.Content);
    }

    [Fact]
    public void DA_9_4_owner_registration_then_refusal_closes_the_tab()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var restored = LayoutState.Empty.Apply(
            DockLayout(new DockAreaState { Root = Group("p:t1", "p:t2"), CurrentTabId = "p:t1" }),
            ApplyScope.Full, registry).State;
        var tabs = new StubTabFactory("p:") { Refuse = id => string.Equals(id, "p:t1", StringComparison.Ordinal) };
        var state = lifecycle.Register(restored, new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = tabs,
        });

        var result = lifecycle.MaterializeTab(state, "p:t1");

        Assert.Equal(TabMaterializationKind.Refused, result.Kind);
        Assert.Equal(["p:t2"], Assert.IsType<TabGroupNode>(result.State.DockArea.Root).Tabs);
    }

    // ---- DA-9.3 refusal ----

    [Fact]
    public void DA_9_3_refusal_closes_the_tab_with_normalization()
    {
        var (registry, lifecycle, docs) = DockSetup();
        docs.Refuse = id => string.Equals(id, "d-bad", StringComparison.Ordinal);
        var state = DockLayout(new DockAreaState
        {
            Root = new TabGroupNode { Tabs = ["d1", "d-bad"], ActiveTabId = "d-bad" },
            CurrentTabId = "d-bad",
        });

        var result = lifecycle.MaterializeTab(state, "d-bad");

        Assert.Equal(TabMaterializationKind.Refused, result.Kind);
        Assert.Null(result.Content);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(result.State.DockArea.Root).Tabs);
        Assert.Equal("d1", result.State.DockArea.CurrentTabId); // фолбэк активности DA-5.2
        Assert.Equal(0, docs.Created);
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }

    [Fact]
    public void DA_9_3_refusal_of_the_last_window_tab_dissolves_the_window()
    {
        var (registry, lifecycle, docs) = DockSetup();
        docs.Refuse = id => string.Equals(id, "d-w", StringComparison.Ordinal);
        var state = DockLayout(new DockAreaState
        {
            Root = Group("d-m"),
            CurrentTabId = "d-m",
            Windows = [new DocumentWindowState(Bounds, Group("d-w"), "d-w")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = lifecycle.MaterializeTab(state, "d-w");

        Assert.Equal(TabMaterializationKind.Refused, result.Kind);
        Assert.Empty(result.State.DockArea.Windows); // окно исчезло (INV-D6)
        Assert.Equal(DockHost.MainWindow, result.State.DockArea.ActiveDockHost);
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }

    [Fact]
    public void DA_9_3_refusal_is_uniform_for_a_freshly_opened_tab()
    {
        // Отказ единообразен: fresh или restored — ядро их не различает (спека v0.7).
        var (_, lifecycle, docs) = DockSetup();
        docs.Refuse = _ => true;
        var state = LayoutState.Empty.OpenDocument("d-fresh");

        var result = lifecycle.MaterializeTab(state, "d-fresh");

        Assert.Equal(TabMaterializationKind.Refused, result.Kind);
        var root = Assert.IsType<TabGroupNode>(result.State.DockArea.Root);
        Assert.Empty(root.Tabs);
        Assert.Null(result.State.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_9_3_restore_loop_recovers_after_each_refusal()
    {
        // Дисциплина DA-1.3: после каждого Refused вызывающий продолжает с нового состояния.
        var (registry, lifecycle, docs) = DockSetup();
        docs.Refuse = id => id.EndsWith("bad", StringComparison.Ordinal);
        var state = DockLayout(new DockAreaState
        {
            Root = new SplitNode
            {
                Orientation = SplitOrientation.Row,
                Children =
                [
                    new SplitChild(new TabGroupNode { Tabs = ["d1", "d-bad"], ActiveTabId = "d1" }, 0.5),
                    new SplitChild(new TabGroupNode { Tabs = ["d2bad", "d3"], ActiveTabId = "d3" }, 0.5),
                ],
            },
            CurrentTabId = "d1",
        });

        foreach (var id in new[] { "d1", "d-bad", "d2bad", "d3" })
        {
            state = lifecycle.MaterializeTab(state, id).State;
        }

        var root = Assert.IsType<SplitNode>(state.DockArea.Root);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(root.Children[0].Node).Tabs);
        Assert.Equal(["d3"], Assert.IsType<TabGroupNode>(root.Children[1].Node).Tabs);
        Assert.Equal(2, docs.Created); // материализованы ровно принятые
        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }

    // ---- DA-5.4 moves never touch content ----

    [Fact]
    public void DA_5_4_moves_never_touch_content()
    {
        var (_, lifecycle, docs) = DockSetup();
        var state = LayoutState.Empty.OpenDocument("d1").OpenDocument("d2");
        var content = lifecycle.MaterializeTab(state, "d1").Content;

        var after = state.SplitTab("d1", SplitDirection.Right);
        lifecycle.NotifyTransition(state, after);
        state = after;

        after = state.MoveTab("d1", DockGroupRef.AtTab("d2"), 0);
        lifecycle.NotifyTransition(state, after);
        state = after;

        after = state.MoveTabToNewWindow("d1", Bounds);
        lifecycle.NotifyTransition(state, after);
        state = after;

        Assert.Equal(1, docs.Created); // ни одного пересоздания…
        Assert.Equal(0, docs.Released); // …и ни одного освобождения (DA-5.4)
        Assert.Same(content, lifecycle.MaterializeTab(state, "d1").Content);

        var closed = state.CloseTab("d1");
        lifecycle.NotifyTransition(state, closed);
        Assert.Equal(1, docs.Released); // закрытие — освобождает
    }

    // ---- DA-9.1 Apply as a content-affecting transition ----

    [Fact]
    public void DA_9_1_apply_full_releases_content_of_vanished_tabs()
    {
        var (registry, lifecycle, docs) = DockSetup();
        var state = LayoutState.Empty.OpenDocument("d1").OpenDocument("d2");
        var kept = lifecycle.MaterializeTab(state, "d2").Content;
        lifecycle.MaterializeTab(state, "d1");

        var snapshot = DockLayout(new DockAreaState { Root = Group("d2"), CurrentTabId = "d2" });
        var applied = state.Apply(snapshot, ApplyScope.Full, registry);
        lifecycle.NotifyTransition(state, applied.State);

        Assert.Equal(1, docs.Released); // d1 ушла из раскладки — освобождена
        Assert.Same(kept, lifecycle.MaterializeTab(applied.State, "d2").Content); // d2 пережила Apply
        Assert.Equal(2, docs.Created);
    }

    [Fact]
    public void DA_9_1_arrangement_apply_releases_no_tab_content()
    {
        // Зеркало E20: Arrangement док-зону не трогает — ноль освобождений вкладок.
        var (registry, lifecycle, docs) = DockSetup();
        var state = LayoutState.Empty.OpenDocument("d1");
        lifecycle.MaterializeTab(state, "d1");

        var applied = state.Apply(LayoutState.Empty, ApplyScope.Arrangement, registry);
        lifecycle.NotifyTransition(state, applied.State);

        Assert.Equal(0, docs.Released);
    }

    // ---- errors ----

    [Fact]
    public void MaterializeTab_of_an_unknown_tab_throws()
    {
        var (_, lifecycle, _) = DockSetup();

        Assert.Throws<ArgumentException>(() => lifecycle.MaterializeTab(LayoutState.Empty, "ghost"));
    }

    [Fact]
    public void TW_9_11_materialization_of_a_conflicting_claim_throws()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("x"));
        registry.RegisterDockContent(new StubTabFactory("x"));
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty.OpenDocument("x1");

        Assert.Throws<InvalidOperationException>(() => lifecycle.MaterializeTab(state, "x1"));
    }
}
