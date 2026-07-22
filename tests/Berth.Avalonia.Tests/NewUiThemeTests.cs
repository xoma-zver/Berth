using Avalonia;
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

    private static Border Underline(Visual header) =>
        header.GetVisualDescendants().OfType<Border>().First(b =>
            string.Equals(b.Name, "UnderlineBar", StringComparison.Ordinal));

    [AvaloniaFact]
    public void Document_tabs_speak_the_editor_underline_not_a_fill()
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

        // d1 is the current document (:active + :current) — the focused blue underline;
        // d3 is merely selected in its group (:active) — the dimmed underline; d2 shows
        // none. Selection is never a fill: every document tab keeps the transparent
        // template background (§7 of the reference).
        var current = Underline(TabHeader(window, "d1"));
        Assert.True(current.IsVisible);
        Assert.Equal(Color.Parse("#3574F0"), ColorOf(current.Background));
        var active = Underline(TabHeader(window, "d3"));
        Assert.True(active.IsVisible);
        Assert.Equal(Color.Parse("#A8ADBD"), ColorOf(active.Background));
        Assert.False(Underline(TabHeader(window, "d2")).IsVisible);
        Assert.Equal(Colors.Transparent, ColorOf(((DockTabHeader)TabHeader(window, "d1")).Background));
        Assert.Equal(Colors.Transparent, ColorOf(((DockTabHeader)TabHeader(window, "d3")).Background));
    }

    [AvaloniaFact]
    public void Panel_tabs_keep_the_selected_fill_without_an_underline()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    ContentTree = new TabGroupNode { Tabs = ["p1", "p2"], ActiveTabId = "p1" },
                },
            ],
        };
        var window = ShowThemed(state, Registry("a"), ThemeVariant.Light);

        // Tool window tabs keep the reference fill language of the header tabs (§3):
        // the selected one is filled, no underline shows on either.
        Assert.Equal(
            Color.Parse("#D0D4D8"), ColorOf(((DockTabHeader)TabHeader(window, "p1")).Background));
        Assert.Equal(Colors.Transparent, ColorOf(((DockTabHeader)TabHeader(window, "p2")).Background));
        Assert.False(Underline(TabHeader(window, "p1")).IsVisible);
        Assert.False(Underline(TabHeader(window, "p2")).IsVisible);
    }

    [AvaloniaFact]
    public void The_workspace_canvas_takes_the_uniform_surface()
    {
        var window = ShowThemed(TwoOpenPanels(), Registry("a", "b"), ThemeVariant.Light);

        // The IDE-look canvas (§6): one background under the whole workspace, so nothing
        // dark bleeds through between panes and behind the dock area.
        var workspace = window.GetVisualDescendants().OfType<BerthWorkspace>().Single();
        var canvas = (Panel)workspace.Child!;
        Assert.Equal(LightPane, ColorOf(canvas.Background));

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DarkPane, ColorOf(canvas.Background));
    }

    [AvaloniaFact]
    public void The_reference_chrome_sizes_apply()
    {
        var window = ShowThemed(TwoOpenPanels(), Registry("a", "b"), ThemeVariant.Light);

        // Splitters: the 1 px hairline of the reference look — the grab stays narrow until
        // the split visual/grab entity exists (owner decision, 2026-07-22); header 41; the
        // stripe border adds its 1 px edge to the 40 px content column; the icon face is
        // the 30 px visible highlight of the reference.
        Assert.Equal(1.0, Part(window, "PART_LeftSideSplitter").Bounds.Width);
        var headerRow = (Control)((Border)Part(Decorator(window, "a"), "PART_Header")).Child!;
        Assert.Equal(41.0, headerRow.Bounds.Height);
        Assert.Equal(41.0, Part(window, "PART_LeftStripe").Bounds.Width);
        var face = Button(window, "a").GetVisualDescendants().OfType<StripeIconFace>().Single();
        Assert.Equal(30.0, face.Bounds.Width);
    }
}
