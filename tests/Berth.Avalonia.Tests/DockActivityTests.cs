using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Activity and focus wiring between the dock area and the tool windows (spec DA-6.4, TW-6.3,
/// TW-6.5, DA-E21): focus gains in tab content reduce to ActivateTab, Esc inside a panel
/// moves focus into the current tab of the effective active host — the main window until
/// document windows materialize — and closing the focused panel returns focus to the
/// document. Direct state assignments never move focus (DA-6.4).
/// </summary>
public class DockActivityTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Panel(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static SplitChild Child(TabTreeNode node, double share) => new(node, share);

    private static SplitNode Row(params SplitChild[] children) =>
        new() { Orientation = SplitOrientation.Row, Children = [.. children] };

    private static void Focus(Control control)
    {
        Assert.True(control.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class PanelFactory(Func<string, object> create) : IToolWindowContentFactory
    {
        public object CreateContent(string toolWindowId) => create(toolWindowId);

        public void ReleaseContent(string toolWindowId, object content)
        {
        }
    }

    /// <summary>
    /// One panel «p» with a TextBox body plus dock content claimed by the "d" prefix; the
    /// dock area is set afterwards, so the layout starts from the reconciled panel state.
    /// </summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State,
        Dictionary<string, TextBox> Boxes, TextBox PanelBox) Setup(ToolWindowMode mode)
    {
        var registry = new ToolWindowRegistry();
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        registry.RegisterDockContent(new CountingTabFactory("d", id => boxes[id] = new TextBox()));
        var lifecycle = new ContentLifecycle(registry);
        var panelBox = new TextBox();
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "p", "P", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = new PanelFactory(_ => panelBox),
        });
        state = state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => w with
                {
                    Mode = mode,
                    LastInternalMode = mode,
                    IsOpen = true,
                }),
            ],
        };
        return (registry, lifecycle, state, boxes, panelBox);
    }

    private static LayoutState WithDock(LayoutState state, TabTreeNode root, string? current) =>
        state with { DockArea = state.DockArea with { Root = root, CurrentTabId = current } };

    [AvaloniaFact]
    public void DA_6_4_focus_gain_in_content_activates()
    {
        var (registry, lifecycle, state, boxes, _) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);

        // Не клик по заголовку, а прямое получение фокуса контентом — внутризонный аналог
        // DA-E20: любой реальный focus-gained сводится к ActivateTab.
        Focus(boxes["d2"]);

        Assert.Equal("d2", St(window).DockArea.CurrentTabId);
        Assert.Equal(DockHost.MainWindow, St(window).DockArea.ActiveDockHost);
    }

    [AvaloniaFact]
    public void DA_6_4_focus_gain_clears_the_active_tool_window()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);
        Assert.Equal("p", St(window).ActiveToolWindowId);

        Focus(boxes["d1"]);

        Assert.Null(St(window).ActiveToolWindowId); // TW-6.5 через DA-6.4
        Assert.True(Panel(window, "p").IsOpen); // pinned-панель не закрывается (TW-3.2)
        Assert.DoesNotContain(":active", Decorator(window, "p").Classes);
    }

    [AvaloniaFact]
    public void TW_6_1_focus_move_into_a_document_closes_the_unpinned_panel()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockUnpinned);
        state = WithDock(state, Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        Focus(boxes["d1"]);

        Assert.False(Panel(window, "p").IsOpen); // фокус-проигравший закрылся (TW-6.1)
        Assert.Equal("d1", St(window).DockArea.CurrentTabId);
        Assert.Null(St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_6_3_esc_moves_focus_into_the_current_tab()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Group("d1", "d1", "d2"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        PressEscape(window);

        Assert.True(boxes["d1"].IsFocused); // фокус — в текущую вкладку активного хоста
        Assert.True(Panel(window, "p").IsOpen); // pinned не закрывается
        Assert.Null(St(window).ActiveToolWindowId); // активация документа сбросила поле (TW-6.5)
    }

    [AvaloniaFact]
    public void TW_6_3_esc_from_an_unpinned_panel_closes_it_by_focus_loss()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockUnpinned);
        state = WithDock(state, Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        PressEscape(window);

        Assert.True(boxes["d1"].IsFocused);
        Assert.False(Panel(window, "p").IsOpen); // закрытие — следствие потери фокуса (TW-6.1)
    }

    [AvaloniaFact]
    public void TW_6_3_esc_with_an_empty_dock_is_a_noop()
    {
        var (registry, lifecycle, state, _, panelBox) = Setup(ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        PressEscape(window);

        Assert.True(panelBox.IsFocused); // фокус на месте…
        Assert.True(Panel(window, "p").IsOpen); // …и закрытия нет: Esc без цели ничего не делает
    }

    [AvaloniaFact]
    public void DA_E21_shortcut_close_returns_focus_to_the_document()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        Workspace(window).ActivateToolWindow("p"); // открыта и активна по фокусу → закрыть
        Dispatcher.UIThread.RunJobs();

        Assert.False(Panel(window, "p").IsOpen);
        Assert.True(boxes["d1"].IsFocused); // DA-E21
    }

    [AvaloniaFact]
    public void DA_E21_hide_button_close_returns_focus()
    {
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Group("d1", "d1"), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        Click(window, Part(Decorator(window, "p"), "PART_HideButton"));

        Assert.False(Panel(window, "p").IsOpen);
        Assert.True(boxes["d1"].IsFocused);
    }

    [AvaloniaFact]
    public void DA_6_4_direct_assignment_does_not_move_focus()
    {
        var (registry, lifecycle, state, boxes, _) = Setup(ToolWindowMode.DockPinned);
        state = WithDock(state, Row(Child(Group("d1", "d1"), 0.5), Child(Group("d2", "d2"), 0.5)), "d1");
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxes["d1"]);

        Workspace(window).State = St(window).ActivateTab("d2");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("d2", St(window).DockArea.CurrentTabId);
        Assert.True(boxes["d1"].IsFocused); // прямое присвоение фокус не двигает (DA-6.4)
    }

    [AvaloniaFact]
    public void DA_6_4_effective_host_degrades_to_the_main_window()
    {
        // ActiveDockHost восстановленной раскладки указывает на окно документов, которое до
        // фазы 6 не материализуется: цели фокуса деградируют к главному окну, состояние цело.
        var (registry, lifecycle, state, boxes, panelBox) = Setup(ToolWindowMode.DockPinned);
        state = state with
        {
            DockArea = new DockAreaState
            {
                Root = Group("d1", "d1"),
                CurrentTabId = "d1",
                Windows =
                [
                    new DocumentWindowState(new FloatingBounds(0, 0, 300, 200), Group("d9", "d9"), "d9"),
                ],
                ActiveDockHost = DockHost.DocumentWindow(0),
            },
        };
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(panelBox);

        PressEscape(window);

        Assert.True(boxes["d1"].IsFocused); // цель фокуса деградировала к главному окну
        // Дальше штатная проводка DA-6.4: фокус в d1 активировал её командой — активный хост
        // сменился ядром, а не молчаливой правкой представления; окно документов цело.
        Assert.Equal(DockHost.MainWindow, St(window).DockArea.ActiveDockHost);
        var docWindow = Assert.Single(St(window).DockArea.Windows);
        Assert.Equal("d9", docWindow.CurrentTabId);
    }
}
