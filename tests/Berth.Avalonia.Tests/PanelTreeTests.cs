using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The tab strip and splits inside tool windows (spec TW-9.5, DA-8.4, DA-9.6 — task 4.1):
/// the root group's strip lives in the decorator header and hides exactly for the solitary
/// body; the tree materializes by the shared projection with the workspace-wide host cache,
/// so moves between the panel and the dock area reattach the same host with its built view;
/// tab gestures reduce to core commands with activation and focus following (DA-5.3, DA-5.4,
/// DA-6.4), splitter drags commit SetSplitShares addressed by the panel id (DA-5.6), and the
/// tab menu carries the tail of the full window menu (TW-5.16). Unregistration puts the tabs
/// to sleep and drops their views together with the released content (TW-9.4, DA-9.6).
/// </summary>
public class PanelTreeTests
{
    private sealed class BodyFactory(Func<string, object> create) : IToolWindowContentFactory
    {
        public int Created { get; private set; }

        public int Released { get; private set; }

        public object CreateContent(string toolWindowId)
        {
            Created++;
            return create(toolWindowId);
        }

        public void ReleaseContent(string toolWindowId, object content) => Released++;
    }

    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Panel(Window window, string id = "p") =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

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

    private static MenuFlyout TabMenu(Window window, string tabId) =>
        (MenuFlyout)TabHeader(window, tabId).ContextFlyout!;

    private static Panel HeaderTabs(Window window, string panelId = "p") =>
        (Panel)Part(Decorator(window, panelId), "PART_HeaderTabs");

    /// <summary>
    /// Registry + coordinator with panel «p» claiming «p:»-prefixed tabs (TW-9.11); the body
    /// factory is optional — with one, registration seeds the body tab (TW-9.5).
    /// </summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State, CountingTabFactory Tabs)
        PanelSetup(
            Func<string, object>? createTab = null,
            BodyFactory? body = null,
            ContentRetentionPolicy retention = ContentRetentionPolicy.KeepWhileRegistered)
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var tabs = new CountingTabFactory("p:", createTab);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = body,
            TabFactory = tabs,
            RetentionPolicy = retention,
        });
        return (registry, lifecycle, state, tabs);
    }

    private static LayoutState WithPanelTree(LayoutState state, TabTreeNode tree, bool open = true) => state with
    {
        ToolWindows =
        [
            .. state.ToolWindows.Select(w => string.Equals(w.Id, "p", StringComparison.Ordinal)
                ? w with { IsOpen = open, ContentTree = tree }
                : w),
        ],
    };

    // ---- the strip: DA-8.4, TW-9.5 ----

    [AvaloniaFact]
    public void TW_9_5_solitary_body_hides_the_strip_and_the_panel_looks_classic()
    {
        var (registry, lifecycle, state, _) = PanelSetup(body: new BodyFactory(_ => new TextBox()));

        var window = Show(state.Open("p"), registry, lifecycle: lifecycle);

        Assert.Empty(HeaderTabs(window).Children); // полоса скрыта ровно для одиночного тела (DA-8.4)
        Assert.False(Part(Decorator(window, "p"), "PART_TabStrip").IsVisible);
        Assert.IsType<TextBox>(TabHost(window, "p").Child); // тело материализовано мостом (TW-9.5)
    }

    [AvaloniaFact]
    public void DA_8_4_solitary_own_tab_shows_in_the_header_strip()
    {
        var (registry, lifecycle, state, _) = PanelSetup(_ => new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry);

        var window = Show(state, registry, lifecycle: lifecycle);

        // Одиночная вкладка с собственным id отображается — иначе у неё нет UI-ручек (DA-8.4).
        var header = Assert.Single(HeaderTabs(window).Children);
        Assert.Equal("p:t1", ((Control)header).Tag);
    }

    [AvaloniaFact]
    public void TW_9_5_root_group_strip_lives_in_the_header_and_split_moves_it_to_the_groups()
    {
        var (registry, lifecycle, state, _) = PanelSetup(
            _ => new TextBox(), body: new BodyFactory(_ => new TextBox()));
        state = state.Open("p").OpenPanelTab("p:t1", registry);
        var window = Show(state, registry, lifecycle: lifecycle);

        // Корень-группа: полоса в ряду заголовка (= эталон), собственная полоса группы скрыта.
        Assert.Equal(2, HeaderTabs(window).Children.Count);
        Assert.False(Part(Decorator(window, "p"), "PART_TabStrip").IsVisible);

        Workspace(window).State = St(window).SplitTab("p:t1", SplitDirection.Right);
        Dispatcher.UIThread.RunJobs();

        // Корень-сплит: заголовок без вкладок, полосы — по группам.
        Assert.Empty(HeaderTabs(window).Children);
        var strips = Decorator(window, "p").GetVisualDescendants()
            .OfType<Control>()
            .Where(c => string.Equals(c.Name, "PART_TabStrip", StringComparison.Ordinal) && c.IsVisible);
        Assert.Equal(2, strips.Count());
    }

    // ---- activity: DA-5.3, DA-6.4, DA-E19 ----

    [AvaloniaFact]
    public void DA_5_3_click_on_a_panel_tab_activates_the_panel_and_leaves_the_dock_alone()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry).OpenPanelTab("p:t2", registry);
        state = state with
        {
            DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        Workspace(window).State = St(window).ActivateTab("p:t2"); // прямое присвоение фокус не двигает
        Dispatcher.UIThread.RunJobs();

        Click(window, TabHeader(window, "p:t1"));

        var tree = Assert.IsType<TabGroupNode>(Panel(window).ContentTree);
        Assert.Equal("p:t1", tree.ActiveTabId); // активная вкладка группы (DA-5.3)
        Assert.Equal("p", St(window).ActiveToolWindowId); // панель активирована
        Assert.Equal("d1", St(window).DockArea.CurrentTabId); // док-хосты не тронуты (DA-E19)
        Assert.True(boxes["p:t1"].IsFocused); // жест перенёс фокус в контент (DA-6.4)
    }

    [AvaloniaFact]
    public void TW_6_1_click_on_the_own_strip_does_not_close_the_unpinned_panel()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry).OpenPanelTab("p:t2", registry);
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                }),
            ],
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxes["p:t2"]); // активная вкладка p:t2 — фокус внутри панели

        Click(window, TabHeader(window, "p:t1")); // клик по своей полосе — жест, не автоскрытие

        Assert.True(Panel(window).IsOpen); // TW-6.1/TW-6.2: панель не закрылась
        Assert.True(boxes["p:t1"].IsFocused);
    }

    [AvaloniaFact]
    public void DA_5_2_closing_the_focused_panel_tab_keeps_focus_inside_the_panel()
    {
        // Regression (review of 4.1): the dangling-focus fallback jumped to the dock area's
        // current tab, deactivating the still-active panel — and auto-hiding an unpinned one:
        // closing a single tab silently closed the whole panel. Focus must follow the group's
        // new active tab of the same panel (DA-5.2, the panel-side mirror of DA-9.6).
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry).OpenPanelTab("p:t2", registry);
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                }),
            ],
            DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
        };
        // Широкое окно: полоса заголовка целиком внутри панели — клик по «×» реалистичен
        // (переполнение полосы — отдельный необработанный случай, раздел 11 document-area).
        var window = Show(state, registry, width: 1600, lifecycle: lifecycle);
        Focus(boxes["p:t2"]);

        Click(window, Part(TabHeader(window, "p:t2"), "PART_TabClose"));

        Assert.True(Panel(window).IsOpen); // фокус не покинул панель — автоскрытие не сработало
        Assert.Equal("p", St(window).ActiveToolWindowId);
        var tree = Assert.IsType<TabGroupNode>(Panel(window).ContentTree);
        Assert.Equal("p:t1", tree.ActiveTabId);
        // Нажатие само перевело фокус на ближайший фокусируемый предок «×» — декоратор:
        // фокус внутри панели не переставляется (TW-6.6), достаточно, что он не ушёл.
        Assert.True(Decorator(window, "p").IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void DA_5_2_menu_close_of_the_focused_panel_tab_focuses_the_successor()
    {
        // Дополнение к клику по «×»: закрытие через меню оставляет фокус повисшим (контент
        // закрытой вкладки отцеплен), и командный канал обязан вернуть его владельцу —
        // в новую активную вкладку группы (DA-5.2), а не в док-зону: скачок наружу
        // деактивировал бы панель и захлопнул unpinned автоскрытием (TW-6.1).
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry).OpenPanelTab("p:t2", registry);
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                }),
            ],
            DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxes["p:t2"]);

        Invoke(Item(TabMenu(window, "p:t2").Items, "Close"));

        Assert.True(Panel(window).IsOpen);
        Assert.Equal("p", St(window).ActiveToolWindowId);
        Assert.True(boxes["p:t1"].IsFocused); // фокус — в новую активную вкладку той же панели
    }

    // ---- gestures: DA-5.5, DA-5.6, DA-8.2, DA-E39, DA-9.6 ----

    [AvaloniaFact]
    public void DA_5_5_panel_split_menu_splits_in_place_and_keeps_the_host()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, tabs) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry).OpenPanelTab("p:t2", registry);
        var window = Show(state, registry, lifecycle: lifecycle);
        var host = TabHost(window, "p:t2");
        Focus(boxes["p:t2"]);

        Invoke(Item(TabMenu(window, "p:t2").Items, "Split and Move Right"));

        var root = Assert.IsType<SplitNode>(Panel(window).ContentTree);
        Assert.Equal(SplitOrientation.Row, root.Orientation);
        Assert.Same(host, TabHost(window, "p:t2")); // тот же хост после перестройки (DA-9.6)
        Assert.True(boxes["p:t2"].IsFocused); // командный канал вернул фокус (зеркало TW-6.6)
        Assert.Equal(2, tabs.Created); // перенос — не пересоздание (DA-5.4)
    }

    [AvaloniaFact]
    public void DA_5_6_panel_splitter_commits_shares_addressed_by_the_panel_id()
    {
        var (registry, lifecycle, state, _) = PanelSetup(_ => new TextBox());
        state = WithPanelTree(
            state.Open("p"),
            Row(Child(Group("p:t1", "p:t1"), 0.5), Child(Group("p:t2", "p:t2"), 0.5)));
        var window = Show(state, registry, lifecycle: lifecycle, width: 1200);
        var initial = Workspace(window).State;

        var splitter = Part(Decorator(window, "p"), "PART_DockSplitter");
        var start = Center(splitter, window);
        var end = new Point(start.X + 40, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(initial, Workspace(window).State); // до отпускания состояние не тронуто

        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var root = Assert.IsType<SplitNode>(Panel(window).ContentTree);
        Assert.True(root.Children[0].Share > 0.5); // одна команда SetSplitShares по id панели (DA-5.6)
        Assert.Equal(1.0, root.Children[0].Share + root.Children[1].Share, precision: 9);
        Assert.Empty(LayoutInvariants.Validate(St(window), registry));
    }

    [AvaloniaFact]
    public void DA_8_2_move_to_document_area_moves_activates_and_focuses()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, tabs) = PanelSetup(id => boxes[id] = new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry);
        state = state with
        {
            DockArea = new DockAreaState { Root = Group("d1", "d1"), CurrentTabId = "d1" },
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        var view = TabHost(window, "p:t1").Child;

        Invoke(Item(TabMenu(window, "p:t1").Items, "Move to Document Area"));

        var root = Assert.IsType<TabGroupNode>(St(window).DockArea.Root);
        Assert.Equal(["d1", "p:t1"], root.Tabs); // в конец текущей группы главного окна
        Assert.Equal("p:t1", St(window).DockArea.CurrentTabId); // активация вслед (DA-5.4)
        Assert.Null(St(window).ActiveToolWindowId);
        Assert.Same(view, TabHost(window, "p:t1").Child); // тот же хост и вид (DA-9.6)
        Assert.True(boxes["p:t1"].IsFocused); // и фокус за вкладкой (DA-6.4)
        Assert.Equal(0, tabs.Released); // перенос — не закрытие (DA-5.4)
    }

    [AvaloniaFact]
    public void DA_E39_move_to_the_closed_owner_opens_it_and_focuses_the_tab()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        registry.RegisterDockContent(new CountingTabFactory("d", _ => new TextBox()));
        state = state with
        {
            DockArea = new DockAreaState { Root = Group("p:t1", "d1", "p:t1"), CurrentTabId = "p:t1" },
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        var view = TabHost(window, "p:t1").Child;

        Invoke(Item(TabMenu(window, "p:t1").Items, "Move to Panel"));

        Assert.True(Panel(window).IsOpen); // активация открыла закрытого владельца (DA-E39)
        Assert.Equal("p", St(window).ActiveToolWindowId);
        Assert.Equal(["d1"], Assert.IsType<TabGroupNode>(St(window).DockArea.Root).Tabs);
        Assert.Same(view, TabHost(window, "p:t1").Child); // общий кэш: хост переехал с видом (DA-9.6)
        Assert.True(boxes["p:t1"].IsFocused);
    }

    // ---- the menu tail: TW-5.16 ----

    [AvaloniaFact]
    public void TW_5_16_panel_tab_menu_carries_the_window_tail()
    {
        var (registry, lifecycle, state, _) = PanelSetup(_ => new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry);
        var window = Show(state, registry, lifecycle: lifecycle);

        var headers = TabMenu(window, "p:t1").Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
        Assert.Equal(
            [
                "Close",
                "Split and Move Right", "Split and Move Down", "Split and Move Left", "Split and Move Up",
                "Move to Document Area", "Move to New Window",
                "View Mode", "Move to", "Remove from Sidebar", "Hide",
            ],
            headers); // вкладочная секция + хвост полного меню; Rotate нет у корневой группы (DA-5.9)

        Invoke(Item(TabMenu(window, "p:t1").Items, "Remove from Sidebar"));

        Assert.False(Panel(window).IsIconVisible); // TW-5.10 из хвоста меню
        Assert.False(Panel(window).IsOpen);
    }

    [AvaloniaFact]
    public void TW_5_16_full_menu_hide_closes_and_remove_hides_the_icon()
    {
        var (registry, lifecycle, state, _) = PanelSetup(body: new BodyFactory(_ => new TextBox()));
        var window = Show(state.Open("p"), registry, lifecycle: lifecycle);
        var decorator = Decorator(window, "p"); // после Hide хост уходит в кэш — держим ссылку

        var menuButton = (Button)Part(decorator, "PART_MenuButton");
        Invoke(Item(((MenuFlyout)menuButton.Flyout!).Items, "Hide"));
        Assert.False(Panel(window).IsOpen); // «Hide» = Close (TW-5.3)
        Assert.True(Panel(window).IsIconVisible);

        // Меню пересобрано обновлением кэшированного хоста; команда не требует показа панели.
        Invoke(Item(((MenuFlyout)menuButton.Flyout!).Items, "Remove from Sidebar"));
        Assert.False(Panel(window).IsIconVisible); // «Remove from Sidebar» = SetIconVisible(false) (TW-5.10)
    }

    // ---- lifecycle: TW-9.4, DA-9.4, DA-9.6 ----

    [AvaloniaFact]
    public void TW_9_4_unregister_sleeps_the_tabs_and_drops_their_views()
    {
        var (registry, lifecycle, state, tabs) = PanelSetup(_ => new TextBox());
        state = state.Open("p").OpenPanelTab("p:t1", registry);
        var window = Show(state, registry, lifecycle: lifecycle);
        var host = TabHost(window, "p:t1");
        Assert.IsType<TextBox>(host.Child);

        Workspace(window).State = lifecycle.Unregister(St(window), "p");
        Workspace(window).Refresh(); // мутация реестра невидима системе свойств
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, tabs.Released); // контент вкладок собственного дерева освобождён (TW-9.4)
        Assert.IsType<TextBlock>(host.Child); // вид уронен вместе с контентом (DA-9.6) — заглушка
        Assert.Contains("p:t1", Assert.IsType<TabGroupNode>(Panel(window).ContentTree).Tabs); // вкладка спит (TW-10.2)

        // Повторная регистрация подхватывает спящую запись; открытие материализует заново.
        var reopened = lifecycle.Register(St(window), new ToolWindowDescriptor(
            "p", "Panel", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            TabFactory = new CountingTabFactory("p:", _ => new TextBox()),
        });
        Workspace(window).State = reopened.Open("p");
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<TextBox>(TabHost(window, "p:t1").Child); // проснулась штатно лениво (DA-9.4)
    }

    [AvaloniaFact]
    public void DA_9_6_unrelated_commands_do_not_detach_panel_tab_hosts()
    {
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var (registry, lifecycle, state, _) = PanelSetup(id => boxes[id] = new TextBox());
        registry.Register(new ToolWindowDescriptor(
            "q", "Q", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary)));
        state = state.Open("p").OpenPanelTab("p:t1", registry);
        state = state with
        {
            ToolWindows = state.ToolWindows.Add(Win("q", ToolWindowSide.Right, ToolWindowGroup.Primary)),
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        var detach = TrackDetach(TabHost(window, "p:t1"));
        Focus(boxes["p:t1"]);

        Workspace(window).State = St(window).SetSideSize(ToolWindowSide.Left, 0.4);
        Dispatcher.UIThread.RunJobs();
        Workspace(window).State = St(window).Open("q", activate: false); // соседняя сторона появилась
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, detach()); // хосты панельных вкладок не переприсоединяются (DA-9.6)
        Assert.True(boxes["p:t1"].IsFocused);
    }
}
