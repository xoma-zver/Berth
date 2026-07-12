using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// Dock-area commands (spec document-area, section 5): DA-5.1…DA-5.9 with the activity
/// rules DA-6.1…DA-6.3. Catalog edge cases are referenced by their DA-E ids; canHost cases
/// (DA-E6/E7/E11/E12) arrive with panel trees (backlog 1.8), Apply cases — DockApplyTests.
/// </summary>
public class DockOperationsTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);
    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);
    private static readonly ToolWindowRegistry EmptyRegistry = new();

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static TabGroupNode GroupActive(string active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static SplitNode Column(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Column, Children = [.. children] };

    private static LayoutState Layout(DockAreaState area) => LayoutState.Empty with { DockArea = area };

    private static LayoutState WithPanel(LayoutState state, string id, bool open, bool active) => state with
    {
        ToolWindows = [new ToolWindowState(id, LeftPrimary, 0) with { IsOpen = open }],
        ActiveToolWindowId = active ? id : state.ActiveToolWindowId,
    };

    private static TabGroupNode AssertGroup(TabTreeNode node, string? active, params string[] tabs)
    {
        var group = Assert.IsType<TabGroupNode>(node);
        Assert.Equal(tabs, group.Tabs);
        Assert.Equal(active, group.ActiveTabId);
        return group;
    }

    private static SplitNode AssertSplit(TabTreeNode node, SplitOrientation orientation, params double[] shares)
    {
        var split = Assert.IsType<SplitNode>(node);
        Assert.Equal(orientation, split.Orientation);
        Assert.Equal(shares.Length, split.Children.Length);
        for (var i = 0; i < shares.Length; i++)
        {
            Assert.Equal(shares[i], split.Children[i].Share, precision: 12);
        }

        return split;
    }

    // ---- DA-5.1 OpenDocument ----

    [Fact]
    public void DA_5_1_opens_into_the_empty_main_window()
    {
        var result = LayoutState.Empty.OpenDocument("a", EmptyRegistry);

        AssertGroup(result.DockArea.Root, "a", "a");
        Assert.Equal("a", result.DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_5_1_inserts_after_the_active_tab()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b"), CurrentTabId = "a" });

        var result = state.OpenDocument("c", EmptyRegistry);

        AssertGroup(result.DockArea.Root, "c", "a", "c", "b");
        Assert.Equal("c", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_1_reopen_activates_in_place() // DA-E17
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a"), 0.5), Child(GroupActive("e", "e", "d"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.OpenDocument("d", EmptyRegistry);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "d", "e", "d");
        Assert.Equal("d", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_1_effective_host_is_main_while_a_panel_is_active() // DA-E16
    {
        var state = WithPanel(
            Layout(new DockAreaState
            {
                Root = GroupActive("m", "m"),
                CurrentTabId = "m",
                Windows = [new DocumentWindowState(Bounds, Group("w"), "w")],
                ActiveDockHost = DockHost.DocumentWindow(0),
            }),
            "p", open: true, active: true);

        var result = state.OpenDocument("d", EmptyRegistry);

        AssertGroup(result.DockArea.Root, "d", "m", "d");
        Assert.Equal("d", result.DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void DA_5_1_effective_host_is_the_active_document_window()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("w", "w"), "w")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = state.OpenDocument("d", EmptyRegistry);

        AssertGroup(result.DockArea.Windows[0].Root, "d", "w", "d");
        Assert.Equal("d", result.DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.DockArea.ActiveDockHost);
        AssertGroup(result.DockArea.Root, "m", "m");
    }

    [Fact]
    public void DA_E33_reopen_in_another_host_activates_there()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("x", "x", "d"), "x")],
            ActiveDockHost = DockHost.MainWindow,
        });

        var result = state.OpenDocument("d", EmptyRegistry);

        AssertGroup(result.DockArea.Windows[0].Root, "d", "x", "d");
        Assert.Equal("d", result.DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.DockArea.ActiveDockHost);
        Assert.Same(state.DockArea.Root, result.DockArea.Root);
        Assert.Equal("m", result.DockArea.CurrentTabId);
    }

    // ---- DA-5.2 CloseTab ----

    [Fact]
    public void DA_5_2_closing_the_active_tab_selects_the_previous_neighbour() // DA-E2
    {
        var state = Layout(new DockAreaState { Root = GroupActive("b", "a", "b", "c"), CurrentTabId = "b" });

        var result = state.CloseTab("b");

        AssertGroup(result.DockArea.Root, "a", "a", "c");
        Assert.Equal("a", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_2_closing_the_first_active_tab_selects_the_new_first() // DA-E3
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b"), CurrentTabId = "a" });

        var result = state.CloseTab("a");

        AssertGroup(result.DockArea.Root, "b", "b");
        Assert.Equal("b", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_2_closing_the_last_active_tab_selects_the_previous_not_the_first() // DA-E29
    {
        var state = Layout(new DockAreaState { Root = GroupActive("c", "a", "b", "c"), CurrentTabId = "c" });

        var result = state.CloseTab("c");

        AssertGroup(result.DockArea.Root, "b", "a", "b");
        Assert.Equal("b", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_2_closing_an_inactive_tab_keeps_the_active_one()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b", "c"), CurrentTabId = "a" });

        var result = state.CloseTab("c");

        AssertGroup(result.DockArea.Root, "a", "a", "b");
        Assert.Equal("a", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_E28_fallbacks_do_not_distinguish_sleeping_tabs()
    {
        // Владелец S не зарегистрирован — «спящая» вкладка для структуры и фолбэков ничем
        // не отличается от живой (DA-9.4): активной и текущей становится она.
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "s"), CurrentTabId = "a" });

        var result = state.CloseTab("a");

        AssertGroup(result.DockArea.Root, "s", "s");
        Assert.Equal("s", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_2_closing_the_last_tab_leaves_the_empty_root() // DA-E4
    {
        var state = Layout(new DockAreaState { Root = GroupActive("x", "x"), CurrentTabId = "x" });

        var result = state.CloseTab("x");

        AssertGroup(result.DockArea.Root, null);
        Assert.Null(result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_6_3_vanished_group_falls_back_to_the_previous_in_dfs() // DA-E30
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("g1", "g1"), 1.0 / 3),
                Child(GroupActive("g2", "g2"), 1.0 / 3),
                Child(GroupActive("g3", "g3"), 1.0 / 3)),
            CurrentTabId = "g3",
        });

        var result = state.CloseTab("g3");

        Assert.Equal("g2", result.DockArea.CurrentTabId);
        AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
    }

    [Fact]
    public void DA_6_3_vanished_first_group_falls_back_to_the_next()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("g1", "g1"), 0.5), Child(GroupActive("g2", "g2"), 0.5)),
            CurrentTabId = "g1",
        });

        var result = state.CloseTab("g1");

        Assert.Equal("g2", result.DockArea.CurrentTabId);
        AssertGroup(result.DockArea.Root, "g2", "g2");
    }

    [Fact]
    public void DA_6_3_vanished_middle_group_prefers_the_previous_over_the_next()
    {
        // Дискриминатор приоритета «предыдущая, иначе следующая»: у донора в середине
        // есть обе соседки — инверсия previous/next выбрала бы g3.
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("g1", "g1"), 1.0 / 3),
                Child(GroupActive("g2", "g2"), 1.0 / 3),
                Child(GroupActive("g3", "g3"), 1.0 / 3)),
            CurrentTabId = "g2",
        });

        var result = state.CloseTab("g2");

        Assert.Equal("g1", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_6_3_middle_group_fallback_applies_to_moves_out_of_the_host()
    {
        // Зеркало предыдущего теста для MoveTabToNewWindow — фолбэк донора общий.
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("g1", "g1"), 1.0 / 3),
                Child(GroupActive("g2", "g2"), 1.0 / 3),
                Child(GroupActive("g3", "g3"), 1.0 / 3)),
            CurrentTabId = "g2",
        });

        var result = state.MoveTabToNewWindow("g2", Bounds);

        Assert.Equal("g1", result.DockArea.CurrentTabId);
        Assert.Equal("g2", result.DockArea.Windows[0].CurrentTabId);
    }

    [Fact]
    public void DA_6_3_neighbour_is_taken_from_the_pre_removal_tree()
    {
        // Обход до нормализации: a, b, c, g — предыдущая соседка G это C; вырожденный
        // фолбэк N5 («первая непустая группа») дал бы A.
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.5),
                Child(Column(
                    Child(Row(Child(GroupActive("b", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)), 0.5),
                    Child(GroupActive("g", "g"), 0.5)), 0.5)),
            CurrentTabId = "g",
        });

        var result = state.CloseTab("g");

        Assert.Equal("c", result.DockArea.CurrentTabId);
        AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.25, 0.25);
    }

    [Fact]
    public void DA_E8_closing_the_last_tab_of_a_group_collapses_and_scales()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.5),
                Child(Column(
                    Child(GroupActive("g", "g"), 0.5),
                    Child(Row(Child(GroupActive("b", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.CloseTab("g");

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.25, 0.25);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "b");
        AssertGroup(root.Children[2].Node, "c", "c");
        Assert.Equal("a", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_E18_closing_all_window_tabs_restores_the_main_window_memory()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("w1", "w1", "w2"), "w1")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var afterFirst = state.CloseTab("w1");
        Assert.Equal("w2", afterFirst.DockArea.Windows[0].CurrentTabId);

        var result = afterFirst.CloseTab("w2");

        Assert.Empty(result.DockArea.Windows);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
        Assert.Equal("m", result.DockArea.CurrentTabId);
    }

    // ---- DA-5.3 ActivateTab / DA-6.2 ----

    [Fact]
    public void DA_6_2_activate_tab_clears_the_active_tool_window() // TW-6.5
    {
        var state = WithPanel(
            Layout(new DockAreaState { Root = GroupActive("x", "x"), CurrentTabId = "x" }),
            "p", open: true, active: true);

        var result = state.ActivateTab("x");

        Assert.Null(result.ActiveToolWindowId);
    }

    [Fact]
    public void DA_5_3_activation_updates_group_host_and_active_host()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("w2", "w1", "w2"), "w2")],
            ActiveDockHost = DockHost.MainWindow,
        });

        var result = state.ActivateTab("w1");

        AssertGroup(result.DockArea.Windows[0].Root, "w1", "w1", "w2");
        Assert.Equal("w1", result.DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.DockArea.ActiveDockHost);
        Assert.Equal("m", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_6_2_opening_a_panel_leaves_the_dock_area_untouched()
    {
        var state = WithPanel(
            Layout(new DockAreaState
            {
                Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5)),
                CurrentTabId = "a",
            }),
            "p", open: false, active: false);

        var result = state.Open("p");

        Assert.Same(state.DockArea, result.DockArea);
    }

    // ---- DA-5.4 MoveTab ----

    [Fact]
    public void DA_5_4_reorders_within_the_group()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b", "c"), CurrentTabId = "a" });

        var result = state.MoveTab("c", DockGroupRef.AtTab("a"), 0, EmptyRegistry);

        AssertGroup(result.DockArea.Root, "c", "c", "a", "b");
        Assert.Equal("c", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_4_moved_tab_becomes_current_of_the_receiving_host()
    {
        // b не была текущей; правило приёмника безусловно делает её текущей.
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.MoveTab("b", DockGroupRef.AtTab("c"), 1, EmptyRegistry);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "c", "b");
        Assert.Equal("b", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_4_moving_the_current_tab_within_one_host_keeps_it_current()
    {
        // Регрессия guard-правила: внутри одного хоста фолбэк донора DA-6.3 не применяется —
        // наивная реализация отдала бы текущую вкладку соседке B.
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.MoveTab("a", DockGroupRef.AtTab("c"), 0, EmptyRegistry);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "b", "b");
        AssertGroup(root.Children[1].Node, "a", "a", "c");
        Assert.Equal("a", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_4_index_is_clamped()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b"), CurrentTabId = "a" });

        AssertGroup(state.MoveTab("a", DockGroupRef.AtTab("b"), 99, EmptyRegistry).DockArea.Root, "a", "b", "a");
        AssertGroup(state.MoveTab("b", DockGroupRef.AtTab("a"), -5, EmptyRegistry).DockArea.Root, "b", "b", "a");
    }

    [Fact]
    public void DA_5_4_target_addressed_by_the_moved_tab_is_a_reorder()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("b", "a", "b", "c"), CurrentTabId = "b" });

        var result = state.MoveTab("b", DockGroupRef.AtTab("b"), 2, EmptyRegistry);

        AssertGroup(result.DockArea.Root, "b", "a", "c", "b");
        Assert.Equal("b", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_4_cross_host_move_applies_the_donor_fallback()
    {
        var state = WithPanel(
            Layout(new DockAreaState
            {
                Root = GroupActive("m", "m"),
                CurrentTabId = "m",
                Windows = [new DocumentWindowState(Bounds, GroupActive("w1", "w1", "w2"), "w1")],
                ActiveDockHost = DockHost.DocumentWindow(0),
            }),
            "p", open: true, active: true);

        var result = state.MoveTab("w1", DockGroupRef.AtTab("m"), 1, EmptyRegistry);

        AssertGroup(result.DockArea.Root, "w1", "m", "w1");
        Assert.Equal("w1", result.DockArea.CurrentTabId);
        AssertGroup(result.DockArea.Windows[0].Root, "w2", "w2");
        Assert.Equal("w2", result.DockArea.Windows[0].CurrentTabId);
        // ActiveDockHost и ActiveToolWindowId команда не трогает (DA-5.4).
        Assert.Equal(DockHost.DocumentWindow(0), result.DockArea.ActiveDockHost);
        Assert.Equal("p", result.ActiveToolWindowId);
    }

    [Fact]
    public void DA_E5_moving_the_last_tab_dissolves_the_window()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("x", "x"), "x")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = state.MoveTab("x", DockGroupRef.AtTab("m"), 0, EmptyRegistry);

        Assert.Empty(result.DockArea.Windows);
        AssertGroup(result.DockArea.Root, "x", "x", "m");
        Assert.Equal("x", result.DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_5_4_hostroot_targets_the_empty_main_root()
    {
        var state = Layout(new DockAreaState
        {
            Root = TabGroupNode.Empty,
            CurrentTabId = null,
            Windows = [new DocumentWindowState(Bounds, GroupActive("w1", "w1", "w2"), "w1")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = state.MoveTab("w1", DockGroupRef.HostRoot(DockHost.MainWindow), 0, EmptyRegistry);

        AssertGroup(result.DockArea.Root, "w1", "w1");
        Assert.Equal("w1", result.DockArea.CurrentTabId);
        AssertGroup(result.DockArea.Windows[0].Root, "w2", "w2");
        Assert.Equal("w2", result.DockArea.Windows[0].CurrentTabId);
        Assert.Equal(DockHost.DocumentWindow(0), result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_5_4_hostroot_over_a_split_root_throws()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Throws<ArgumentException>(() => state.MoveTab("a", DockGroupRef.HostRoot(DockHost.MainWindow), 0, EmptyRegistry));
    }

    // ---- DA-5.5 SplitTab ----

    [Fact]
    public void DA_E13_split_across_replaces_the_group_with_a_perpendicular_pair()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a"), 0.5), Child(GroupActive("x", "x", "y"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.SplitTab("x", SplitDirection.Down);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "a", "a");
        var column = AssertSplit(root.Children[1].Node, SplitOrientation.Column, 0.5, 0.5);
        AssertGroup(column.Children[0].Node, "y", "y");
        AssertGroup(column.Children[1].Node, "x", "x");
        Assert.Equal("x", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_E14_split_along_halves_only_the_donor_share()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.3),
                Child(GroupActive("x", "x", "y"), 0.4),
                Child(GroupActive("c", "c"), 0.3)),
            CurrentTabId = "a",
        });

        var result = state.SplitTab("x", SplitDirection.Right);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.3, 0.2, 0.2, 0.3);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "y", "y");
        AssertGroup(root.Children[2].Node, "x", "x");
        AssertGroup(root.Children[3].Node, "c", "c");
    }

    [Fact]
    public void DA_E1_split_of_a_single_tab_root_group_restores_the_state()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("x", "x"), CurrentTabId = "x" });

        var result = state.SplitTab("x", SplitDirection.Right);

        AssertGroup(result.DockArea.Root, "x", "x");
        Assert.Equal("x", result.DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_E1_split_of_a_single_tab_group_keeps_the_neighbour_shares()
    {
        // Вариант B спеки v0.5: опустевший донор отдаёт место и полную долю новой группе —
        // доли соседей не пересчитываются (было бы ⅔/⅓ при распиле пополам и перенормировке).
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a"), 0.5), Child(GroupActive("x", "x"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.SplitTab("x", SplitDirection.Right);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "x", "x");
        Assert.Equal("x", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_5_left_inserts_before_along_the_axis()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("x", "x", "y"), 0.6), Child(GroupActive("c", "c"), 0.4)),
            CurrentTabId = "c",
        });

        var result = state.SplitTab("x", SplitDirection.Left);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.3, 0.3, 0.4);
        AssertGroup(root.Children[0].Node, "x", "x");
        AssertGroup(root.Children[1].Node, "y", "y");
        AssertGroup(root.Children[2].Node, "c", "c");
        Assert.Equal("x", result.DockArea.CurrentTabId);
    }

    [Fact]
    public void DA_5_5_up_places_the_new_group_first_across_the_axis()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a"), 0.5), Child(GroupActive("x", "x", "y"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.SplitTab("x", SplitDirection.Up);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        var column = AssertSplit(root.Children[1].Node, SplitOrientation.Column, 0.5, 0.5);
        AssertGroup(column.Children[0].Node, "x", "x");
        AssertGroup(column.Children[1].Node, "y", "y");
    }

    [Fact]
    public void DA_E24_repeated_splits_produce_tiny_but_valid_shares()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("t0", "t0", "t1", "t2", "t3"), 0.5), Child(GroupActive("c", "c"), 0.5)),
            CurrentTabId = "c",
        });

        var result = state.SplitTab("t0", SplitDirection.Right).SplitTab("t1", SplitDirection.Right).SplitTab("t2", SplitDirection.Right);

        var root = Assert.IsType<SplitNode>(result.DockArea.Root);
        Assert.Equal(0.0625, root.Children[0].Share, precision: 12);
        Assert.Empty(LayoutInvariants.Validate(result, new ToolWindowRegistry()));
    }

    // ---- DA-5.6 SetSplitShares ----

    [Fact]
    public void DA_5_6_sets_the_share_vector_exactly()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.SetSplitShares(DockHost.MainWindow, [], [0.3, 0.7]);

        AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.3, 0.7);
    }

    [Fact]
    public void DA_5_6_addresses_nested_splits_by_path()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(Group("a"), 0.5),
                Child(Column(Child(Group("b"), 0.5), Child(Group("c"), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.SetSplitShares(DockHost.MainWindow, [1], [0.6, 0.4]);

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertSplit(root.Children[1].Node, SplitOrientation.Column, 0.6, 0.4);
    }

    [Fact]
    public void DA_5_6_invalid_vectors_and_paths_throw()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.5), Child(Group("b"), 0.5)),
            CurrentTabId = "a",
        });

        Assert.Throws<ArgumentException>(() => state.SetSplitShares(DockHost.MainWindow, [], [0.3, 0.3, 0.4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetSplitShares(DockHost.MainWindow, [], [0.0, 1.0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetSplitShares(DockHost.MainWindow, [], [1.5, -0.5]));
        Assert.Throws<ArgumentException>(() => state.SetSplitShares(DockHost.MainWindow, [], [0.3, 0.3]));
        Assert.Throws<ArgumentException>(() => state.SetSplitShares(DockHost.MainWindow, [5], [0.5, 0.5]));
        Assert.Throws<ArgumentException>(() => state.SetSplitShares(DockHost.MainWindow, [0], [0.5, 0.5]));
        Assert.Throws<ArgumentException>(() => state.SetSplitShares(DockHost.DocumentWindow(3), [], [0.5, 0.5]));
    }

    // ---- DA-5.7 MoveTabToNewWindow ----

    [Fact]
    public void DA_5_7_creates_a_window_at_the_end_of_the_list()
    {
        var other = new DocumentWindowState(Bounds, GroupActive("w", "w"), "w");
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("a", "a", "b"),
            CurrentTabId = "a",
            Windows = [other],
        });
        var bounds = new FloatingBounds(1, 2, 300, 200);

        var result = state.MoveTabToNewWindow("b", bounds);

        Assert.Equal(2, result.DockArea.Windows.Length);
        Assert.Same(other, result.DockArea.Windows[0]);
        var window = result.DockArea.Windows[1];
        Assert.Equal(bounds, window.Bounds);
        AssertGroup(window.Root, "b", "b");
        Assert.Equal("b", window.CurrentTabId);
        AssertGroup(result.DockArea.Root, "a", "a");
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_5_7_donor_current_tab_falls_back()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a", "b"), CurrentTabId = "a" });

        var result = state.MoveTabToNewWindow("a", Bounds);

        Assert.Equal("b", result.DockArea.CurrentTabId);
        Assert.Equal("a", result.DockArea.Windows[0].CurrentTabId);
    }

    [Fact]
    public void DA_5_7_sole_tab_of_a_window_moves_to_a_new_window()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("x", "x"), "x")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });
        var bounds = new FloatingBounds(5, 6, 400, 300);

        var result = state.MoveTabToNewWindow("x", bounds);

        var window = Assert.Single(result.DockArea.Windows);
        Assert.Equal(bounds, window.Bounds);
        Assert.Equal("x", window.CurrentTabId);
        // Прежний активный хост исчез — активным становится главное окно (DA-6.3).
        Assert.Equal(DockHost.MainWindow, result.DockArea.ActiveDockHost);
    }

    [Fact]
    public void DA_5_7_rejects_non_finite_bounds() // TW-5.9
    {
        var state = Layout(new DockAreaState { Root = GroupActive("m", "m"), CurrentTabId = "m" });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.MoveTabToNewWindow("m", new FloatingBounds(0, double.NaN, 100, 100)));
    }

    // ---- DA-5.8 SetDocumentWindowBounds ----

    [Fact]
    public void DA_5_8_updates_the_bounds_of_the_containing_window()
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("x", "x", "y"), "x")],
        });
        var bounds = new FloatingBounds(50, 60, 640, 480);

        var result = state.SetDocumentWindowBounds("y", bounds);

        Assert.Equal(bounds, result.DockArea.Windows[0].Bounds);
        Assert.Same(state.DockArea.Windows[0].Root, result.DockArea.Windows[0].Root);
    }

    [Fact]
    public void DA_5_8_tab_in_the_main_window_throws()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("m", "m"), CurrentTabId = "m" });

        Assert.Throws<ArgumentException>(() => state.SetDocumentWindowBounds("m", Bounds));
    }

    [Fact]
    public void DA_5_8_rejects_non_finite_bounds() // TW-5.9
    {
        var state = Layout(new DockAreaState
        {
            Root = GroupActive("m", "m"),
            CurrentTabId = "m",
            Windows = [new DocumentWindowState(Bounds, GroupActive("x", "x"), "x")],
        });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.SetDocumentWindowBounds("x", new FloatingBounds(0, 0, double.PositiveInfinity, 100)));
    }

    // ---- DA-5.9 RotateSplit ----

    [Fact]
    public void DA_E25_rotation_keeps_order_and_shares()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(Child(GroupActive("a", "a"), 0.3), Child(GroupActive("b", "b"), 0.7)),
            CurrentTabId = "a",
        });

        var result = state.RotateSplit("a");

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Column, 0.3, 0.7);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "b");
    }

    [Fact]
    public void DA_E26_rotating_the_root_merges_the_coinciding_child()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.5),
                Child(Column(Child(GroupActive("b", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.RotateSplit("a");

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Column, 0.5, 0.25, 0.25);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "b");
        AssertGroup(root.Children[2].Node, "c", "c");
    }

    [Fact]
    public void DA_E31_rotating_an_inner_split_merges_into_the_parent()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.5),
                Child(Column(Child(GroupActive("b", "b"), 0.5), Child(GroupActive("c", "c"), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.RotateSplit("b");

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.25, 0.25);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "b");
        AssertGroup(root.Children[2].Node, "c", "c");
    }

    [Fact]
    public void DA_E32_rotating_an_inner_split_merges_both_boundaries()
    {
        var state = Layout(new DockAreaState
        {
            Root = Row(
                Child(GroupActive("a", "a"), 0.5),
                Child(Column(
                    Child(GroupActive("b", "b"), 0.4),
                    Child(Row(Child(GroupActive("c", "c"), 0.5), Child(GroupActive("d", "d"), 0.5)), 0.6)), 0.5)),
            CurrentTabId = "a",
        });

        var result = state.RotateSplit("b");

        var root = AssertSplit(result.DockArea.Root, SplitOrientation.Row, 0.5, 0.2, 0.15, 0.15);
        AssertGroup(root.Children[0].Node, "a", "a");
        AssertGroup(root.Children[1].Node, "b", "b");
        AssertGroup(root.Children[2].Node, "c", "c");
        AssertGroup(root.Children[3].Node, "d", "d");
    }

    [Fact]
    public void DA_5_9_rotating_a_root_group_throws()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("x", "x"), CurrentTabId = "x" });

        Assert.Throws<ArgumentException>(() => state.RotateSplit("x"));
    }

    // ---- errors and composition ----

    [Fact]
    public void Operation_on_an_unknown_tab_throws()
    {
        var state = Layout(new DockAreaState { Root = GroupActive("a", "a"), CurrentTabId = "a" });

        Assert.Throws<ArgumentException>(() => state.CloseTab("ghost"));
        Assert.Throws<ArgumentException>(() => state.ActivateTab("ghost"));
        Assert.Throws<ArgumentException>(() => state.MoveTab("ghost", DockGroupRef.AtTab("a"), 0, EmptyRegistry));
        Assert.Throws<ArgumentException>(() => state.MoveTab("a", DockGroupRef.AtTab("ghost"), 0, EmptyRegistry));
        Assert.Throws<ArgumentException>(() => state.SplitTab("ghost", SplitDirection.Right));
        Assert.Throws<ArgumentException>(() => state.MoveTabToNewWindow("ghost", Bounds));
        Assert.Throws<ArgumentException>(() => state.SetDocumentWindowBounds("ghost", Bounds));
        Assert.Throws<ArgumentException>(() => state.RotateSplit("ghost"));
    }

    [Fact]
    public void Operations_keep_invariants_on_a_worked_example()
    {
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor("p", "P", LeftPrimary));
        var state = WithPanel(LayoutState.Empty, "p", open: false, active: false);

        var result = state
            .OpenDocument("a", EmptyRegistry)
            .OpenDocument("b", EmptyRegistry)
            .SplitTab("b", SplitDirection.Right)
            .OpenDocument("c", EmptyRegistry)
            .MoveTab("a", DockGroupRef.AtTab("c"), 0, EmptyRegistry)
            .MoveTabToNewWindow("c", Bounds)
            .Open("p")
            .OpenDocument("d", EmptyRegistry)
            .SplitTab("d", SplitDirection.Down)
            .RotateSplit("d")
            .CloseTab("b");

        Assert.Empty(LayoutInvariants.Validate(result, registry));
    }
}
