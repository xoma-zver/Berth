using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Input wiring of the dock area to core commands (spec DA-5.2, DA-5.3, DA-5.5, DA-5.6,
/// DA-5.9, DA-8.2, TW-6.5; ADR-0004): header clicks activate, the «×» button and middle
/// clicks close, the tab menu splits, rotates and moves to the owner panel, and split
/// splitter drags commit one SetSplitShares on release. Menu items are invoked by raising
/// their Click event — the wiring under test is «item → core command».
/// </summary>
public class DockAreaInputTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static TabGroupNode RootGroup(Window window) =>
        Assert.IsType<TabGroupNode>(St(window).DockArea.Root);

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static LayoutState DockState(TabTreeNode root, string? current) =>
        LayoutState.Empty with { DockArea = new DockAreaState { Root = root, CurrentTabId = current } };

    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, CountingTabFactory Docs) DockSetup(
        Func<string, object>? create = null)
    {
        var registry = new ToolWindowRegistry();
        var docs = new CountingTabFactory("d", create);
        registry.RegisterDockContent(docs);
        return (registry, new ContentLifecycle(registry), docs);
    }

    private static MenuItem Item(ItemCollection items, string header) =>
        items.OfType<MenuItem>().First(i => string.Equals(i.Header as string, header, StringComparison.Ordinal));

    private static void Invoke(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ---- DA-5.3: header clicks ----

    [AvaloniaFact]
    public void DA_5_3_header_click_activates_and_focuses_the_content()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, _) = DockSetup(id => boxes[id] = new TextBox());
        var state = DockState(
            Row(Child(Group("d1", "d1", "d2"), 0.5), Child(Group("d3", "d3"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        Click(window, TabHeader(window, "d3"));

        Assert.Equal("d3", St(window).DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, St(window).DockArea.ActiveDockHost);
        Assert.True(boxes["d3"].IsFocused); // жест перенёс фокус в контент (DA-6.4)
    }

    [AvaloniaFact]
    public void DA_5_3_click_on_an_inactive_tab_swaps_the_hosts()
    {
        var (registry, lifecycle, _) = DockSetup(_ => new TextBox());
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        Click(window, TabHeader(window, "d2"));

        Assert.Equal(["d2"], TabHosts(window).Select(h => h.TabId)); // хост d1 ушёл в кэш
        Assert.Equal("d2", RootGroup(window).ActiveTabId);
        // Фокус перенесён немедленно — контент придёт лениво, цель — сам хост (фолбэк DA-6.4).
        Assert.True(TabHost(window, "d2").IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void TW_6_5_dock_activation_clears_the_active_tool_window()
    {
        var (registry, lifecycle, _) = DockSetup(_ => new TextBox());
        registry.Register(new ToolWindowDescriptor(
            "p", "P", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        var state = DockState(Group("d1", "d1"), "d1") with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
            ActiveToolWindowId = "p",
        };
        var window = Show(state, registry, lifecycle: lifecycle);

        Click(window, TabHeader(window, "d1"));

        Assert.Null(St(window).ActiveToolWindowId); // TW-6.5
        Assert.True(St(window).ToolWindows[0].IsOpen); // панель осталась открытой
        Assert.DoesNotContain(":active", Decorator(window, "p").Classes);
    }

    // ---- DA-5.2: closing ----

    [AvaloniaFact]
    public void DA_5_2_close_button_closes_and_releases_once()
    {
        var (registry, lifecycle, docs) = DockSetup(_ => new TextBox());
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Assert.Equal(1, docs.Created);

        var close = TabHeader(window, "d1").GetVisualDescendants().OfType<Button>().Single();
        Click(window, close);

        Assert.Equal(["d2"], RootGroup(window).Tabs);
        Assert.Equal("d2", St(window).DockArea.CurrentTabId); // фолбэк DA-5.2
        Assert.Equal(1, docs.Released); // один жест — одно освобождение (TW-9.2)
    }

    [AvaloniaFact]
    public void DA_5_2_middle_click_closes_the_tab()
    {
        var (registry, lifecycle, _) = DockSetup();
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        var point = Center(TabHeader(window, "d2"), window);
        window.MouseDown(point, MouseButton.Middle);
        window.MouseUp(point, MouseButton.Middle);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["d1"], RootGroup(window).Tabs);
    }

    // ---- DA-5.5 / DA-5.9: the tab menu ----

    [AvaloniaFact]
    public void DA_5_5_split_menu_splits_and_focuses_the_moved_tab()
    {
        var (registry, lifecycle, _) = DockSetup();
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        var flyout = (MenuFlyout)TabHeader(window, "d2").ContextFlyout!;
        Invoke(Item(flyout.Items, "Split Right"));

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(SplitOrientation.Row, root.Orientation);
        Assert.Equal(2, root.Children.Length);
        Assert.Equal("d2", St(window).DockArea.CurrentTabId);
        Assert.True(TabHost(window, "d2").IsKeyboardFocusWithin); // жест активации (DA-6.4)
    }

    [AvaloniaFact]
    public void DA_5_9_rotate_menu_rotates_and_the_root_group_has_no_item()
    {
        var (registry, lifecycle, _) = DockSetup();
        var split = DockState(Row(Child(Group("d1", "d1"), 0.3), Child(Group("d2", "d2"), 0.7)), "d1");
        var window = Show(split, registry, lifecycle: lifecycle);

        Invoke(Item(((MenuFlyout)TabHeader(window, "d1").ContextFlyout!).Items, "Rotate Split"));

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(SplitOrientation.Column, root.Orientation); // DA-E25: порядок и доли целы
        Assert.Equal(0.3, root.Children[0].Share, precision: 12);
        var host1 = BoundsIn(TabHost(window, "d1"), window);
        var host2 = BoundsIn(TabHost(window, "d2"), window);
        Assert.True(host1.Bottom <= host2.Y + 1); // теперь стопка

        // У корневой группы родителя-сплита нет — пункта поворота нет (DA-5.9). Отдельное
        // окно без жизненного цикла: контент первого окна не должен переехать во второе.
        var rootGroup = Show(DockState(Group("d1", "d1"), "d1"), new ToolWindowRegistry());
        var items = ((MenuFlyout)TabHeader(rootGroup, "d1").ContextFlyout!).Items;
        Assert.DoesNotContain(
            items.OfType<MenuItem>(),
            i => string.Equals(i.Header as string, "Rotate Split", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void DA_8_2_move_to_the_owner_panel_via_the_menu()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        registry.Register(new ToolWindowDescriptor(
            "p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = new CountingTabFactory("p:"),
        });
        var lifecycle = new ContentLifecycle(registry);
        var state = DockState(Group("d1", "d1", "p:t1"), "d1") with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry, lifecycle: lifecycle);

        // У документа пункта Move нет — владелец не панель (DA-8.1).
        var documentItems = ((MenuFlyout)TabHeader(window, "d1").ContextFlyout!).Items;
        Assert.DoesNotContain(
            documentItems.OfType<MenuItem>(),
            i => (i.Header as string)?.StartsWith("Move to", StringComparison.Ordinal) == true);

        Invoke(Item(((MenuFlyout)TabHeader(window, "p:t1").ContextFlyout!).Items, "Move to Panel"));

        Assert.Equal(["d1"], RootGroup(window).Tabs);
        var panelTree = Assert.IsType<TabGroupNode>(St(window).ToolWindows[0].ContentTree);
        Assert.Equal(["p:t1"], panelTree.Tabs); // перенос в дерево владельца (DA-8.2)
        Assert.Empty(LayoutInvariants.Validate(St(window), registry));
    }

    // ---- DA-5.6: split splitters ----

    [AvaloniaFact]
    public void DA_5_6_splitter_commits_one_command_on_release_only()
    {
        var (registry, lifecycle, _) = DockSetup();
        var state = DockState(Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(window);
        var initial = workspace.State!;
        var w1 = TabHost(window, "d1").Bounds.Width;
        var w2 = TabHost(window, "d2").Bounds.Width;

        var start = Center(Part(window, "PART_DockSplitter"), window);
        var end = new Point(start.X + 100, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        Dispatcher.UIThread.RunJobs();

        // Живой драг — чистая визуализация (ADR-0004): состояние не тронуто.
        Assert.Same(initial, workspace.State);

        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal((w1 + 100) / (w1 + w2), root.Children[0].Share, 0.02);
        Assert.Equal(1.0, root.Children[0].Share + root.Children[1].Share, precision: 9);
    }

    [AvaloniaFact]
    public void DA_5_6_click_without_movement_commits_nothing()
    {
        var (registry, lifecycle, _) = DockSetup();
        var state = DockState(Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        var initial = Workspace(window).State;

        Click(window, Part(window, "PART_DockSplitter"));

        Assert.Same(initial, Workspace(window).State);
    }

    [AvaloniaFact]
    public void DA_5_6_three_children_change_only_the_adjacent_pair()
    {
        var (registry, lifecycle, _) = DockSetup();
        var state = DockState(
            Row(Child(Group("d1", "d1"), 0.2), Child(Group("d2", "d2"), 0.4), Child(Group("d3", "d3"), 0.4)),
            "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        var splitter = window.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => string.Equals(c.Name, "PART_DockSplitter", StringComparison.Ordinal))
            .OrderBy(c => BoundsIn(c, window).X)
            .First();
        var start = Center(splitter, window);
        var end = new Point(start.X + 50, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.True(root.Children[0].Share > 0.2); // пара перераспределена…
        Assert.Equal(0.4, root.Children[2].Share, precision: 12); // …третья доля не тронута (DA-5.6)
        Assert.Equal(1.0, root.Children.Sum(c => c.Share), precision: 9);
    }
}
