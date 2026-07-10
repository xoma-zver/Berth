using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Berth;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Workspace composition (spec TW-2.1), the pair stacks (TW-2.3, TW-2.4), side sizes from the
/// weights (TW-2.5), the render-time minimum clamp (TW-2.8), the Undock overlay (TW-3.3) and
/// the tool window decorator. Fractions are checked against actual bounds with a tolerance
/// covering the splitter separators.
/// </summary>
public class WorkspaceLayoutTests
{
    private const double SplitterThickness = 4;

    private static double CenterWidth(Window window) =>
        window.ClientSize.Width
        - Part(window, "PART_LeftStripe").Bounds.Width
        - Part(window, "PART_RightStripe").Bounds.Width;

    [AvaloniaFact]
    public void TW_2_1_bottom_pane_spans_the_full_width_between_the_stripes()
    {
        var registry = Registry("l", "r", "b");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("l", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("r", ToolWindowSide.Right, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true },
            ],
        };

        var window = Show(state, registry);

        var bottom = BoundsIn(Part(window, "PART_BottomPane"), window);
        var left = BoundsIn(Part(window, "PART_LeftPane"), window);
        var right = BoundsIn(Part(window, "PART_RightPane"), window);
        var dock = BoundsIn(Part(window, "PART_DockArea"), window);

        // Нижняя панель — на всю ширину между стрипами; боковые и док-зона — над ней.
        Assert.Equal(Part(window, "PART_LeftStripe").Bounds.Width, bottom.X, 1.0);
        Assert.Equal(CenterWidth(window), bottom.Width, 1.0);
        Assert.True(left.Bottom <= bottom.Y + 1);
        Assert.True(right.Bottom <= bottom.Y + 1);
        Assert.True(dock.Bottom <= bottom.Y + 1);

        // Ряд над нижней панелью: левая панель → док-зона → правая панель.
        Assert.True(left.Right <= dock.X + SplitterThickness + 1);
        Assert.True(dock.Right <= right.X + 1);
    }

    [AvaloniaFact]
    public void TW_2_5_side_width_follows_the_side_weight()
    {
        var registry = Registry("l");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("l", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
            Left = new SideState(Weight: 0.25),
        };

        var window = Show(state, registry);

        var expected = 0.25 * (CenterWidth(window) - SplitterThickness);
        Assert.Equal(expected, Part(window, "PART_LeftPane").Bounds.Width, 1.0);
        // Панели закрытых сторон коллапсированы в ноль, а не сняты с дерева (TW-9.13).
        Assert.Equal(0, Part(window, "PART_RightPane").Bounds.Width, 1.0);
        Assert.Equal(0, Part(window, "PART_BottomPane").Bounds.Height, 1.0);
    }

    [AvaloniaFact]
    public void TW_2_5_bottom_height_follows_the_side_weight()
    {
        var registry = Registry("b");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true }],
            Bottom = new SideState(Weight: 0.4),
        };

        var window = Show(state, registry);

        var expected = 0.4 * (window.ClientSize.Height - SplitterThickness);
        Assert.Equal(expected, Part(window, "PART_BottomPane").Bounds.Height, 1.0);
    }

    [AvaloniaFact]
    public void TW_2_3_side_pair_stacks_vertically_at_the_derived_pair_ratio()
    {
        var registry = Registry("p", "s");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true, PairRatio = 0.7 },
                Win("s", ToolWindowSide.Left, ToolWindowGroup.Secondary) with { IsOpen = true, PairRatio = 0.3 },
            ],
        };

        var window = Show(state, registry);
        var pane = Part(window, "PART_LeftPane");
        var primary = BoundsIn(Decorator(pane, "p"), window);
        var secondary = BoundsIn(Decorator(pane, "s"), window);

        // Primary сверху, Secondary снизу (TW-2.3); сплиттер пары — по выводимой доле R1.
        Assert.Equal(primary.X, secondary.X, 1.0);
        Assert.True(primary.Bottom <= secondary.Y);
        var stack = pane.Bounds.Height - SplitterThickness;
        Assert.Equal(0.7 * stack, primary.Height, 1.0);
        Assert.Equal(0.3 * stack, secondary.Height, 1.0);
    }

    [AvaloniaFact]
    public void TW_2_4_bottom_pair_splits_horizontally_at_the_derived_pair_ratio()
    {
        var registry = Registry("p", "s");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true, PairRatio = 0.6 },
                Win("s", ToolWindowSide.Bottom, ToolWindowGroup.Secondary) with { IsOpen = true, PairRatio = 0.4 },
            ],
        };

        var window = Show(state, registry);
        var pane = Part(window, "PART_BottomPane");
        var primary = BoundsIn(Decorator(pane, "p"), window);
        var secondary = BoundsIn(Decorator(pane, "s"), window);

        // Primary слева, Secondary справа (TW-2.4).
        Assert.Equal(primary.Y, secondary.Y, 1.0);
        Assert.True(primary.Right <= secondary.X);
        var stack = pane.Bounds.Width - SplitterThickness;
        Assert.Equal(0.6 * stack, primary.Width, 1.0);
        Assert.Equal(0.4 * stack, secondary.Width, 1.0);
    }

    [AvaloniaFact]
    public void TW_2_8_render_clamps_to_the_minimum_without_touching_the_state()
    {
        var registry = Registry("l");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("l", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
            Left = new SideState(Weight: 0.05),
        };

        var window = Show(state, registry, width: 400, height: 300);

        // Доля дала бы ~16 px — рендер клемпит к минимуму слоя UI, состояние не тронуто.
        Assert.True(Part(window, "PART_LeftPane").Bounds.Width >= 47.5);
        var workspace = Assert.IsType<BerthWorkspace>(window.Content);
        Assert.Same(state, workspace.State);
        Assert.Equal(0.05, state.Left.Weight);
    }

    [AvaloniaFact]
    public void TW_3_3_undock_overlay_takes_the_full_extent_at_the_side_weight()
    {
        var registry = Registry("l", "b", "u");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("l", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Secondary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
            Left = new SideState(Weight: 0.4),
        };

        var window = Show(state, registry);
        var stripeWidth = Part(window, "PART_LeftStripe").Bounds.Width;
        var overlay = BoundsIn(Decorator(Part(window, "PART_UndockOverlay"), "u"), window);
        var bottom = BoundsIn(Part(window, "PART_BottomPane"), window);
        var pane = Part(window, "PART_LeftPane");

        // Оверлей прижат к стороне прописки на полную высоту — поверх области нижней панели —
        // толщиной, равной весу стороны: он накрывает докированную соседку (TW-3.3), чьи
        // размеры показом не изменились.
        Assert.Equal(stripeWidth, overlay.X, 1.0);
        Assert.Equal(window.ClientSize.Height, overlay.Height, 1.0);
        Assert.True(overlay.Bottom > bottom.Y); // поверх нижней панели
        Assert.Equal(0.4 * CenterWidth(window), overlay.Width, 1.0);
        var expectedPane = 0.4 * (CenterWidth(window) - SplitterThickness);
        Assert.Equal(expectedPane, pane.Bounds.Width, 1.0);
        Assert.True(overlay.Right >= BoundsIn(pane, window).Right - 1.0); // накрывает докированную
    }

    [AvaloniaFact]
    public void TW_3_3_bottom_undock_overlay_spans_the_width_between_the_stripes()
    {
        var registry = Registry("u");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
            Bottom = new SideState(Weight: 0.4),
        };

        var window = Show(state, registry);
        var overlay = BoundsIn(Decorator(window, "u"), window);

        Assert.Equal(CenterWidth(window), overlay.Width, 1.0);
        Assert.Equal(0.4 * window.ClientSize.Height, overlay.Height, 1.0);
        Assert.Equal(window.ClientSize.Height, overlay.Bottom, 1.0);
    }

    [AvaloniaFact]
    public void TW_3_3_undock_overlay_backdrop_is_opaque()
    {
        var registry = Registry("l", "u");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("l", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Secondary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
        };

        var window = Show(state, registry);

        // Панели под оверлеем не просвечивают (TW-3.3): скелетные кисти полупрозрачны,
        // поэтому у каждой записи оверлея — непрозрачная подложка.
        var backdrop = Assert.IsAssignableFrom<Border>(Part(window, "PART_OverlayBackdrop"));
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(backdrop.Background);
        Assert.Equal(0xFF, brush.Color.A);
        Assert.NotNull(Decorator(backdrop, "u")); // подложка оборачивает именно оверлейный декоратор
    }

    [AvaloniaFact]
    public void Decorator_shows_the_title_and_the_chrome_buttons()
    {
        var registry = Registry("p:Project");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };

        var window = Show(state, registry);
        var decorator = Decorator(window, "p");

        Assert.Equal("Project", decorator.Title);
        Assert.Contains(
            decorator.GetVisualDescendants().OfType<TextBlock>(),
            t => string.Equals(t.Text, "Project", StringComparison.Ordinal));
        Assert.NotNull(TryPart(decorator, "PART_MenuButton"));
        Assert.NotNull(TryPart(decorator, "PART_HideButton"));
        Assert.NotNull(TryPart(decorator, "PART_Content"));
    }

    [AvaloniaFact]
    public void Sleeping_open_window_renders_with_its_id_as_the_title()
    {
        // Спящая запись может быть открытой (E11 сохраняет открытость) — декоратор есть,
        // заголовок — id: без регистрации нет Title (ADR-0003).
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("sleeper", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };

        var window = Show(state, new ToolWindowRegistry());

        Assert.Equal("sleeper", Decorator(window, "sleeper").Title);
    }

    [AvaloniaFact]
    public void Workspace_renders_a_rich_state_and_does_not_materialize_floating_windows()
    {
        var registry = Registry("lp", "ls", "b", "u", "f", "hidden");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("lp", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("ls", ToolWindowSide.Left, ToolWindowGroup.Secondary) with { IsOpen = true },
                Win("b", ToolWindowSide.Bottom, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("u", ToolWindowSide.Right, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
                Win("f", ToolWindowSide.Right, ToolWindowGroup.Primary, order: 1) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Float,
                    FloatingBounds = new FloatingBounds(10, 10, 300, 200),
                },
                Win("hidden", ToolWindowSide.Right, ToolWindowGroup.Secondary) with { IsIconVisible = false },
                Win("sleeper", ToolWindowSide.Bottom, ToolWindowGroup.Secondary),
            ],
            ActiveToolWindowId = "lp",
        };

        var window = Show(state, registry);

        // Float/Window не материализуются до фазы плавающих окон — но их иконки живут и
        // подсвечены открытостью; Undock — оверлеем; спящая запись кнопки не даёт.
        Assert.Equal(["b", "lp", "ls", "u"], Decorators(window).Select(d => d.ToolWindowId).Order());
        Assert.True(Button(window, "f").IsOpen);
        Assert.NotNull(TryPart(window, "PART_QuickAccess"));
    }

    [AvaloniaFact]
    public void Refresh_projects_registry_changes_invisible_to_the_property_system()
    {
        // Живая регистрация мутирует реестр на месте и для спящей записи возвращает тот же
        // (и вообще value-equal) LayoutState — присвоение дедуплицируется системой свойств,
        // проекцию обновляет явный Refresh (контракт BerthWorkspace).
        var registry = new ToolWindowRegistry();
        var lifecycle = new ContentLifecycle(registry);
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("sleeper", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var window = Show(state, registry);
        var workspace = Assert.IsType<BerthWorkspace>(window.Content);
        Assert.Empty(Buttons(window)); // спящая запись без регистрации кнопки не даёт

        workspace.State = lifecycle.Register(workspace.State!, new ToolWindowDescriptor(
            "sleeper", "Sleeper", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        Assert.Empty(Buttons(window)); // присвоение равного состояния перестройку не вызвало

        workspace.Refresh();

        Assert.Equal(["sleeper"], Buttons(window).Select(b => b.ToolWindowId));
    }

    [AvaloniaFact]
    public void Workspace_without_a_state_renders_nothing()
    {
        var window = new Window { Content = new BerthWorkspace() };
        window.Show();

        Assert.Empty(Buttons(window));
        Assert.Empty(Decorators(window));
    }
}
