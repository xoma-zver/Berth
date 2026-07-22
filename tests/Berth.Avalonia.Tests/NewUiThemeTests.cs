using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth.Themes.NewUi;
using Xunit;

namespace Berth.Controls.Tests;

using static WorkspaceTestSupport;

/// <summary>
/// The ready-made New UI theme package (<see cref="BerthNewUiTheme"/>): including the style
/// set restyles the workspace to the reference palette per theme variant, places the
/// active-panel accent on the stripe icon (the solid fill of the reference) while
/// neutralizing the header accent, fills the selected tab headers, and applies the
/// reference chrome sizes — all over the public styling contract (docs/styling.md), with
/// no template replaced.
/// </summary>
public class NewUiThemeTests
{
    private static readonly Color LightPane = Color.Parse("#F7F8FA");
    private static readonly Color DarkPane = Color.Parse("#2B2D30");
    private static readonly Color StripeActive = Color.Parse("#3574F0");

    private static Window ShowThemed(LayoutState state, ToolWindowRegistry registry, ThemeVariant variant)
    {
        var window = new Window { Width = 800, Height = 600, RequestedThemeVariant = variant };
        window.Styles.Add(new BerthNewUiTheme());
        window.Content = new BerthWorkspace { State = state, Registry = registry };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Color ColorOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    private static LayoutState TwoOpenPanels() => LayoutState.Empty with
    {
        ToolWindows =
        [
            Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
            Win("b", ToolWindowSide.Right, ToolWindowGroup.Primary) with { IsOpen = true },
        ],
        ActiveToolWindowId = "a",
    };

    [AvaloniaFact]
    public void Tokens_resolve_per_theme_variant_and_follow_a_live_switch()
    {
        var window = ShowThemed(TwoOpenPanels(), Registry("a", "b"), ThemeVariant.Light);

        var header = (Border)Part(Decorator(window, "b"), "PART_Header");
        Assert.Equal(LightPane, ColorOf(header.Background));
        Assert.Equal(Color.Parse("#EBECF0"), ColorOf(header.BorderBrush));

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DarkPane, ColorOf(header.Background));
        Assert.Equal(Color.Parse("#1E1F22"), ColorOf(header.BorderBrush));
    }

    [AvaloniaFact]
    public void The_active_panel_accent_lives_on_the_stripe_icon_not_the_header()
    {
        var window = ShowThemed(TwoOpenPanels(), Registry("a", "b"), ThemeVariant.Light);

        // The reference accent: the active panel's icon is a solid fill, one hex in both
        // variants; the open-but-inactive icon keeps the translucent token highlight.
        Assert.Equal(StripeActive, ColorOf(Button(window, "a").Background));
        Assert.Equal(Color.Parse("#1D000000"), ColorOf(Button(window, "b").Background));

        // The header accent is neutralized: the active header equals the inactive one —
        // in the reference the colored header is dead old-UI code.
        var active = (Border)Part(Decorator(window, "a"), "PART_Header");
        var inactive = (Border)Part(Decorator(window, "b"), "PART_Header");
        Assert.Equal(LightPane, ColorOf(active.Background));
        Assert.Equal(ColorOf(inactive.Background), ColorOf(active.Background));

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StripeActive, ColorOf(Button(window, "a").Background));
        Assert.Equal(DarkPane, ColorOf(active.Background));
    }

    [AvaloniaFact]
    public void The_selected_tab_headers_take_the_selected_fill()
    {
        var state = LayoutState.Empty with
        {
            DockArea = new DockAreaState
            {
                Root = new SplitNode
                {
                    Orientation = SplitOrientation.Row,
                    Children =
                    [
                        new SplitChild(new TabGroupNode { Tabs = ["d1", "d2"], ActiveTabId = "d1" }, 0.5),
                        new SplitChild(new TabGroupNode { Tabs = ["d3"], ActiveTabId = "d3" }, 0.5),
                    ],
                },
                CurrentTabId = "d1",
            },
        };
        var window = ShowThemed(state, Registry(), ThemeVariant.Light);

        // d1 is the current document (:active + :current), d3 is active in its group only
        // (:active) — both take the selected fill; d2 keeps the transparent template value.
        var selected = Color.Parse("#D0D4D8");
        Assert.Equal(selected, ColorOf(((DockTabHeader)TabHeader(window, "d1")).Background));
        Assert.Equal(selected, ColorOf(((DockTabHeader)TabHeader(window, "d3")).Background));
        Assert.Equal(Colors.Transparent, ColorOf(((DockTabHeader)TabHeader(window, "d2")).Background));
    }

    [AvaloniaFact]
    public void The_reference_chrome_sizes_apply()
    {
        var window = ShowThemed(TwoOpenPanels(), Registry("a", "b"), ThemeVariant.Light);

        // Splitters: 6 px — the reference grab zone over Berth's one-entity splitter
        // (owner decision); header 41; the stripe border adds its 1 px edge to the 40 px
        // content column; the icon face is the 30 px visible highlight of the reference.
        Assert.Equal(6.0, Part(window, "PART_LeftSideSplitter").Bounds.Width);
        var headerRow = (Control)((Border)Part(Decorator(window, "a"), "PART_Header")).Child!;
        Assert.Equal(41.0, headerRow.Bounds.Height);
        Assert.Equal(41.0, Part(window, "PART_LeftStripe").Bounds.Width);
        var face = Button(window, "a").GetVisualDescendants().OfType<StripeIconFace>().Single();
        Assert.Equal(30.0, face.Bounds.Width);
    }
}
