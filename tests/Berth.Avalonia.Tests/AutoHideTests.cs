using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Auto-hide and activation wiring (spec TW-6.1, TW-6.2, TW-6.4, TW-6.6): the focus loser
/// closes, clicks outside close on release, activation transfers keyboard focus and focus
/// gains activate. Popup exceptions are structural on the desktop popup model — focus events
/// inside popup roots never reach the workspace's TopLevel — and are additionally guarded by
/// the logical containment predicate for overlay-hosted popups; the browser overlay model is
/// re-verified in phase 6. Application deactivation is approximated by focusing a second
/// headless window; the modal-dialog exception shares that structure and is checked manually
/// on the desktop demo.
/// </summary>
public class AutoHideTests
{
    private sealed class CountingFactory(Func<string, object> create) : IToolWindowContentFactory
    {
        public int Released { get; private set; }

        public object CreateContent(string toolWindowId) => create(toolWindowId);

        public void ReleaseContent(string toolWindowId, object content) => Released++;
    }

    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Get(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    /// <summary>Registers a window with a factory-built body and returns the reconciled state.</summary>
    private static LayoutState Register(
        ContentLifecycle lifecycle,
        LayoutState state,
        string id,
        ToolWindowSide side,
        ToolWindowGroup group,
        Func<string, object>? create = null,
        ContentRetentionPolicy retention = ContentRetentionPolicy.KeepWhileRegistered) =>
        lifecycle.Register(state, new ToolWindowDescriptor(id, id, new ToolWindowSlot(side, group))
        {
            ContentFactory = create is null ? null : new CountingFactory(create),
            RetentionPolicy = retention,
        });

    private static LayoutState Mutate(LayoutState state, string id, Func<ToolWindowState, ToolWindowState> change) =>
        state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w =>
                    string.Equals(w.Id, id, StringComparison.Ordinal) ? change(w) : w),
            ],
        };

    private static LayoutState OpenAs(LayoutState state, string id, ToolWindowMode mode) =>
        Mutate(state, id, w => w with
        {
            Mode = mode,
            LastInternalMode = mode.IsInternal() ? mode : LayoutDefaults.LastInternalMode,
            IsOpen = true,
        });

    private static void Focus(Control control)
    {
        Assert.True(control.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    // ---- TW-6.1: the focus loser closes ----

    [AvaloniaFact]
    public void TW_6_1_the_focus_loser_closes_and_keeps_its_fields()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var boxB = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "b", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxB);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        state = OpenAs(state, "b", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);

        Focus(boxA);
        Assert.Equal("a", St(window).ActiveToolWindowId); // фокус в контенте = активация (TW-6.6)

        Focus(boxB);

        var a = Get(window, "a");
        Assert.False(a.IsOpen); // фокус ушёл из панели — она закрылась (TW-6.1)
        Assert.Equal(ToolWindowMode.DockUnpinned, a.Mode); // закрытие — Close: поля сохранены (TW-5.3)
        Assert.True(Get(window, "b").IsOpen);
        Assert.Equal("b", St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_6_1_undock_closes_while_the_pinned_neighbour_stays() // E17
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var boxU = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "u", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxU);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        state = OpenAs(state, "u", ToolWindowMode.Undock); // сосуществуют: разные слои (INV-2)
        var window = Show(state, registry, lifecycle: lifecycle);

        Focus(boxU);
        Focus(boxA);

        Assert.False(Get(window, "u").IsOpen);
        Assert.True(Get(window, "a").IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_6_1_a_panel_that_never_had_focus_survives_foreign_focus_moves()
    {
        // Защита восстановленной раскладки: открытая unpinned-панель без фокуса не закрывается
        // чужими перемещениями фокуса — закрывается только фокус-проигравший (TW-6.1).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxB = new TextBox();
        var boxC = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary);
        state = Register(lifecycle, state, "b", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxB);
        state = Register(lifecycle, state, "c", ToolWindowSide.Bottom, ToolWindowGroup.Primary, _ => boxC);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        state = OpenAs(state, "b", ToolWindowMode.DockPinned);
        state = OpenAs(state, "c", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);

        Focus(boxB);
        Assert.True(Get(window, "a").IsOpen);

        Focus(boxC);
        Assert.True(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_1_own_menu_popup_does_not_close_the_panel()
    {
        // Попап, владелец которого внутри панели: меню «⋮» забирает фокус, панель жива.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        Click(window, Part(Decorator(window, "a"), "PART_MenuButton"));

        Assert.True(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_1_combobox_popup_does_not_close_the_panel() // E10
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var combo = new ComboBox { ItemsSource = new[] { "x", "y" } };
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => combo);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(combo);

        Click(window, combo);
        Assert.True(combo.IsDropDownOpen);

        Assert.True(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_1_own_icon_menu_does_not_close_the_panel()
    {
        // Расширенное исключение: меню собственной иконки — ручка самой панели (TW-6.1, TW-5.16).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        RightClick(window, Button(window, "a"));

        Assert.True(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_1_focus_in_another_window_keeps_the_panel_open()
    {
        // Деактивация приложения: фокус ушёл из TopLevel — GotFocus в нём не приходит,
        // автоскрытие не срабатывает. Модальный диалог покрыт той же структурой.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        var foreign = new TextBox();
        var second = new Window { Width = 200, Height = 200, Content = foreign };
        second.Show();
        Focus(foreign);

        Assert.True(Get(window, "a").IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        second.Close();
    }

    // ---- TW-6.2: clicks outside ----

    [AvaloniaFact]
    public void TW_6_2_click_on_the_dock_area_closes_the_unpinned_panel()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        Click(window, Part(window, "PART_DockArea"));

        Assert.False(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_2_panel_opened_during_the_gesture_survives_the_release()
    {
        // Команда жеста может открыть панель между нажатием и отпусканием — перенос вкладки
        // в закрытого владельца (DA-E39); на платформах с оверлей-попапами так выглядит любой
        // клик пункта меню. Отпускание закрывает только панели, открытые на момент нажатия
        // (TW-6.2) — свежеоткрытую тот же клик не «пролистнул».
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary);
        state = Mutate(state, "a", w => w with
        {
            Mode = ToolWindowMode.DockUnpinned,
            LastInternalMode = ToolWindowMode.DockUnpinned,
        });
        var window = Show(state, registry, lifecycle: lifecycle);

        var point = Center(Part(window, "PART_DockArea"), window);
        window.MouseDown(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        // Модель команды, выполнившейся внутри жеста: панель открылась между press и release.
        Workspace(window).State = St(window).Open("a");
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_2_click_on_another_icon_processes_the_toggle_and_closes()
    {
        // Каскад одного жеста: toggle открывает c (команда 1), перенос фокуса активации
        // закрывает фокус-проигравшую a (команда 2) — ровно два перехода, порядок жеста.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var boxC = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "c", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxC);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(window);
        Focus(boxA);

        var changes = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.Property == BerthWorkspace.StateProperty)
            {
                changes++;
            }
        };

        Click(window, Button(window, "c"));

        Assert.True(Get(window, "c").IsOpen);
        Assert.Equal("c", St(window).ActiveToolWindowId);
        Assert.False(Get(window, "a").IsOpen);
        Assert.True(boxC.IsFocused); // активация перенесла фокус (TW-6.6)
        Assert.Equal(2, changes); // каскад терминировался: Open(c) и Close(a), ничего сверх
    }

    [AvaloniaFact]
    public void TW_6_2_click_on_the_panels_own_icon_is_the_toggle()
    {
        // Автоскрытие не перехватывает клик по своей иконке: перехват дал бы «закрыть + открыть».
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        Click(window, Button(window, "a"));

        Assert.False(Get(window, "a").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_2_splitter_drag_does_not_close_the_unpinned_panel()
    {
        // Драг сплиттера — жест ресайза, не клик: иначе ресайз пары закрывал бы её же
        // unpinned-панель на отпускании (TW-6.2; сплиттер нефокусируем — фокус не двигается).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "c", ToolWindowSide.Left, ToolWindowGroup.Secondary);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        state = OpenAs(state, "c", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        var start = Center(Part(window, "PART_PairSplitter"), window);
        var end = start.WithY(start.Y + 60);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Get(window, "a").IsOpen);
        Assert.True(boxA.IsFocused); // фокус жест не трогал
        Assert.NotEqual(LayoutDefaults.PairRatio, Get(window, "a").PairRatio); // ресайз состоялся (R2)
    }

    // ---- TW-6.6: activation ↔ focus ----

    [AvaloniaFact]
    public void TW_6_6_activation_by_click_moves_focus_into_the_content()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        var window = Show(state, registry, lifecycle: lifecycle);

        Click(window, Button(window, "a"));

        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(boxA.IsFocused);
    }

    [AvaloniaFact]
    public void TW_6_6_activation_falls_back_to_the_host_without_focusable_content()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry);

        Click(window, Button(window, "a"));

        Assert.True(Decorator(window, "a").IsFocused); // фолбэк — хост панели (TW-6.6)
    }

    [AvaloniaFact]
    public void TW_6_6_focus_gained_inside_activates_without_rearranging_focus()
    {
        // Гвард «фокус уже внутри»: активация по фокусу не перетаскивает фокус на первый
        // фокусируемый элемент вида.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var first = new TextBox();
        var second = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => panel);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Assert.Null(St(window).ActiveToolWindowId);

        Focus(second);

        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(second.IsFocused);
    }

    [AvaloniaFact]
    public void TW_6_6_direct_state_assignment_does_not_move_focus()
    {
        // Фокус переносит только командный канал: присвоение State приложением — нет (TW-6.6).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var boxB = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "b", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxB);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        state = OpenAs(state, "b", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(window);
        Focus(boxB);

        workspace.State = workspace.State!.Open("a"); // активация из кода, без фокуса (TW-9.3)
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(boxB.IsFocused);
    }

    [AvaloniaFact]
    public void TW_6_1_the_second_undock_closes_the_first_by_autohide()
    {
        // Краевой случай бэклога: открытие второго Undock — любой стороны — закрывает первый
        // тем же автохайдом; два видимых оверлея живым потоком недостижимы (комментарий в
        // UndockOverlay о z-порядке мимолётного состояния).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxU1 = new TextBox();
        var boxU2 = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "u1", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxU1);
        state = Register(lifecycle, state, "u2", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxU2);
        state = OpenAs(state, "u1", ToolWindowMode.Undock);
        state = Mutate(state, "u2", w => w with { Mode = ToolWindowMode.Undock, LastInternalMode = ToolWindowMode.Undock });
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxU1);

        Click(window, Button(window, "u2"));

        Assert.False(Get(window, "u1").IsOpen);
        Assert.True(Get(window, "u2").IsOpen);
        Assert.True(boxU2.IsFocused);
    }

    // ---- TW-6.4: the active window is marked ----

    [AvaloniaFact]
    public void TW_6_4_the_active_window_carries_the_active_pseudoclass()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var boxB = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = Register(lifecycle, state, "b", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxB);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        state = OpenAs(state, "b", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);

        Focus(boxA);
        Assert.Contains(":active", Decorator(window, "a").Classes);
        Assert.DoesNotContain(":active", Decorator(window, "b").Classes);

        Focus(boxB);
        Assert.DoesNotContain(":active", Decorator(window, "a").Classes);
        Assert.Contains(":active", Decorator(window, "b").Classes);
    }

    // ---- TW-9.2: autohide close is a regular close for the lifecycle ----

    [AvaloniaFact]
    public void TW_9_2_autohide_close_releases_disposeonclose_content_exactly_once()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var factory = new CountingFactory(_ => new TextBox());
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "a", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = factory,
            RetentionPolicy = ContentRetentionPolicy.DisposeOnClose,
        });
        var boxB = new TextBox();
        state = Register(lifecycle, state, "b", ToolWindowSide.Right, ToolWindowGroup.Primary, _ => boxB);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        state = OpenAs(state, "b", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        var boxA = (TextBox)TabHost(Decorator(window, "a"), "a").Child!;
        Focus(boxA);

        Focus(boxB);

        Assert.False(Get(window, "a").IsOpen);
        Assert.Equal(1, factory.Released); // один жест — одно освобождение (TW-9.2, ADR-0004)
    }
}
