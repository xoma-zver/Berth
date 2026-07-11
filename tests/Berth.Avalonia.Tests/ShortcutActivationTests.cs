using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The activation shortcut and its wiring (spec TW-5.5, TW-6.4, TW-6.6): the tri-state public
/// entry decides «active» by keyboard focus, the stripe icon tooltip carries the
/// application-supplied shortcut hint, and a command that factually reattaches the active
/// window's host (the whitelist of TW-9.13) restores the keyboard focus it dropped — the
/// extended trigger of TW-6.6. The platform fact behind the reattach tests (probe, 2026-07):
/// detaching the focused element clears focus to nowhere without raising GotFocus, so
/// auto-hide never fires on the transient loss.
/// </summary>
public class ShortcutActivationTests
{
    private sealed class Factory(Func<string, object> create) : IToolWindowContentFactory
    {
        public object CreateContent(string toolWindowId) => create(toolWindowId);

        public void ReleaseContent(string toolWindowId, object content)
        {
        }
    }

    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Get(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static LayoutState Register(
        ContentLifecycle lifecycle,
        LayoutState state,
        string id,
        ToolWindowSide side,
        ToolWindowGroup group,
        Func<string, object> create) =>
        lifecycle.Register(state, new ToolWindowDescriptor(id, id, new ToolWindowSlot(side, group))
        {
            ContentFactory = new Factory(create),
        });

    private static LayoutState OpenAs(LayoutState state, string id, ToolWindowMode mode) =>
        state with
        {
            ToolWindows =
            [
                .. state.ToolWindows.Select(w => string.Equals(w.Id, id, StringComparison.Ordinal)
                    ? w with
                    {
                        Mode = mode,
                        LastInternalMode = mode.IsInternal() ? mode : LayoutDefaults.LastInternalMode,
                        IsOpen = true,
                    }
                    : w),
            ],
        };

    private static void Focus(Control control)
    {
        Assert.True(control.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    private static Func<int> CountStateChanges(BerthWorkspace workspace)
    {
        var changes = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.Property == BerthWorkspace.StateProperty)
            {
                changes++;
            }
        };
        return () => changes;
    }

    private static MenuItem Item(ItemCollection items, string header) =>
        items.OfType<MenuItem>().First(i => string.Equals(i.Header as string, header, StringComparison.Ordinal));

    private static void Invoke(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void ActivateShortcut(Window window, string id)
    {
        Workspace(window).ActivateToolWindow(id);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- TW-5.5: the tri-state shortcut ----

    [AvaloniaFact]
    public void TW_5_5_shortcut_opens_and_activates_a_closed_panel()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        var window = Show(state, registry, lifecycle: lifecycle);
        var changes = CountStateChanges(Workspace(window));

        ActivateShortcut(window, "a");

        Assert.True(Get(window, "a").IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(boxA.IsFocused); // активация перенесла фокус в контент (TW-6.6)
        Assert.Equal(1, changes()); // один шорткат — одна команда (ADR-0004)
    }

    [AvaloniaFact]
    public void TW_5_5_shortcut_activates_an_open_unfocused_panel()
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
        Focus(boxB);

        ActivateShortcut(window, "a");

        Assert.True(Get(window, "a").IsOpen);
        Assert.True(Get(window, "b").IsOpen);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(boxA.IsFocused);
    }

    [AvaloniaFact]
    public void TW_5_5_shortcut_closes_the_open_and_focused_panel()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);

        ActivateShortcut(window, "a");

        Assert.False(Get(window, "a").IsOpen);
        Assert.Null(St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_5_5_active_means_focus_not_the_stored_id()
    {
        // Хранимый ActiveToolWindowId до фазы 4 означает «последняя активная» (TW-6.6):
        // развилку «активировать или закрыть» решает клавиатурный фокус — как у эталона,
        // чей active вычисляется из фокуса. Хранимая проверка закрыла бы открытую панель.
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

        workspace.State = St(window).Open("a"); // активна по полю, фокус остался в b (TW-9.3)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.True(boxB.IsFocused);

        ActivateShortcut(window, "a");

        Assert.True(Get(window, "a").IsOpen); // активация, не закрытие
        Assert.True(boxA.IsFocused); // и перенос фокуса — хотя поле не менялось (TW-5.2, TW-6.6)
    }

    // ---- TW-5.5 / TW-6.4: the tooltip hint ----

    [AvaloniaFact]
    public void TW_5_5_tooltip_carries_the_hint_from_the_provider()
    {
        var registry = Registry("a:Alpha", "b:Beta");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = Show(state, registry);

        // Смена провайдера подхватывается перепроекцией: тултипы — лист-хром (TW-9.13).
        Workspace(window).ShortcutHintProvider = id =>
            string.Equals(id, "a", StringComparison.Ordinal) ? "Alt+1" : null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Alpha  Alt+1", ToolTip.GetTip(Button(window, "a")));
        Assert.Equal("Beta", ToolTip.GetTip(Button(window, "b"))); // null-подсказка — только Title
    }

    [AvaloniaFact]
    public void TW_6_4_tooltip_without_a_provider_is_the_title()
    {
        var registry = Registry("a:Alpha");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry);

        Assert.Equal("Alpha", ToolTip.GetTip(Button(window, "a")));
    }

    // ---- TW-6.6: reattachment of the active window's host restores focus ----

    [AvaloniaFact]
    public void TW_6_6_move_of_the_active_panel_restores_focus_into_the_content()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);
        var host = Decorator(window, "a");
        var changes = CountStateChanges(Workspace(window));

        var menuButton = (Button)Part(host, "PART_MenuButton");
        var moveTo = Item(((MenuFlyout)menuButton.Flyout!).Items, "Move to");
        Invoke(Item(moveTo.Items, "Right Top"));

        var a = Get(window, "a");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), a.Slot);
        Assert.True(a.IsOpen);
        Assert.Same(host, Decorator(window, "a")); // хост пережил перекладку (TW-9.13)
        Assert.True(boxA.IsFocused); // реатач потерял фокус — командный канал восстановил (TW-6.6)
        Assert.Equal(1, changes()); // без каскадов: один Move, ничего сверх
    }

    [AvaloniaFact]
    public void TW_6_6_unpinned_move_does_not_close_the_moved_panel()
    {
        // Петля «Move открытой unpinned-панели закрывает её же» исключена: транзиентная
        // потеря фокуса от белосписочного реатача — не потеря фокуса для TW-6.1.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);
        var changes = CountStateChanges(Workspace(window));

        var moveTo = Item(((MenuFlyout)Button(window, "a").ContextFlyout!).Items, "Move to");
        Invoke(Item(moveTo.Items, "Bottom Left"));

        var a = Get(window, "a");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary), a.Slot);
        Assert.True(a.IsOpen); // автоскрытие не сработало на собственный перенос
        Assert.True(boxA.IsFocused);
        Assert.Equal(1, changes());
    }

    [AvaloniaFact]
    public void TW_6_6_layer_change_of_the_active_panel_restores_focus()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockUnpinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        Focus(boxA);
        var changes = CountStateChanges(Workspace(window));

        var menuButton = (Button)Part(Decorator(window, "a"), "PART_MenuButton");
        var viewMode = Item(((MenuFlyout)menuButton.Flyout!).Items, "View Mode");
        Invoke(Item(viewMode.Items, "Undock"));

        var a = Get(window, "a");
        Assert.Equal(ToolWindowMode.Undock, a.Mode); // другой слой: Docked → Overlay (TW-9.13)
        Assert.True(a.IsOpen);
        Assert.True(boxA.IsFocused);
        Assert.Equal(1, changes());
    }

    [AvaloniaFact]
    public void TW_6_6_move_of_an_inactive_panel_does_not_move_focus()
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
        Focus(boxB);

        var moveTo = Item(((MenuFlyout)Button(window, "a").ContextFlyout!).Items, "Move to");
        Invoke(Item(moveTo.Items, "Bottom Left"));

        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary), Get(window, "a").Slot);
        Assert.True(boxB.IsFocused); // переносится только фокус активной панели
        Assert.Equal("b", St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_6_6_reattach_transfer_requires_focus_inside_before_the_command()
    {
        // Гвард происхождения: активная по полю панель без фокуса внутри (stale «последняя
        // активная») не отнимает фокус у стороннего владельца при своём перемещении.
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
        workspace.State = St(window).Open("a"); // активна по полю, фокус в b
        Dispatcher.UIThread.RunJobs();

        var moveTo = Item(((MenuFlyout)Button(window, "a").ContextFlyout!).Items, "Move to");
        Invoke(Item(moveTo.Items, "Bottom Left"));

        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary), Get(window, "a").Slot);
        Assert.True(Get(window, "a").IsOpen);
        Assert.True(boxB.IsFocused); // фокус не был внутри a — остался у владельца
    }

    [AvaloniaFact]
    public void TW_6_6_direct_assignment_move_does_not_restore_focus()
    {
        // Фокус переносит только командный канал: прямое присвоение State — нет (TW-6.6);
        // реатач роняет фокус в никуда (headless-проба), и никто его не восстанавливает.
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var boxA = new TextBox();
        var state = Register(lifecycle, LayoutState.Empty, "a", ToolWindowSide.Left, ToolWindowGroup.Primary, _ => boxA);
        state = OpenAs(state, "a", ToolWindowMode.DockPinned);
        var window = Show(state, registry, lifecycle: lifecycle);
        var workspace = Workspace(window);
        Focus(boxA);
        var changes = CountStateChanges(workspace);

        workspace.State = St(window).Move(
            "a", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), 0);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Get(window, "a").IsOpen);
        Assert.False(boxA.IsFocused);
        Assert.Equal(1, changes()); // и никаких наведённых команд
    }
}
