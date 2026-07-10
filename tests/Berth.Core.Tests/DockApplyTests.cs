using Xunit;

namespace Berth.Core.Tests;

/// <summary>
/// The dock-area side of Apply (spec DA-9.1…DA-9.5): deduplication with the report, N1–N5
/// repairs, removal of emptied document windows, bounds validation; edge cases
/// DA-E9/E10/E15/E23/E27/E34 (DA-E28 — DockOperationsTests).
/// </summary>
public class DockApplyTests
{
    private static readonly FloatingBounds Bounds = new(10, 20, 800, 600);

    private static TabGroupNode Group(params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = tabs.Length == 0 ? null : tabs[0] };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static DocumentWindowState Window(TabTreeNode root, string currentTabId) =>
        new(Bounds, root, currentTabId);

    private static LayoutState Layout(DockAreaState area) => LayoutState.Empty with { DockArea = area };

    private static ApplyResult Apply(LayoutState snapshot, BoundsValidator? validateBounds = null) =>
        LayoutState.Empty.Apply(snapshot, ApplyScope.Full, new ToolWindowRegistry(), validateBounds);

    private static string[] FixRules(ApplyResult result) => result.Fixes.Select(f => f.Rule).ToArray();

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

    [Fact]
    public void DA_E9_duplicate_between_groups_keeps_the_first_occurrence()
    {
        var snapshot = Layout(new DockAreaState
        {
            Root = Row(
                Child(new TabGroupNode { Tabs = ["x", "a"], ActiveTabId = "x" }, 0.5),
                Child(new TabGroupNode { Tabs = ["x", "b"], ActiveTabId = "x" }, 0.5)),
            CurrentTabId = "x",
        });

        var result = Apply(snapshot);

        var root = AssertSplit(result.State.DockArea.Root, SplitOrientation.Row, 0.5, 0.5);
        AssertGroup(root.Children[0].Node, "x", "x", "a");
        // Повисшая активная вкладка второй группы — вторичная починка N5, без своей записи.
        AssertGroup(root.Children[1].Node, "b", "b");
        Assert.Equal("x", result.State.DockArea.CurrentTabId);
        Assert.Equal(["DA-9.2"], FixRules(result));
    }

    [Fact]
    public void DA_E10_share_sum_is_renormalized_with_a_report()
    {
        var snapshot = Layout(new DockAreaState
        {
            Root = Row(Child(Group("a"), 0.9), Child(Group("b"), 0.6)),
            CurrentTabId = "a",
        });

        var result = Apply(snapshot);

        AssertSplit(result.State.DockArea.Root, SplitOrientation.Row, 0.6, 0.4);
        Assert.Equal(["INV-D3"], FixRules(result));
    }

    [Fact]
    public void DA_E15_offscreen_window_bounds_are_replaced_by_the_validator()
    {
        var replacement = new FloatingBounds(120, 120, 400, 300);
        var snapshot = Layout(new DockAreaState
        {
            Windows = [new DocumentWindowState(new FloatingBounds(9999, 9999, 100, 100), Group("w"), "w")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = Apply(snapshot, bounds => bounds.X > 5000 ? replacement : null);

        Assert.Equal(replacement, result.State.DockArea.Windows[0].Bounds);
        Assert.Equal(["DA-7.4"], FixRules(result));
    }

    [Fact]
    public void DA_E23_non_numeric_share_renormalizes_only_its_node()
    {
        var json = """
            {
              "schemaVersion": 1,
              "dockArea": {
                "root": {
                  "type": "split",
                  "orientation": "row",
                  "children": [
                    { "share": "abc", "node": { "type": "group", "tabs": ["a"], "activeTabId": "a" } },
                    { "share": 0.25, "node": {
                        "type": "split",
                        "orientation": "column",
                        "children": [
                          { "share": 0.3, "node": { "type": "group", "tabs": ["b"], "activeTabId": "b" } },
                          { "share": 0.7, "node": { "type": "group", "tabs": ["c"], "activeTabId": "c" } }
                        ] } }
                  ]
                },
                "currentTabId": "a"
              }
            }
            """;

        var result = Apply(LayoutPersistence.Deserialize(json));

        // NaN → равная доля 1/2, вектор [0.5, 0.25] перенормирован к сумме 1 целиком (N4)…
        var root = AssertSplit(result.State.DockArea.Root, SplitOrientation.Row, 2.0 / 3, 1.0 / 3);
        // …а соседний узел не тронут.
        AssertSplit(root.Children[1].Node, SplitOrientation.Column, 0.3, 0.7);
        Assert.Equal("a", result.State.DockArea.CurrentTabId);
        Assert.Equal(["INV-D3"], FixRules(result));
    }

    [Fact]
    public void DA_E27_window_of_sleeping_tabs_is_restored_as_is()
    {
        // Владелец вкладок не зарегистрирован — для структуры это обычные вкладки (DA-9.4).
        var snapshot = Layout(new DockAreaState
        {
            Windows = [Window(new TabGroupNode { Tabs = ["p:t1", "p:t2"], ActiveTabId = "p:t1" }, "p:t1")],
        });

        var result = Apply(snapshot);

        var window = Assert.Single(result.State.DockArea.Windows);
        AssertGroup(window.Root, "p:t1", "p:t1", "p:t2");
        Assert.Equal("p:t1", window.CurrentTabId);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void DA_E34_dedup_then_window_removal_reports_both_fixes()
    {
        var snapshot = Layout(new DockAreaState
        {
            Root = Group("x"),
            CurrentTabId = "x",
            Windows = [Window(Group("x"), "x")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = Apply(snapshot);

        AssertGroup(result.State.DockArea.Root, "x", "x"); // первое вхождение — в главном окне
        Assert.Empty(result.State.DockArea.Windows); // опустевшее окно исчезло
        Assert.Equal(DockHost.MainWindow, result.State.DockArea.ActiveDockHost); // вторичный ремап без записи
        Assert.Equal(["DA-9.2", "INV-D6"], FixRules(result));
    }

    [Fact]
    public void DA_9_2_secondary_current_tab_reassignment_has_no_entry_of_its_own()
    {
        var snapshot = Layout(new DockAreaState
        {
            Root = Group("x"),
            CurrentTabId = "x",
            Windows = [Window(new TabGroupNode { Tabs = ["x", "y"], ActiveTabId = "x" }, "x")],
        });

        var result = Apply(snapshot);

        AssertGroup(result.State.DockArea.Windows[0].Root, "y", "y");
        Assert.Equal("y", result.State.DockArea.Windows[0].CurrentTabId);
        Assert.Equal(["DA-9.2"], FixRules(result));
    }

    [Fact]
    public void DA_9_2_dangling_current_tab_is_repaired_with_a_report()
    {
        var snapshot = Layout(new DockAreaState { Root = Group("a"), CurrentTabId = "ghost" });

        var result = Apply(snapshot);

        Assert.Equal("a", result.State.DockArea.CurrentTabId);
        Assert.Equal(["INV-D4"], FixRules(result));
    }

    [Fact]
    public void DA_9_2_invalid_active_dock_host_falls_back_to_the_main_window()
    {
        var snapshot = Layout(new DockAreaState { ActiveDockHost = DockHost.DocumentWindow(3) });

        var result = Apply(snapshot);

        Assert.Equal(DockHost.MainWindow, result.State.DockArea.ActiveDockHost);
        Assert.Equal(["INV-D4"], FixRules(result));
    }

    [Fact]
    public void DA_9_2_structural_defects_are_repaired_with_a_report()
    {
        // Одноориентационная вложенность — дефект каноничности (INV-D1), чинит N3.
        var snapshot = Layout(new DockAreaState
        {
            Root = Row(
                Child(Group("a"), 0.5),
                Child(Row(Child(Group("b"), 0.5), Child(Group("c"), 0.5)), 0.5)),
            CurrentTabId = "a",
        });

        var result = Apply(snapshot);

        var root = AssertSplit(result.State.DockArea.Root, SplitOrientation.Row, 0.5, 0.25, 0.25);
        AssertGroup(root.Children[0].Node, "a", "a");
        Assert.Equal(["INV-D1"], FixRules(result));
    }

    [Fact]
    public void DA_9_2_window_already_empty_in_the_snapshot_is_removed()
    {
        var snapshot = Layout(new DockAreaState
        {
            Windows = [Window(TabGroupNode.Empty, "stale")],
            ActiveDockHost = DockHost.DocumentWindow(0),
        });

        var result = Apply(snapshot);

        Assert.Empty(result.State.DockArea.Windows);
        Assert.Equal(DockHost.MainWindow, result.State.DockArea.ActiveDockHost);
        Assert.Contains("INV-D6", FixRules(result));
    }

    // ---- INV-D5: panel trees at Apply (задача 1.8) ----

    private static readonly ToolWindowSlot LeftPrimary = new(ToolWindowSide.Left, ToolWindowGroup.Primary);

    private static ToolWindowState Panel(LayoutState state, string id) =>
        state.ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    [Fact]
    public void DA_E36_confirmed_foreign_tab_relocates_from_a_panel_tree_with_a_report()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc"));
        var snapshot = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with
                {
                    ContentTree = new TabGroupNode { Tabs = ["s1", "doc1"], ActiveTabId = "s1" },
                },
            ],
            DockArea = new DockAreaState
            {
                Root = new TabGroupNode { Tabs = ["m1", "m2"], ActiveTabId = "m1" },
                CurrentTabId = "m1",
            },
        };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        // doc1 — в конец текущей группы главного окна; активность не переназначена (DA-9.2).
        AssertGroup(result.State.DockArea.Root, "m1", "m1", "m2", "doc1");
        Assert.Equal("m1", result.State.DockArea.CurrentTabId);
        AssertGroup(Panel(result.State, "p").ContentTree, "s1", "s1"); // спящая s1 осталась
        Assert.Equal(["INV-D5"], FixRules(result));
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }

    [Fact]
    public void DA_9_2_relocation_into_the_empty_main_tree_forces_the_assignment()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc"));
        var snapshot = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with { ContentTree = Group("doc1") },
            ],
        };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        // Пустое дерево получило вкладку корневой группой; назначение активности вынужденное
        // (INV-D4), отдельной записи не порождает.
        AssertGroup(result.State.DockArea.Root, "doc1", "doc1");
        Assert.Equal("doc1", result.State.DockArea.CurrentTabId);
        AssertGroup(Panel(result.State, "p").ContentTree, null);
        Assert.Equal(["INV-D5"], FixRules(result));
    }

    [Fact]
    public void TW_9_11_conflicted_claim_keeps_the_tab_in_place_at_apply()
    {
        // Конфликт заявок владельца не подтверждает: Apply не бросает и не чинит — ошибка
        // приложения всплывёт при операции или материализации (TW-9.11).
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc"));
        registry.RegisterDockContent(new StubTabFactory("doc"));
        var snapshot = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with { ContentTree = Group("doc1") },
            ],
        };

        var result = LayoutState.Empty.Apply(snapshot, ApplyScope.Full, registry);

        AssertGroup(Panel(result.State, "p").ContentTree, "doc1", "doc1");
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void DA_9_2_deduplication_spans_panel_trees()
    {
        var snapshot = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("p", LeftPrimary, 0) with { ContentTree = Group("x") },
            ],
            DockArea = new DockAreaState { Root = Group("x"), CurrentTabId = "x" },
        };

        var result = Apply(snapshot);

        AssertGroup(result.State.DockArea.Root, "x", "x"); // первое вхождение — главное окно
        AssertGroup(Panel(result.State, "p").ContentTree, null);
        Assert.Equal(["DA-9.2"], FixRules(result));
    }

    [Fact]
    public void TW_10_7_new_sleeping_tree_is_cleaned_in_the_arrangement_scope()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new StubTabFactory("doc"));
        var current = LayoutState.Empty with
        {
            DockArea = new DockAreaState { Root = Group("m"), CurrentTabId = "m" },
        };
        var macro = LayoutState.Empty with
        {
            ToolWindows =
            [
                new ToolWindowState("s", LeftPrimary, 0) with
                {
                    // Дубликат текущей раскладки, незаявленная и подтверждённо чужая вкладки.
                    ContentTree = new TabGroupNode { Tabs = ["m", "z", "doc1"], ActiveTabId = "z" },
                },
            ],
        };

        var result = current.Apply(macro, ApplyScope.Arrangement, registry);

        Assert.Same(current.DockArea, result.State.DockArea); // док-зона неприкосновенна (DA-9.1)
        AssertGroup(Panel(result.State, "s").ContentTree, "z", "z");
        Assert.Equal(["DA-9.2", "INV-D5"], FixRules(result));
        Assert.Empty(LayoutInvariants.Validate(result.State, registry));
    }
}
