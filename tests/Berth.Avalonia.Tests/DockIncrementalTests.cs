using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Incremental materialization of the dock area (spec DA-9.6, the mirror of TW-9.13): hosts
/// of remaining tabs are neither recreated nor reattached by unrelated changes — keyboard
/// focus and view-state survive; reattachment happens only by command semantics, and the
/// command channel restores focus it dropped; built views survive activation switches and
/// moves out of the materialized area; the refusal path and sleeping tabs resolve through
/// the out-of-render pull pass. Reattachment is observed through DetachedFromVisualTree.
/// </summary>
public class DockIncrementalTests
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
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static Func<int> TrackDetach(Control control)
    {
        var count = 0;
        control.DetachedFromVisualTree += (_, _) => count++;
        return () => count;
    }

    private static void Focus(Control control)
    {
        Assert.True(control.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record TabModel(string Id);

    [AvaloniaFact]
    public void DA_9_6_unrelated_changes_do_not_reattach_tab_hosts()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, _) = DockSetup(id => boxes[id] = new TextBox());
        registry.Register(new ToolWindowDescriptor(
            "p", "P", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        var state = DockState(Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2"), 0.5)), "d1") with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        var detach1 = TrackDetach(TabHost(window, "d1"));
        var detach2 = TrackDetach(TabHost(window, "d2"));
        Focus(boxes["d1"]);

        // Прямое присвоение: ресайз стороны — только перекладка геометрии.
        Workspace(window).State = St(window).SetSideSize(ToolWindowSide.Left, 0.4);
        Dispatcher.UIThread.RunJobs();
        Assert.True(boxes["d1"].IsFocused);

        // Полный канал: открытие панели (активация уводит фокус — семантика команды),
        // затем её закрытие возвращает фокус в текущую вкладку (DA-E21).
        Click(window, Button(window, "p"));
        Click(window, Button(window, "p"));

        Assert.Equal(0, detach1());
        Assert.Equal(0, detach2());
        Assert.True(boxes["d1"].IsFocused);
    }

    [AvaloniaFact]
    public void DA_9_6_split_of_a_neighbour_group_keeps_the_host_attached()
    {
        var (registry, lifecycle, _) = DockSetup(_ => new TextBox());
        var state = DockState(Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2", "d3"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        var detach1 = TrackDetach(TabHost(window, "d1"));

        // Сплит активной вкладки соседней группы перестраивает только её узел: вставка
        // соседа вдоль оси не переприсоединяет пережившие поддеревья (DA-9.6).
        Invoke(Item(((MenuFlyout)TabHeader(window, "d2").ContextFlyout!).Items, "Split and Move Right"));

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(3, root.Children.Length);
        Assert.Equal(0, detach1());
    }

    [AvaloniaFact]
    public void DA_9_6_view_built_once_survives_switch_away_and_back()
    {
        var (registry, lifecycle, docs) = DockSetup(id => new TabModel(id));
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = new Window { Width = 800, Height = 600 };
        window.DataTemplates.Add(new FuncDataTemplate<TabModel>((model, _) =>
            new TextBlock { Text = model.Id }));
        window.Content = new BerthWorkspace { State = state, Registry = registry, Lifecycle = lifecycle };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = Assert.IsType<TextBlock>(TabHost(window, "d1").Child);
        Assert.Equal("d1", view.Text); // вид построен шаблоном приложения (MVVM-путь)

        Click(window, TabHeader(window, "d2")); // d1 ушла в кэш вместе с построенным видом
        Click(window, TabHeader(window, "d1")); // и вернулась

        Assert.Same(view, TabHost(window, "d1").Child); // «один раз построили» (DA-9.6)
        Assert.Equal(2, docs.Created); // по разу на вкладку, без пересозданий
    }

    [AvaloniaFact]
    public void DA_9_6_own_split_restores_focus_into_the_moved_tab()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, _) = DockSetup(id => boxes[id] = new TextBox());
        var state = DockState(Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxes["d1"]);

        // Сплит собственной группы — белосписочная перестройка адресованного узла: хост
        // переприсоединяется, командный канал возвращает фокус в контент (DA-9.6).
        Invoke(Item(((MenuFlyout)TabHeader(window, "d1").ContextFlyout!).Items, "Split and Move Down"));

        var root = Assert.IsType<SplitNode>(St(window).DockArea.Root);
        Assert.Equal(SplitOrientation.Column, root.Orientation);
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
        Assert.True(boxes["d1"].IsFocused);
    }

    [AvaloniaFact]
    public void DA_5_9_rotate_reattaches_nothing_and_keeps_focus()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, _) = DockSetup(id => boxes[id] = new TextBox());
        var state = DockState(Row(Child(Group("d1", "d1"), 0.3), Child(Group("d2", "d2"), 0.7)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        var detach1 = TrackDetach(TabHost(window, "d1"));
        var detach2 = TrackDetach(TabHost(window, "d2"));
        Focus(boxes["d1"]);

        Invoke(Item(((MenuFlyout)TabHeader(window, "d1").ContextFlyout!).Items, "Rotate Split"));

        // Поворот меняет только определения грида: дети размещаются индексами и не
        // переприсоединяются вовсе — строже белого списка (DA-9.6).
        Assert.Equal(0, detach1());
        Assert.Equal(0, detach2());
        Assert.True(boxes["d1"].IsFocused);
        Assert.True(
            BoundsIn(TabHost(window, "d1"), window).Bottom
                <= BoundsIn(TabHost(window, "d2"), window).Y + 1);
    }

    [AvaloniaFact]
    public void DA_9_3_refusal_closes_the_tab_through_the_pull_pass()
    {
        var (registry, lifecycle, docs) = DockSetup(_ => new TextBox());
        docs.Refuse = id => string.Equals(id, "d-bad", StringComparison.Ordinal);
        var state = DockState(Group("d-bad", "d1", "d-bad"), "d-bad");

        var window = Show(state, registry, lifecycle: lifecycle);

        // Отказ обработан вне прохода проекции: вкладка закрыта штатной CloseTab (DA-9.3),
        // раскладка перечитана, выживший сосед материализован.
        Assert.Equal(["d1"], RootGroup(window).Tabs);
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
        Assert.Equal(1, docs.Created);
        Assert.Empty(LayoutInvariants.Validate(St(window), registry));
    }

    [AvaloniaFact]
    public void DA_9_4_registration_plus_refresh_wakes_the_sleeping_tab()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = DockState(Group("x1", "x1"), "x1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Assert.IsType<TextBlock>(TabHost(window, "x1").Child); // заглушка спящей (DA-9.4)

        var docs = new CountingTabFactory("x", _ => new TextBox());
        Workspace(window).State = lifecycle.RegisterDockContent(St(window), docs);
        Workspace(window).Refresh(); // мутация реестра невидима системе свойств
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, docs.Created);
        Assert.IsType<TextBox>(TabHost(window, "x1").Child); // проснулась штатно лениво
    }

    [AvaloniaFact]
    public void DA_5_4_move_to_a_panel_and_back_keeps_the_view_and_content()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        var panelTabs = new CountingTabFactory("p:", _ => new TextBox());
        registry.Register(new ToolWindowDescriptor(
            "p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = panelTabs,
        });
        var lifecycle = new ContentLifecycle(registry);
        var state = DockState(Group("p:t1", "d1", "p:t1"), "p:t1") with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        var view = Assert.IsType<TextBox>(TabHost(window, "p:t1").Child);

        // В дерево панели-владельца: с 4.1 переносу сопутствует активация (панель
        // открывается по DA-E39) — вкладка остаётся материализованной уже в панели…
        Invoke(Item(((MenuFlyout)TabHeader(window, "p:t1").ContextFlyout!).Items, "Move to Panel"));
        Assert.Equal(["d1"], RootGroup(window).Tabs);
        Assert.Equal(0, panelTabs.Released); // перенос — не закрытие (DA-5.4)

        // …и возвращается прямым присвоением: вид и контент те же.
        Workspace(window).State = St(window).MoveTab("p:t1", DockGroupRef.AtTab("d1"), 1, registry);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(view, TabHost(window, "p:t1").Child); // вид удержан кэшем хоста (DA-9.6)
        Assert.Equal(1, panelTabs.Created);
    }

    [AvaloniaFact]
    public void DA_9_6_title_provider_swap_reprojects_without_detaching()
    {
        var (registry, lifecycle, _) = DockSetup(_ => new TextBox());
        var state = DockState(Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        var detach = TrackDetach(TabHost(window, "d1"));

        Workspace(window).TabTitleProvider = id => $"T-{id}";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, detach());
        Assert.Equal(
            "T-d1",
            TabHeader(window, "d1").GetVisualDescendants().OfType<TextBlock>().First().Text);
    }
}
