using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Input wiring to core commands (spec TW-5.4, TW-5.3, TW-5.16, TW-8.2/8.3, TW-5.15, TW-5.9,
/// TW-2.7 R2; ADR-0004): clicks, menus and splitter drags mutate the workspace State. Menu
/// items are invoked by raising their Click event — the wiring under test is «item → core
/// command»; the flyout opening itself is smoke-tested where the headless platform allows.
/// </summary>
public class InputTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Get(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static MenuItem Item(ItemCollection items, string header) =>
        items.OfType<MenuItem>().First(i => string.Equals(i.Header as string, header, StringComparison.Ordinal));

    private static void Invoke(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ---- TW-5.4 / TW-5.3: clicks ----

    [AvaloniaFact]
    public void TW_5_4_click_toggles_openness()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry);

        Click(window, Button(window, "a"));
        Assert.True(Get(window, "a").IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.Single(Decorators(window));

        Click(window, Button(window, "a")); // повторный клик по открытой активной — закрывает
        Assert.False(Get(window, "a").IsOpen);
        Assert.Null(St(window).ActiveToolWindowId);
        Assert.Empty(Decorators(window));
    }

    [AvaloniaFact]
    public void TW_5_4_click_closes_an_open_inactive_window()
    {
        // «Независимо от активности»: открытая, но не активная панель закрывается, не активируется.
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);

        Click(window, Button(window, "a"));

        Assert.False(Get(window, "a").IsOpen);
        Assert.Null(St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_5_3_hide_button_closes_the_window()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
            ActiveToolWindowId = "a",
        };
        var window = Show(state, registry);

        Click(window, Part(window, "PART_HideButton"));

        var a = Get(window, "a");
        Assert.False(a.IsOpen);
        Assert.True(a.IsIconVisible); // Hide панели относится к панели: иконка остаётся (TW-5.16)
        Assert.Null(St(window).ActiveToolWindowId);
    }

    // ---- TW-5.16: the compact icon menu ----

    [AvaloniaFact]
    public void TW_5_16_icon_menu_hide_sends_the_window_to_quick_access()
    {
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = Show(state, registry);

        var flyout = (MenuFlyout)Button(window, "a").ContextFlyout!;
        Invoke(Item(flyout.Items, "Hide"));

        var a = Get(window, "a");
        Assert.False(a.IsIconVisible); // Hide иконки относится к иконке (TW-5.10)
        Assert.False(a.IsOpen);
        Assert.Equal(["b"], Buttons(window).Select(b => b.ToolWindowId));
        Assert.NotNull(TryPart(window, "PART_QuickAccess")); // панель уехала в «⋯» (TW-8.2)
    }

    [AvaloniaFact]
    public void TW_5_16_icon_menu_moves_to_the_end_of_the_target_slot()
    {
        var registry = Registry("a", "b", "c");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary),
                Win("b", ToolWindowSide.Right, ToolWindowGroup.Primary, order: 0),
                Win("c", ToolWindowSide.Right, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = Show(state, registry);

        var flyout = (MenuFlyout)Button(window, "a").ContextFlyout!;
        var moveTo = Item(flyout.Items, "Move to");
        var current = Item(moveTo.Items, "Left Top");
        Assert.True(current.IsChecked); // текущий слот отмечен и недоступен (TW-5.16)
        Assert.False(current.IsEnabled);

        Invoke(Item(moveTo.Items, "Right Top"));

        var a = Get(window, "a");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), a.Slot);
        Assert.Equal(2, a.Order); // в конец слота-приёмника (TW-5.7)
        Assert.Equal(0, Get(window, "b").Order);
        Assert.Equal(1, Get(window, "c").Order);
    }

    [AvaloniaFact]
    public void TW_5_16_right_click_opens_the_icon_menu()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry);
        var button = Button(window, "a");

        RightClick(window, button);

        Assert.True(button.ContextFlyout!.IsOpen);
    }

    // ---- TW-5.16: the full menu ----

    [AvaloniaFact]
    public void TW_5_16_view_mode_switches_the_internal_mode()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);

        var menuButton = (Button)Part(window, "PART_MenuButton");
        var flyout = (MenuFlyout)menuButton.Flyout!;
        var viewMode = Item(flyout.Items, "View Mode");

        // Under a Window TopLevel the platform hosts real windows: all five modes (TW-5.16, TW-7.6).
        Assert.Equal(5, viewMode.Items.Count);
        var pinned = Item(viewMode.Items, "Dock Pinned");
        Assert.True(pinned.IsChecked);
        Assert.False(pinned.IsEnabled);

        Invoke(Item(viewMode.Items, "Undock"));

        var a = Get(window, "a");
        Assert.Equal(ToolWindowMode.Undock, a.Mode);
        Assert.Equal(ToolWindowMode.Undock, a.LastInternalMode);
        Assert.True(a.IsOpen); // SetMode открытость не меняет (TW-5.6)
    }

    [AvaloniaFact]
    public void TW_5_16_header_context_menu_is_the_full_menu()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);

        var flyout = (MenuFlyout)Part(window, "PART_Header").ContextFlyout!;

        Assert.NotNull(Item(flyout.Items, "View Mode"));
        Assert.NotNull(Item(flyout.Items, "Move to"));
    }

    [AvaloniaFact]
    public void TW_5_16_full_menu_opens_from_both_hosts()
    {
        // Один MenuFlyout обслуживает и кнопку «⋮», и контекст заголовка: ShowAt
        // переанкоривает попап на каждый показ — контракт закреплён смоук-тестом.
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);
        var menuButton = (Button)Part(window, "PART_MenuButton");

        Click(window, menuButton);
        // Первый клик по хрому неактивной панели её активирует (TW-6.6), и Sync пересобирает
        // меню до показа — флайаут захватывается после: открывается текущий экземпляр.
        var flyout = (MenuFlyout)menuButton.Flyout!;
        Assert.True(flyout.IsOpen, "open via the menu button");
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        RightClick(window, Part(window, "PART_Header"));
        Assert.True(flyout.IsOpen, "open via the header context after the button");
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Click(window, menuButton);
        Assert.True(flyout.IsOpen, "open via the menu button again after the header");
    }

    [AvaloniaFact]
    public void TW_5_16_floating_record_offers_the_dock_return() // E27
    {
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Float,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                    FloatingBounds = new FloatingBounds(10, 10, 300, 200),
                },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1) with { IsOpen = true },
            ],
        };
        var window = Show(state, registry);

        var flyout = (MenuFlyout)Button(window, "a").ContextFlyout!;
        Invoke(Item(flyout.Items, "Dock"));

        var a = Get(window, "a");
        Assert.Equal(ToolWindowMode.DockUnpinned, a.Mode); // возврат в LastInternalMode (TW-5.6, E27)
        Assert.True(a.IsOpen);
        Assert.False(Get(window, "b").IsOpen); // вытеснение слоя-приёмника (E6)
    }

    // ---- TW-8.2 / TW-8.3 / TW-5.15: quick access ----

    [AvaloniaFact]
    public void TW_8_3_quick_access_selection_returns_the_icon_and_opens()
    {
        var registry = Registry("a:Alpha", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0) with { IsIconVisible = false },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = Show(state, registry);

        var quick = Part(window, "PART_QuickAccess");
        var flyout = (MenuFlyout)FlyoutBase.GetAttachedFlyout(quick)!;
        Invoke(Item(flyout.Items, "Alpha"));

        var a = Get(window, "a");
        Assert.True(a.IsIconVisible);
        Assert.True(a.IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.Null(TryPart(window, "PART_QuickAccess")); // список опустел — кнопка исчезла (TW-8.4)
    }

    [AvaloniaFact]
    public void TW_8_2_click_opens_the_quick_access_list()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsIconVisible = false }],
        };
        var window = Show(state, registry);
        var quick = Part(window, "PART_QuickAccess");

        Click(window, quick);

        Assert.True(FlyoutBase.GetAttachedFlyout(quick)!.IsOpen);
    }

    [AvaloniaFact]
    public void TW_8_2_quick_access_list_is_sorted_by_title()
    {
        // Порядок регистрации (beta раньше) — дискриминатор: без сортировки beta шла бы первой.
        var registry = Registry("z:beta", "a:Alpha");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("z", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0) with { IsIconVisible = false },
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1) with { IsIconVisible = false },
            ],
        };
        var window = Show(state, registry);

        var flyout = (MenuFlyout)FlyoutBase.GetAttachedFlyout(Part(window, "PART_QuickAccess"))!;

        Assert.Equal(
            ["Alpha", "beta"],
            flyout.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
    }

    [AvaloniaFact]
    public void TW_5_15_quick_access_context_menu_moves_the_button()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsIconVisible = false }],
        };
        var window = Show(state, registry);

        var context = (MenuFlyout)Part(window, "PART_QuickAccess").ContextFlyout!;
        var left = Item(context.Items, "Move to Left");
        Assert.True(left.IsChecked); // текущая сторона отмечена и недоступна (TW-5.16)
        Assert.False(left.IsEnabled);

        Invoke(Item(context.Items, "Move to Right"));

        Assert.Equal(QuickAccessSide.Right, St(window).QuickAccessSide);
        Assert.NotNull(TryPart(Part(window, "PART_RightStripe"), "PART_QuickAccess"));
        Assert.Null(TryPart(Part(window, "PART_LeftStripe"), "PART_QuickAccess"));
    }

    // ---- TW-5.9 / TW-2.7 R2: splitter drags ----

    [AvaloniaFact]
    public void TW_5_9_side_splitter_commits_one_command_on_release()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);
        var workspace = Workspace(window);
        var initial = workspace.State!;
        var paneWidth = Part(window, "PART_LeftPane").Bounds.Width;
        var total = paneWidth + Part(window, "PART_DockArea").Bounds.Width;

        var start = Center(Part(window, "PART_LeftSideSplitter"), window);
        var end = new Point(start.X + 100, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        Dispatcher.UIThread.RunJobs();

        // Жест до отпускания — чистая визуализация (ADR-0004): панель растёт, состояние не тронуто.
        Assert.Same(initial, workspace.State);
        Assert.True(Part(window, "PART_LeftPane").Bounds.Width > paneWidth + 50);

        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((paneWidth + 100) / total, workspace.State!.Left.Weight, 0.02);
        Assert.Equal(LayoutDefaults.SideWeight, workspace.State!.Right.Weight); // чужая сторона не тронута
    }

    [AvaloniaFact]
    public void TW_5_9_side_splitter_with_both_sides_open_commits_only_its_side()
    {
        // Самый нетривиальный геометрический путь: три звёздных колонки, GridSplitter
        // ресайзит только соседнюю пару — правая панель не движется ни в рендере, ни в весе.
        var registry = Registry("a", "c");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("c", ToolWindowSide.Right, ToolWindowGroup.Primary) with { IsOpen = true },
            ],
        };
        var window = Show(state, registry);
        var leftWidth = Part(window, "PART_LeftPane").Bounds.Width;
        var rightWidth = Part(window, "PART_RightPane").Bounds.Width;
        var total = leftWidth + Part(window, "PART_DockArea").Bounds.Width + rightWidth;

        var start = Center(Part(window, "PART_LeftSideSplitter"), window);
        var end = new Point(start.X + 100, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((leftWidth + 100) / total, St(window).Left.Weight, 0.02);
        Assert.Equal(LayoutDefaults.SideWeight, St(window).Right.Weight); // чужой вес не тронут
        Assert.Equal(rightWidth, Part(window, "PART_RightPane").Bounds.Width, 1.5); // и рендер тоже
    }

    [AvaloniaFact]
    public void TW_5_9_click_on_a_splitter_without_movement_commits_nothing()
    {
        // Thumb шлёт DragCompleted и при нулевом перемещении: без гейта клик защёлкивал бы
        // клемпнутый рендер в состояние и дрейфовал вес на пиксельном округлении (ADR-0004).
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Secondary) with { IsOpen = true },
            ],
        };
        var window = Show(state, registry);
        var workspace = Workspace(window);
        var initial = workspace.State!;

        Click(window, Part(window, "PART_LeftSideSplitter")); // путь WorkspaceGrid
        Click(window, Part(window, "PART_PairSplitter")); // путь SidePane

        Assert.Same(initial, workspace.State);
    }

    [AvaloniaFact]
    public void TW_2_7_R2_pair_splitter_commits_the_ratio_teaching_both()
    {
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Secondary) with { IsOpen = true },
            ],
        };
        var window = Show(state, registry);
        var pane = Part(window, "PART_LeftPane");
        var primaryHeight = BoundsIn(Decorator(pane, "a"), window).Height;
        var total = primaryHeight + BoundsIn(Decorator(pane, "b"), window).Height;

        var start = Center(Part(window, "PART_PairSplitter"), window);
        var end = new Point(start.X, start.Y + 60);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var result = St(window);
        var dragged = (primaryHeight + 60) / total;
        Assert.Equal(dragged, Get(window, "a").PairRatio, 0.02); // R2 «учит обе»
        Assert.Equal(1 - Get(window, "a").PairRatio, Get(window, "b").PairRatio);
        // Выводимая доля R1 воспроизводит позицию драга точно (TW-2.7).
        Assert.Equal(Get(window, "a").PairRatio, result.GetPairRatio(ToolWindowSide.Left));
    }

    [AvaloniaFact]
    public void TW_5_9_bottom_splitter_commits_the_bottom_weight()
    {
        var registry = Registry("b");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);
        var bottomHeight = Part(window, "PART_BottomPane").Bounds.Height;
        var total = bottomHeight + Part(window, "PART_DockArea").Bounds.Height;

        var start = Center(Part(window, "PART_BottomSplitter"), window);
        var end = new Point(start.X, start.Y - 50); // вверх: нижняя панель растёт
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((bottomHeight + 50) / total, St(window).Bottom.Weight, 0.02);
    }
}
