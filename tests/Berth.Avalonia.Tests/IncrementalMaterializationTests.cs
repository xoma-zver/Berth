using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Incremental materialization (spec TW-9.13): hosts of remaining tool windows are neither
/// recreated nor reattached by state changes — keyboard focus and view-state are never lost to
/// materialization itself; reattachment happens only on an actual move to another slot or
/// layer; a closed KeepWhileRegistered host retains the built view until reopening, a
/// DisposeOnClose host drops both the content and the view. Reattachment is observed through
/// DetachedFromVisualTree — the mechanism behind any focus loss.
/// </summary>
public class IncrementalMaterializationTests
{
    private sealed class CountingFactory(Func<string, object> create) : IToolWindowContentFactory
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

    private sealed record BodyModel(string Text);

    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static Func<int> TrackDetach(Control control)
    {
        var count = 0;
        control.DetachedFromVisualTree += (_, _) => count++;
        return () => count;
    }

    /// <summary>The body tab host of a decorator: with 4.1 the body view lives there (DA-9.6), not in PART_Content directly.</summary>
    private static DockTabHost BodyHost(ToolWindowDecorator host) => TabHost(host, host.ToolWindowId);

    private static TextBlock BuiltView(ToolWindowDecorator host) => (TextBlock)BodyHost(host).Child!;

    /// <summary>Registry + coordinator with a templated-body window «a» and a plain window «b» in the given slot.</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State, CountingFactory Factory)
        TemplatedSetup(ToolWindowSlot slotB, ContentRetentionPolicy retention = ContentRetentionPolicy.KeepWhileRegistered)
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var factory = new CountingFactory(_ => new BodyModel("view"));
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            RetentionPolicy = retention,
            ContentFactory = factory,
        });
        state = lifecycle.Register(state, new ToolWindowDescriptor("b", "Beta", slotB));
        return (registry, lifecycle, state, factory);
    }

    private static Window ShowTemplated(LayoutState state, ToolWindowRegistry registry, ContentLifecycle lifecycle)
    {
        var window = new Window { Width = 800, Height = 600 };
        window.DataTemplates.Add(new FuncDataTemplate<BodyModel>((model, _) =>
            new TextBlock { Text = model.Text }));
        window.Content = new BerthWorkspace { State = state, Registry = registry, Lifecycle = lifecycle };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void TW_9_13_activating_open_does_not_reattach_remaining_hosts()
    {
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary),
            ],
        };
        var window = Show(state, registry);
        var host = Decorator(window, "a");
        var detached = TrackDetach(host);

        // Активирующее открытие другой панели (TW-5.1): фокус вправе перенести семантика
        // команды, но хост остающейся панели не переприсоединяется (TW-9.13).
        Click(window, Button(window, "b"));

        Assert.Equal(0, detached());
        Assert.Same(host, Decorator(window, "a"));
        Assert.Same(host, Decorator(Part(window, "PART_LeftPane"), "a"));
    }

    [AvaloniaFact]
    public void TW_9_13_focus_survives_non_activating_changes()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var box = new TextBox();
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = new CountingFactory(_ => box),
        });
        state = lifecycle.Register(state, new ToolWindowDescriptor(
            "b", "Beta", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary)));
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);
        var workspace = Workspace(window);
        Assert.True(box.Focus());

        // Ресайз — только перекладка геометрии (TW-9.13): фокус в теле не теряется.
        workspace.State = workspace.State!.SetSideSize(ToolWindowSide.Left, 0.45);
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        // Образование пары стороны — перестройка компоновки вокруг остающейся панели:
        // неактивирующее открытие соседки не трогает ни хост, ни фокус (TW-9.13).
        workspace.State = workspace.State!.Open("b", activate: false);
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        // Распад пары: закрытие соседки возвращает панели всю сторону — фокус жив.
        workspace.State = workspace.State!.Close("b");
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
    }

    [AvaloniaFact]
    public void TW_9_13_pair_formation_and_dissolution_keep_the_remaining_host()
    {
        var registry = Registry("a", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Secondary),
            ],
        };
        var window = Show(state, registry);
        var host = Decorator(window, "a");
        var detached = TrackDetach(host);

        Click(window, Button(window, "b")); // пара образовалась
        Assert.Equal(2, Decorators(window).Count);

        Click(window, Button(window, "b")); // пара распалась
        Assert.Equal(0, detached());
        Assert.Same(host, Decorator(window, "a"));
    }

    [AvaloniaFact]
    public void TW_9_13_move_to_another_slot_relays_the_same_host()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);
        var host = Decorator(window, "a");

        var workspace = Workspace(window);
        workspace.State = workspace.State!.Move(
            "a", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), 0);
        Dispatcher.UIThread.RunJobs();

        // Фактическое перемещение — легальное переприсоединение того же хоста (TW-9.13).
        Assert.Same(host, Decorator(Part(window, "PART_RightPane"), "a"));
    }

    [AvaloniaFact]
    public void TW_9_13_layer_change_relays_the_host_into_the_overlay()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, registry);
        var host = Decorator(window, "a");

        var workspace = Workspace(window);
        workspace.State = workspace.State!.SetMode("a", ToolWindowMode.Undock);
        Dispatcher.UIThread.RunJobs();

        // Смена слоя ({Dock*} ↔ Undock) — легальное переприсоединение (TW-9.13).
        Assert.Same(host, Decorator(Part(window, "PART_UndockOverlay"), "a"));

        workspace.State = workspace.State!.SetMode("a", ToolWindowMode.DockPinned);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(host, Decorator(Part(window, "PART_LeftPane"), "a"));
    }

    [AvaloniaFact]
    public void TW_9_13_overlay_toggle_leaves_the_docked_host_alone()
    {
        var registry = Registry("a", "u");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Secondary) with
                {
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
        };
        var window = Show(state, registry);
        var host = Decorator(window, "a");
        var detached = TrackDetach(host);

        Click(window, Button(window, "u")); // оверлей появился поверх (INV-2)
        Assert.NotNull(Decorator(Part(window, "PART_UndockOverlay"), "u"));

        Click(window, Button(window, "u")); // оверлей ушёл
        Assert.Equal(0, detached());
        Assert.Same(host, Decorator(window, "a"));
    }

    [AvaloniaFact]
    public void TW_9_13_built_view_survives_unrelated_commands_and_keep_reopen()
    {
        var (registry, lifecycle, state, factory) =
            TemplatedSetup(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary));
        var window = ShowTemplated(state.Open("a"), registry, lifecycle);
        var host = Decorator(window, "a");
        var view = BuiltView(host);

        Click(window, Button(window, "b")); // несвязанная команда: вид тот же (TW-9.13)
        Assert.Same(view, BuiltView(host));

        Click(window, Button(window, "a")); // закрытие: хост снят, вид удержан (Keep)
        Assert.DoesNotContain(Decorators(window), d => string.Equals(d.ToolWindowId, "a", StringComparison.Ordinal));

        Click(window, Button(window, "a")); // переоткрытие: тот же вид, контент не пересоздан
        Assert.Same(view, BuiltView(Decorator(window, "a")));
        Assert.Equal(1, factory.Created);
        Assert.Equal(0, factory.Released);
    }

    [AvaloniaFact]
    public void TW_9_13_focus_survives_inside_a_template_built_view()
    {
        // Симметрия с Control-путём: фокусируемый элемент внутри построенного шаблоном
        // вида переживает неактивирующие изменения — вид не пересобирается (TW-9.13).
        var (registry, lifecycle, state, _) =
            TemplatedSetup(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary));
        var window = new Window { Width = 800, Height = 600 };
        window.DataTemplates.Add(new FuncDataTemplate<BodyModel>((_, _) => new TextBox()));
        window.Content = new BerthWorkspace { State = state.Open("a"), Registry = registry, Lifecycle = lifecycle };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = Assert.IsType<TextBox>(BodyHost(Decorator(window, "a")).Child);
        Assert.True(box.Focus());

        var workspace = Workspace(window);
        workspace.State = workspace.State!.Open("b", activate: false); // пара образовалась
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        workspace.State = workspace.State!.SetSideSize(ToolWindowSide.Left, 0.5);
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
    }

    [AvaloniaFact]
    public void TW_9_13_dispose_on_close_drops_the_view()
    {
        var (registry, lifecycle, state, factory) = TemplatedSetup(
            new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary),
            ContentRetentionPolicy.DisposeOnClose);
        var window = ShowTemplated(state.Open("a"), registry, lifecycle);
        var host = Decorator(window, "a");
        var view = BuiltView(host);

        Click(window, Button(window, "a")); // закрытие освобождает контент — и вид над ним

        Assert.Equal(1, factory.Released);
        // Хост тела не удерживает ни контент, ни вид (TW-9.13, DA-9.6): сброшен к заглушке.
        Assert.NotSame(view, BodyHost(host).Child);
        Assert.Equal("Alpha", ((TextBlock)BodyHost(host).Child!).Text);

        Click(window, Button(window, "a")); // переоткрытие строит новый вид над новым контентом
        Assert.Equal(2, factory.Created);
        Assert.NotSame(view, BuiltView(Decorator(window, "a")));
    }

    [AvaloniaFact]
    public void TW_9_13_splitter_commit_keeps_focus_and_hosts()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var box = new TextBox();
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            ContentFactory = new CountingFactory(_ => box),
        });
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);
        var host = Decorator(window, "a");
        var detached = TrackDetach(host);
        Assert.True(box.Focus());

        var start = Center(Part(window, "PART_LeftSideSplitter"), window);
        var end = new Point(start.X + 80, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Коммит ресайза — перекладка геометрии in-place (TW-9.13): вес записан, а хост
        // и клавиатурный фокус в теле не тронуты.
        Assert.NotEqual(LayoutDefaults.SideWeight, Workspace(window).State!.Left.Weight);
        Assert.Equal(0, detached());
        Assert.True(box.IsFocused);
    }

    [AvaloniaFact]
    public void TW_9_13_live_registration_keeps_hosts()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);
        var host = Decorator(window, "a");
        var detached = TrackDetach(host);

        var workspace = Workspace(window);
        workspace.State = lifecycle.Register(workspace.State!, new ToolWindowDescriptor(
            "c", "Gamma", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        workspace.Refresh(); // мутация реестра невидима системе свойств (контракт BerthWorkspace)
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, detached());
        Assert.Same(host, Decorator(window, "a"));
        Assert.Contains(Buttons(window), b => string.Equals(b.ToolWindowId, "c", StringComparison.Ordinal));
    }
}
