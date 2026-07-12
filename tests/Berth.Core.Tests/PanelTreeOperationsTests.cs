using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Panel content trees in core commands (spec TW-9.5…TW-9.8, TW-9.12; DA-8.1, DA-8.2, INV-D5):
/// OpenPanelTab, canHost validation of moves, activity rules of panel hosts (DA-5.3, DA-E19,
/// DA-E39) and the shared tree commands operating on panel trees; catalog cases DA-E6/E7.
/// </summary>
public class PanelTreeOperationsTests
{
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowSlot LeftSecondary = new(ToolWindowSide.Left, ToolWindowGroup.Secondary);
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static TabGroupNode GroupActive(string active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    /// <summary>Заявки: док-контент — префикс "d", панель p — "p:", панель q — "q:" (TW-9.11).</summary>
    private static ToolWindowRegistry Registry()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("d"));
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"),
        });
        registry.Register(new ToolWindowDescriptor("q", "Q", LeftSecondary)
        {
            TabFactory = new StubTabFactory("q:"),
        });
        return registry;
    }

    private static LayoutState BaseState(
        TabTreeNode? pTree = null, TabTreeNode? qTree = null, DockAreaState? dockArea = null) =>
        LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with { ContentTree = pTree ?? TabGroupNode.Empty },
                new ToolWindowState("q", LeftSecondary, 0) with { ContentTree = qTree ?? TabGroupNode.Empty },
            ],
            DockArea = dockArea ?? DockAreaState.Empty,
        };

    private static ToolWindowState Panel(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabGroupNode AssertGroup(TabTreeNode node, string? active, params string[] tabs)
    {
        var group = Assert.IsType<TabGroupNode>(node);
        Assert.Equal(tabs, group.Tabs);
        Assert.Equal(active, group.ActiveTabId);
        return group;
    }

    // ---- TW-9.12 OpenPanelTab ----

    [Fact]
    public void TW_9_12_opens_into_the_empty_tree_as_the_root_group()
    {
        var registry = Registry();
        var state = BaseState();

        var result = state.OpenPanelTab("p:t1", registry);

        AssertGroup(Panel(result, "p").ContentTree, "p:t1", "p:t1");
        Assert.False(Panel(result, "p").IsOpen); // открытость не меняется (TW-9.3)
        Assert.Null(result.ActiveToolWindowId);
        Assert.Same(state.DockArea, result.DockArea); // хосты дока не тронуты
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void TW_9_12_inserts_after_the_active_tab_of_the_first_non_empty_group()
    {
        var registry = Registry();
        var state = BaseState(pTree: new SplitNode
        {
            Orientation = SplitOrientation.Row,
            Children =
            [
                new SplitChild(GroupActive("p:t1", "p:t1", "p:t2"), 0.5),
                new SplitChild(GroupActive("p:t3", "p:t3"), 0.5),
            ],
        });

        var result = state.OpenPanelTab("p:t4", registry);

        var root = Assert.IsType<SplitNode>(Panel(result, "p").ContentTree);
        AssertGroup(root.Children[0].Node, "p:t4", "p:t1", "p:t4", "p:t2");
        AssertGroup(root.Children[1].Node, "p:t3", "p:t3");
    }

    [Fact]
    public void TW_9_12_present_tab_only_becomes_active_in_its_group()
    {
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("p:t1", "p:t1", "p:t2"));

        var result = state.OpenPanelTab("p:t2", registry);

        AssertGroup(Panel(result, "p").ContentTree, "p:t2", "p:t1", "p:t2");
        Assert.False(Panel(result, "p").IsOpen);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_9_12_present_in_a_dock_group_moves_the_forced_current_tab_along()
    {
        // Вынужденное следствие INV-D4: у вкладки в текущей группе хоста за активной следует
        // и текущая вкладка хоста (N5) — прочих эффектов нет.
        var registry = Registry();
        var state = BaseState(dockArea: new DockAreaState
        {
            Root = GroupActive("d1", "d1", "p:t1"),
            CurrentTabId = "d1",
        });

        var result = state.OpenPanelTab("p:t1", registry);

        AssertGroup(result.DockArea.Root, "p:t1", "d1", "p:t1");
        Assert.Equal("p:t1", result.DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_9_12_rejects_unclaimed_dock_owned_and_stateless_ids()
    {
        var registry = Registry();
        var state = BaseState();

        Assert.Throws<ArgumentException>(() => state.OpenPanelTab("x1", registry)); // спящий владелец
        Assert.Throws<ArgumentException>(() => state.OpenPanelTab("d1", registry)); // документ

        // Владелец заявлен, но записи панели в раскладке нет — ошибка вызывающей стороны.
        registry.Register(new ToolWindowDescriptor("r", "R", LeftPrimary)
        {
            TabFactory = new StubTabFactory("r:"),
        });
        Assert.Throws<ArgumentException>(() => state.OpenPanelTab("r:t1", registry));
    }

    [Fact]
    public void TW_9_11_open_panel_tab_of_a_conflicted_claim_throws()
    {
        var registry = Registry();
        registry.Register(new ToolWindowDescriptor("p2", "P2", LeftPrimary)
        {
            TabFactory = new StubTabFactory("p:"), // конфликт с панелью p
        });

        Assert.Throws<InvalidOperationException>(() => BaseState().OpenPanelTab("p:t1", registry));
    }

    // ---- canHost: DA-E6 / DA-E7 / INV-D5 ----

    [Fact]
    public void DA_E6_moving_a_document_into_a_panel_is_an_error()
    {
        var registry = Registry();
        var state = BaseState(dockArea: new DockAreaState { Root = Group("d1"), CurrentTabId = "d1" });

        Assert.Throws<ArgumentException>(
            () => state.MoveTab("d1", DockGroupRef.PanelRoot("p"), 0, registry));
    }

    [Fact]
    public void INV_D5_moving_a_sleeping_tab_into_a_panel_is_an_error()
    {
        // Владелец не подтверждён — в панель нельзя (DA-9.4, INV-D5).
        var registry = Registry();
        var state = BaseState(dockArea: new DockAreaState { Root = Group("x1"), CurrentTabId = "x1" });

        Assert.Throws<ArgumentException>(
            () => state.MoveTab("x1", DockGroupRef.PanelRoot("p"), 0, registry));
    }

    [Fact]
    public void DA_E7_panel_tab_in_the_dock_moves_only_to_its_owner()
    {
        var registry = Registry();
        var state = BaseState(dockArea: new DockAreaState
        {
            Root = new TabGroupNode { Tabs = ["m", "p:t1"], ActiveTabId = "m" },
            CurrentTabId = "m",
        });

        Assert.Throws<ArgumentException>(
            () => state.MoveTab("p:t1", DockGroupRef.PanelRoot("q"), 0, registry)); // Q — ошибка

        var result = state.MoveTab("p:t1", DockGroupRef.PanelRoot("p"), 0, registry); // P — успех

        AssertGroup(Panel(result, "p").ContentTree, "p:t1", "p:t1");
        AssertGroup(result.DockArea.Root, "m", "m");
        Assert.Equal("m", result.DockArea.CurrentTabId);
        Assert.Null(result.ActiveToolWindowId); // активность назначает UI следом (DA-5.4)
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void DA_5_4_moves_within_one_panel_need_no_owner_confirmation()
    {
        // Спящая вкладка легальна в дереве предполагаемого владельца и переупорядочивается
        // внутри него свободно (INV-D5: проверка — только при смене хоста на панель).
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("s1", "s1", "p:t1"));

        var result = state.MoveTab("s1", DockGroupRef.AtTab("p:t1"), 1, registry);

        AssertGroup(Panel(result, "p").ContentTree, "s1", "p:t1", "s1");
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void DA_5_4_move_from_dock_to_panel_sets_only_the_group_active()
    {
        // У панели нет текущей вкладки хоста — правило приёмника ограничено активной вкладкой
        // группы; донорский док-хост чинит текущую вкладку по DA-6.3 (перенос между хостами).
        var registry = Registry();
        var state = BaseState(
            pTree: GroupActive("p:t0", "p:t0"),
            dockArea: new DockAreaState
            {
                Root = new TabGroupNode { Tabs = ["m", "p:t1"], ActiveTabId = "p:t1" },
                CurrentTabId = "p:t1",
            });

        var result = state.MoveTab("p:t1", DockGroupRef.AtTab("p:t0"), 1, registry);

        AssertGroup(Panel(result, "p").ContentTree, "p:t1", "p:t0", "p:t1");
        Assert.Equal("m", result.DockArea.CurrentTabId); // фолбэк донора DA-6.3
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void TW_9_8_move_from_panel_to_dock_sets_the_receiver_current_tab()
    {
        var registry = Registry();
        var state = BaseState(
            pTree: GroupActive("p:t1", "p:t1", "p:t2"),
            dockArea: new DockAreaState { Root = GroupActive("m", "m"), CurrentTabId = "m" });

        var result = state.MoveTab("p:t1", DockGroupRef.AtTab("m"), 1, registry);

        AssertGroup(result.DockArea.Root, "p:t1", "m", "p:t1");
        Assert.Equal("p:t1", result.DockArea.CurrentTabId);
        AssertGroup(Panel(result, "p").ContentTree, "p:t2", "p:t2"); // актив донора — DA-5.2
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void TW_9_8_panel_tab_moves_into_a_new_document_window()
    {
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("p:t1", "p:t1"));

        var result = state.MoveTabToNewWindow("p:t1", Bounds);

        var window = Assert.Single(result.DockArea.Windows);
        AssertGroup(window.Root, "p:t1", "p:t1");
        Assert.Equal("p:t1", window.CurrentTabId);
        AssertGroup(Panel(result, "p").ContentTree, null); // пустое дерево панели легально (DA-8.4)
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    // ---- DA-5.3 activation in panel trees ----

    [Fact]
    public void DA_E19_activation_in_an_open_panel_sets_the_active_tool_window_only()
    {
        var registry = Registry();
        var state = BaseState(
            pTree: GroupActive("p:t1", "p:t1", "p:t2"),
            dockArea: new DockAreaState { Root = GroupActive("m", "m"), CurrentTabId = "m" })
            with
        { ActiveToolWindowId = null };
        state = state with
        {
            ToolWindows = state.ToolWindows.SetItem(0, Panel(state, "p") with { IsOpen = true }),
        };

        var result = state.ActivateTab("p:t2");

        AssertGroup(Panel(result, "p").ContentTree, "p:t2", "p:t1", "p:t2");
        Assert.Equal("p", result.ActiveToolWindowId);
        Assert.Equal("m", result.DockArea.CurrentTabId); // хосты дока не тронуты (DA-6.2)
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    [Fact]
    public void DA_E39_activation_in_a_closed_panel_opens_it_with_eviction()
    {
        var registry = Registry();
        registry.Register(new ToolWindowDescriptor("b", "B", LeftPrimary));
        var state = BaseState(pTree: GroupActive("p:t1", "p:t1"));
        state = state with
        {
            ToolWindows = state.ToolWindows.Add(
                new ToolWindowState("b", LeftPrimary, 1) with { IsOpen = true }),
        };

        var result = state.ActivateTab("p:t1");

        Assert.True(Panel(result, "p").IsOpen); // панель открыта по TW-5.1
        Assert.False(Panel(result, "b").IsOpen); // сосед по слою вытеснен
        Assert.Equal("p", result.ActiveToolWindowId);
        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }

    // ---- shared tree commands over panel trees ----

    [Fact]
    public void DA_5_2_close_tab_in_a_panel_tree_follows_the_group_rule()
    {
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("p:t2", "p:t1", "p:t2", "p:t3"));

        var result = state.CloseTab("p:t2");

        AssertGroup(Panel(result, "p").ContentTree, "p:t1", "p:t1", "p:t3");
        Assert.Null(result.ActiveToolWindowId);

        var emptied = result.CloseTab("p:t1").CloseTab("p:t3");
        AssertGroup(Panel(emptied, "p").ContentTree, null); // пустой корень легален (DA-2.3)
        Assert.Empty(LayoutInvariants.Validate(emptied, registry));
    }

    [Fact]
    public void DA_5_5_split_and_rotate_work_inside_a_panel_tree()
    {
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("p:t1", "p:t1", "p:t2"));

        var split = state.SplitTab("p:t2", SplitDirection.Down);
        var column = Assert.IsType<SplitNode>(Panel(split, "p").ContentTree);
        Assert.Equal(SplitOrientation.Column, column.Orientation);
        AssertGroup(column.Children[0].Node, "p:t1", "p:t1");
        AssertGroup(column.Children[1].Node, "p:t2", "p:t2");

        var rotated = split.RotateSplit("p:t1");
        var row = Assert.IsType<SplitNode>(Panel(rotated, "p").ContentTree);
        Assert.Equal(SplitOrientation.Row, row.Orientation);
        Assert.Empty(LayoutInvariants.Validate(rotated, registry));
    }

    [Fact]
    public void DA_5_6_panel_splits_are_addressed_by_the_panel_id_and_path()
    {
        var registry = Registry();
        var state = BaseState(pTree: GroupActive("p:t1", "p:t1", "p:t2"))
            .SplitTab("p:t2", SplitDirection.Right);

        var result = state.SetSplitShares("p", [], [0.3, 0.7]);

        var root = Assert.IsType<SplitNode>(Panel(result, "p").ContentTree);
        Assert.Equal(0.3, root.Children[0].Share, precision: 12);
        Assert.Equal(0.7, root.Children[1].Share, precision: 12);
        Assert.Empty(LayoutInvariants.Validate(result, registry));

        Assert.Throws<ArgumentException>(() => state.SetSplitShares("ghost", [], [0.5, 0.5]));
        Assert.Throws<ArgumentException>(() => state.SetSplitShares("q", [], [0.5, 0.5])); // корень — группа
    }

    [Fact]
    public void Operations_keep_invariants_on_a_panel_worked_example()
    {
        var registry = Registry();
        var state = BaseState()
            .OpenDocument("d1", registry)
            .OpenPanelTab("p:t1", registry)
            .OpenPanelTab("p:t2", registry)
            .SplitTab("p:t2", SplitDirection.Down)
            .MoveTab("p:t1", DockGroupRef.AtTab("d1"), 1, registry)
            .OpenPanelTab("q:t1", registry)
            .ActivateTab("p:t2")
            .MoveTab("p:t1", DockGroupRef.AtTab("p:t2"), 0, registry)
            .SplitTab("p:t1", SplitDirection.Right)
            .RotateSplit("p:t1")
            .CloseTab("p:t2");

        Assert.Empty(LayoutInvariants.Validate(state, registry));
    }
}
