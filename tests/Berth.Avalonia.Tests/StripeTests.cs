using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Stripes (spec TW-1.2…TW-1.4), open-icon indication (TW-6.4) and the quick access button
/// (TW-8.1, TW-8.4). Geometry is asserted in window coordinates — what the user actually sees.
/// </summary>
public class StripeTests
{
    [AvaloniaFact]
    public void TW_1_2_left_stripe_segments_run_top_to_bottom_with_the_bottom_segment_at_the_edge()
    {
        var registry = Registry("lp", "ls", "bp", "hidden");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("lp", ToolWindowSide.Left, ToolWindowGroup.Primary),
                Win("ls", ToolWindowSide.Left, ToolWindowGroup.Secondary),
                Win("bp", ToolWindowSide.Bottom, ToolWindowGroup.Primary),
                Win("hidden", ToolWindowSide.Right, ToolWindowGroup.Primary) with { IsIconVisible = false },
            ],
        };

        var window = Show(state, registry);
        var stripe = Part(window, "PART_LeftStripe");

        // Все три сегмента — на левом стрипе (TW-1.1, TW-1.2), правый пуст.
        Assert.Equal(["lp", "ls", "bp"], Buttons(stripe).Select(b => b.ToolWindowId));
        Assert.Empty(Buttons(Part(window, "PART_RightStripe")));

        // Сверху вниз: Primary → разделитель → Secondary → «⋯»; нижний сегмент — у нижнего края.
        var lp = BoundsIn(Button(stripe, "lp"), window);
        var separator = BoundsIn(Part(stripe, "PART_StripeSeparator"), window);
        var ls = BoundsIn(Button(stripe, "ls"), window);
        var quick = BoundsIn(Part(stripe, "PART_QuickAccess"), window);
        var bp = BoundsIn(Button(stripe, "bp"), window);
        Assert.True(lp.Bottom <= separator.Y && separator.Bottom <= ls.Y && ls.Bottom <= quick.Y);
        Assert.True(bp.Y > quick.Bottom);
        Assert.True(bp.Bottom >= BoundsIn(stripe, window).Bottom - 10);
    }

    [AvaloniaFact]
    public void TW_1_2_right_stripe_mirrors_with_right_segments_and_bottom_secondary()
    {
        var registry = Registry("rp", "rs", "bs");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("rp", ToolWindowSide.Right, ToolWindowGroup.Primary),
                Win("rs", ToolWindowSide.Right, ToolWindowGroup.Secondary),
                Win("bs", ToolWindowSide.Bottom, ToolWindowGroup.Secondary),
            ],
        };

        var window = Show(state, registry);

        Assert.Equal(["rp", "rs", "bs"], Buttons(Part(window, "PART_RightStripe")).Select(b => b.ToolWindowId));
        Assert.Empty(Buttons(Part(window, "PART_LeftStripe")));
    }

    [AvaloniaFact]
    public void TW_1_3_separator_is_shown_only_when_both_segments_are_non_empty()
    {
        var registry = Registry("p", "s");
        var both = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary),
                Win("s", ToolWindowSide.Left, ToolWindowGroup.Secondary),
            ],
        };
        var primaryOnly = LayoutState.Empty with
        {
            ToolWindows = [Win("p", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var secondaryOnly = LayoutState.Empty with
        {
            ToolWindows = [Win("s", ToolWindowSide.Left, ToolWindowGroup.Secondary)],
        };

        Assert.NotNull(TryPart(Part(Show(both, registry), "PART_LeftStripe"), "PART_StripeSeparator"));
        Assert.Null(TryPart(Part(Show(primaryOnly, registry), "PART_LeftStripe"), "PART_StripeSeparator"));
        Assert.Null(TryPart(Part(Show(secondaryOnly, registry), "PART_LeftStripe"), "PART_StripeSeparator"));
    }

    [AvaloniaFact]
    public void TW_1_4_icons_follow_the_order_and_the_bottom_segment_grows_upward()
    {
        var registry = Registry("a", "b", "c", "d", "e");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                // Порядок в списке состояния нарочно перемешан — сортирует Order.
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
                Win("c", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 2),
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0),
                Win("e", ToolWindowSide.Bottom, ToolWindowGroup.Primary, order: 1),
                Win("d", ToolWindowSide.Bottom, ToolWindowGroup.Primary, order: 0),
            ],
        };

        var window = Show(state, registry);
        var stripe = Part(window, "PART_LeftStripe");

        // Верхний сегмент: Order растёт сверху вниз.
        Assert.True(BoundsIn(Button(stripe, "a"), window).Y < BoundsIn(Button(stripe, "b"), window).Y);
        Assert.True(BoundsIn(Button(stripe, "b"), window).Y < BoundsIn(Button(stripe, "c"), window).Y);

        // Нижний сегмент растёт снизу вверх: Order 0 — ближайшая к нижнему краю (TW-1.4).
        Assert.True(BoundsIn(Button(stripe, "d"), window).Y > BoundsIn(Button(stripe, "e"), window).Y);
    }

    [AvaloniaFact]
    public void TW_6_4_open_icons_are_highlighted()
    {
        var registry = Registry("open", "closed");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("open", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0) with { IsOpen = true },
                Win("closed", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };

        var window = Show(state, registry);

        var open = Button(window, "open");
        var closed = Button(window, "closed");
        Assert.True(open.IsOpen);
        Assert.Contains(":open", open.Classes);
        Assert.False(closed.IsOpen);
        Assert.DoesNotContain(":open", closed.Classes);
    }

    [AvaloniaFact]
    public void TW_8_1_quick_access_sits_on_the_configured_stripe_after_the_secondary_segment()
    {
        var registry = Registry("rs", "hidden");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("rs", ToolWindowSide.Right, ToolWindowGroup.Secondary),
                Win("hidden", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsIconVisible = false },
            ],
            QuickAccessSide = QuickAccessSide.Right,
        };

        var window = Show(state, registry);

        var right = Part(window, "PART_RightStripe");
        var quick = Part(right, "PART_QuickAccess");
        Assert.Null(TryPart(Part(window, "PART_LeftStripe"), "PART_QuickAccess"));
        Assert.True(BoundsIn(quick, window).Y >= BoundsIn(Button(right, "rs"), window).Bottom);
    }

    [AvaloniaFact]
    public void TW_8_4_quick_access_is_hidden_while_the_list_is_empty()
    {
        var registry = Registry("a");
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };

        var window = Show(state, registry);

        Assert.Null(TryPart(window, "PART_QuickAccess"));
    }

    [AvaloniaFact]
    public void StripeButton_shows_the_application_image_resource_by_icon_key()
    {
        // IconKey — идентификатор ресурса приложения (ADR-0003): ядро хранит строку, UI
        // резолвит её при аттаче; отсутствие ресурса или не-IImage дают фолбэк-инициалы.
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor(
            "a", "Alpha", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary))
        {
            IconKey = "AlphaIcon",
        });
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };
        var icon = new DrawingImage();
        var window = new Window { Width = 800, Height = 600 };
        // Ресурс — до присвоения контента: резолв IconKey происходит при аттаче к
        // логическому дереву, а контент окна аттачится сразу при установке.
        window.Resources.Add("AlphaIcon", icon);
        window.Content = new BerthWorkspace { State = state, Registry = registry };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = Button(window, "a");
        var image = button.GetVisualDescendants().OfType<Image>().Single();
        Assert.Same(icon, image.Source);
        Assert.Equal("Alpha", ToolTip.GetTip(button)); // тултип — Title
    }

    [AvaloniaFact]
    public void StripeButton_initials_do_not_split_surrogate_pairs()
    {
        // Инициалы берутся текстовыми элементами: эмодзи в Title не должен давать битый глиф.
        var registry = new ToolWindowRegistry();
        registry.Register(new ToolWindowDescriptor(
            "r", "🚀 Launcher", new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("r", ToolWindowSide.Left, ToolWindowGroup.Primary)],
        };

        var window = Show(state, registry);

        var text = Button(window, "r").GetVisualDescendants().OfType<TextBlock>().Single().Text;
        Assert.Equal("🚀L", text);
    }

    [AvaloniaFact]
    public void StripeIconFace_shows_a_placeholder_for_an_empty_title()
    {
        // The icon face is a reusable widget (the stripe button and the drag ghost): it must
        // render the «?» fallback for an empty title on its own, without relying on the
        // descriptor's non-empty-title invariant. Setting Title to "" raises no
        // property-changed (it equals the default), so the initials must be seeded at
        // construction — the guard this pins.
        var face = new StripeIconFace(iconKey: null, title: string.Empty);

        Assert.Equal("?", ((TextBlock)face.Child!).Text);
    }

    [AvaloniaFact]
    public void Stripe_shows_no_button_for_a_sleeping_window()
    {
        // Спящая запись без регистрации не даёт кнопки: без дескриптора нет Title и иконки
        // (ADR-0003, TW-10.2); её состояние при этом живёт в раскладке.
        var registry = Registry("live");
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("live", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 0),
                Win("sleeper", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };

        var window = Show(state, registry);

        Assert.Equal(["live"], Buttons(window).Select(b => b.ToolWindowId));
    }
}
