using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The content bridge of the walking skeleton (spec TW-9.3, TW-9.5, TW-9.2): with a
/// <see cref="ContentLifecycle"/> attached, the decorator body materializes through the
/// factory bridge — a Control hosted directly, anything else presented through the
/// application's data templates — and every gesture reports its transition; without a
/// coordinator, and wherever there is nothing to materialize, the body stays a placeholder.
/// </summary>
public class ContentBridgeTests
{
    private sealed class CountingFactory(Func<string, object>? create = null) : IToolWindowContentFactory
    {
        private readonly Func<string, object> _create = create ?? (_ => new TextBlock());

        public int Created { get; private set; }

        public int Released { get; private set; }

        public object CreateContent(string toolWindowId)
        {
            Created++;
            return _create(toolWindowId);
        }

        public void ReleaseContent(string toolWindowId, object content) => Released++;
    }

    private sealed record BodyModel(string Text);

    /// <summary>Registry + coordinator with one registered window «a»; registration seeds the body tab (TW-9.5).</summary>
    private static (ToolWindowRegistry Registry, ContentLifecycle Lifecycle, LayoutState State) Setup(
        CountingFactory factory,
        ContentRetentionPolicy retention = ContentRetentionPolicy.KeepWhileRegistered)
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            RetentionPolicy = retention,
            ContentFactory = factory,
        });
        return (registry, lifecycle, state);
    }

    /// <summary>The body view: with 4.1 the body lives in its tab host inside the panel tree (TW-9.5, DA-9.6).</summary>
    private static Control? Body(Window window) => TabHost(window, "a").Child;

    [AvaloniaFact]
    public void TW_9_3_decorator_materializes_the_body_through_the_bridge()
    {
        var factory = new CountingFactory(_ => new TextBlock { Text = "body" });
        var (registry, lifecycle, state) = Setup(factory);

        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);

        var text = Assert.IsType<TextBlock>(Body(window));
        Assert.Equal("body", text.Text);
        Assert.Equal(1, factory.Created); // OnFirstOpen: создан первой материализацией
    }

    [AvaloniaFact]
    public void TW_9_3_the_content_instance_survives_reopen_and_rebuilds()
    {
        var control = new TextBlock { Text = "body" };
        var factory = new CountingFactory(_ => control);
        var (registry, lifecycle, state) = Setup(factory);
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);

        Click(window, Button(window, "a")); // закрытие: хост снят с раскладки, контент жив
        Assert.Empty(Decorators(window));
        Assert.Equal(0, factory.Released);

        Click(window, Button(window, "a")); // переоткрытие: тот же хост с тем же экземпляром (TW-9.13)
        Assert.Same(control, Body(window));
        Assert.Equal(1, factory.Created);
    }

    [AvaloniaFact]
    public void TW_9_3_move_while_open_relays_the_live_instance()
    {
        var control = new TextBlock { Text = "body" };
        var factory = new CountingFactory(_ => control);
        var (registry, lifecycle, state) = Setup(factory);
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);
        var workspace = (BerthWorkspace)window.Content!;

        // Прямое присвоение State — путь приложения: перекладка хоста при живом контенте
        // (перемещение в другой слот — легальное переприсоединение, TW-9.13).
        workspace.State = workspace.State!.Move(
            "a", new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(control, Body(window));
        Assert.Equal(1, factory.Created);
    }

    [AvaloniaFact]
    public void TW_9_2_dispose_on_close_releases_and_recreates_on_the_click_path()
    {
        var factory = new CountingFactory();
        var (registry, lifecycle, state) = Setup(factory, ContentRetentionPolicy.DisposeOnClose);
        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);
        Assert.Equal(1, factory.Created);

        Click(window, Button(window, "a")); // Execute докладывает переход — DisposeOnClose освобождает
        Assert.Equal(1, factory.Released);

        Click(window, Button(window, "a")); // повторное открытие пересоздаёт контент
        Assert.Equal(2, factory.Created);
        Assert.IsType<TextBlock>(Body(window)); // новый вид над новым контентом
    }

    [AvaloniaFact]
    public void TW_9_3_non_control_content_is_presented_through_app_data_templates()
    {
        var factory = new CountingFactory(_ => new BodyModel("templated"));
        var (registry, lifecycle, state) = Setup(factory);

        // Шаблон — до присвоения контента: презентер резолвит DataTemplates при аттаче.
        var window = new Window { Width = 800, Height = 600 };
        window.DataTemplates.Add(new FuncDataTemplate<BodyModel>((model, _) =>
            new TextBlock { Text = model.Text }));
        window.Content = new BerthWorkspace
        {
            State = state.Open("a"),
            Registry = registry,
            Lifecycle = lifecycle,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Вид построен шаблоном приложения однократно и хостится напрямую (MVVM-путь):
        // ContentPresenter не используется — он перестраивал бы вид при каждом
        // переприсоединении, ломая удержание вида (TW-9.13).
        var built = Assert.IsType<TextBlock>(Body(window));
        Assert.Equal("templated", built.Text);
        Assert.IsType<BodyModel>(built.DataContext);
    }

    [AvaloniaFact]
    public void TW_9_3_without_a_lifecycle_the_body_stays_a_placeholder()
    {
        var factory = new CountingFactory();
        var (registry, _, state) = Setup(factory);

        var window = Show(state.Open("a"), registry); // Lifecycle не задан — статический скелет

        // Хост тела показывает заглушку с заголовком (DA-9.6), фабрика не тронута.
        var placeholder = Assert.IsType<TextBlock>(Body(window));
        Assert.Equal("Alpha", placeholder.Text);
        Assert.Equal(0, factory.Created);
    }

    [AvaloniaFact]
    public void TW_9_3_window_without_a_body_factory_keeps_the_placeholder()
    {
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = lifecycle.Register(LayoutState.Empty, new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));

        var window = Show(state.Open("a"), registry, lifecycle: lifecycle);

        Assert.Empty(TabHosts(window)); // нет фабрики — нет тела (TW-9.5); дерево пусто (DA-8.4)
    }

    [AvaloniaFact]
    public void DA_9_4_sleeping_open_window_keeps_the_placeholder()
    {
        // Открытая спящая запись (E11 сохраняет открытость): без регистрации материализовывать
        // нечего — заглушка, и мост не обращается к координатору (GetOrCreate бросил бы).
        var lifecycle = new ContentLifecycle(new ToolWindowRegistry());
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("sleeper", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = new TabGroupNode { Tabs = ["sleeper"], ActiveTabId = "sleeper" },
                },
            ],
        };

        var window = Show(state, new ToolWindowRegistry(), lifecycle: lifecycle);

        // Спящая вкладка — заглушка (DA-9.4): координатор не тронут, контента нет.
        var placeholder = Assert.IsType<TextBlock>(TabHost(window, "sleeper").Child);
        Assert.Equal("sleeper", placeholder.Text);
    }

    [AvaloniaFact]
    public void TW_9_2_body_living_in_a_dock_host_is_materialized_there_not_by_the_decorator()
    {
        var factory = new CountingFactory(_ => new TextBlock { Text = "body" });
        var (registry, lifecycle, state) = Setup(factory);

        // Тело переезжает в док-зону штатными командами (DA-8.1); декоратору панели
        // материализовывать нечего — тело показывает хост док-зоны (DA-9.6), через ту же
        // единую запись моста TW-9.5.
        var moved = state.Open("a")
            .OpenDocument("d", registry)
            .MoveTab("a", DockGroupRef.AtTab("d"), 1, registry);
        var window = Show(moved, registry, lifecycle: lifecycle);

        Assert.Empty(TabHosts(Decorator(window, "a"))); // декоратор панели тело не хостит
        var body = Assert.IsType<TextBlock>(TabHost(window, "a").Child);
        Assert.Equal("body", body.Text);
        Assert.Equal(1, factory.Created);
        Assert.Same(body, lifecycle.GetOrCreateToolWindowContent("a")); // единая запись контента
    }
}
