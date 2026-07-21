using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The ControlTheme contract of the templated leaf chrome (docs/styling.md): the built-in
/// themes apply without any include, an application ControlTheme resource keyed by the
/// control type replaces the template wholesale, and a pseudo-class selector style restyles
/// the highlight — the token binding rides at Template priority, below StyleTrigger — while
/// without a style the highlight stays the live token default. Templating is legal for leaf
/// chrome only (TW-9.13); the persistent hosts keep their untemplated LocalValue contract
/// (ThemingTests).
/// </summary>
public class ControlThemeTests
{
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Colors.Red);
    private static readonly IBrush Green = new ImmutableSolidColorBrush(Colors.Green);
    private static readonly IBrush Blue = new ImmutableSolidColorBrush(Colors.Blue);

    /// <summary>An open panel plus a two-group dock split: every templated chrome kind materializes.</summary>
    private static LayoutState ChromeState() => LayoutState.Empty with
    {
        ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
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

    private static Window ShowConfigured(LayoutState state, Action<Window> configure)
    {
        var window = new Window { Width = 800, Height = 600 };
        configure(window);
        window.Content = new BerthWorkspace { State = state, Registry = Registry("a") };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void The_built_in_themes_apply_without_any_include()
    {
        var window = Show(ChromeState(), Registry("a"));

        // Each templated chrome control materialized its built-in template: the stripe icon
        // face, the tab close part, the splitter bar and the chrome button presenter.
        Assert.NotEmpty(Button(window, "a").GetVisualDescendants().OfType<StripeIconFace>());
        Assert.NotNull(TryPart(TabHeader(window, "d1"), "PART_TabClose"));
        Assert.NotEmpty(Part(window, "PART_LeftSideSplitter").GetVisualDescendants().OfType<Border>());
        Assert.NotEmpty(Part(window, "PART_HideButton").GetVisualDescendants().OfType<ContentPresenter>());
    }

    [AvaloniaFact]
    public void An_application_control_theme_replaces_each_template()
    {
        var window = ShowConfigured(ChromeState(), w =>
        {
            w.Resources[typeof(StripeButton)] = MarkerTheme<StripeButton>("CustomStripeFace");
            w.Resources[typeof(DockTabHeader)] = MarkerTheme<DockTabHeader>("CustomTabFace");
            w.Resources[typeof(BerthSplitter)] = MarkerTheme<BerthSplitter>("CustomSplitterFace");
            w.Resources[typeof(BerthChromeButton)] = MarkerTheme<BerthChromeButton>("CustomChromeFace");
        });

        // The application theme wins over the built-in fallback for every chrome kind.
        Assert.NotNull(TryPart(Button(window, "a"), "CustomStripeFace"));
        Assert.NotNull(TryPart(TabHeader(window, "d1"), "CustomTabFace"));
        Assert.NotNull(TryPart(Part(window, "PART_LeftSideSplitter"), "CustomSplitterFace"));
        Assert.NotNull(TryPart(Part(window, "PART_MenuButton"), "CustomChromeFace"));
    }

    [AvaloniaFact]
    public void A_custom_tab_header_template_without_the_close_part_is_legal()
    {
        // PART_TabClose is optional (docs/styling.md): the template applies, no throw — the
        // middle click and the context menu keep the close reachable.
        var window = ShowConfigured(ChromeState(), w =>
            w.Resources[typeof(DockTabHeader)] = MarkerTheme<DockTabHeader>("BareTabFace"));

        Assert.NotNull(TryPart(TabHeader(window, "d1"), "BareTabFace"));
        Assert.Null(TryPart(TabHeader(window, "d1"), "PART_TabClose"));
    }

    [AvaloniaFact]
    public void A_pseudo_class_style_recolors_the_open_stripe_highlight()
    {
        // The promised payoff of the templated chrome: StripeButton:open { Background }
        // now works — the style (StyleTrigger) overrides the token binding (Template).
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = new Window { Width = 800, Height = 600 };
        window.Styles.Add(new Style(x => x.OfType<StripeButton>().Class(":open"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Red) },
        });
        window.Content = new BerthWorkspace { State = state, Registry = Registry("a", "b") };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(Red, Button(window, "a").Background);
        // The closed icon stays on the transparent template value — the style does not match.
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)Button(window, "b").Background!).Color);
    }

    [AvaloniaFact]
    public void Pseudo_class_styles_recolor_the_tab_header_highlights()
    {
        var window = new Window { Width = 800, Height = 600 };
        // Both classes ride on the current document's header; equal-specificity styles
        // resolve by order, so :current comes after :active to win on it.
        window.Styles.Add(new Style(x => x.OfType<DockTabHeader>().Class(":active"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Green) },
        });
        window.Styles.Add(new Style(x => x.OfType<DockTabHeader>().Class(":current"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Red) },
        });
        window.Content = new BerthWorkspace { State = ChromeState(), Registry = Registry("a") };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // d1 is the current document (:current); d3 is active in its group but not current
        // (:active); d2 is neither.
        Assert.Same(Red, ((DockTabHeader)TabHeader(window, "d1")).Background);
        Assert.Same(Green, ((DockTabHeader)TabHeader(window, "d3")).Background);
        Assert.Equal(
            Colors.Transparent, ((ISolidColorBrush)((DockTabHeader)TabHeader(window, "d2")).Background!).Color);
    }

    [AvaloniaFact]
    public void The_highlights_default_to_the_tokens_and_follow_a_runtime_change()
    {
        // Without styles the highlight is the live token at Template priority: the built-in
        // default applies, and a runtime resource change restyles already built chrome.
        var window = Show(ChromeState(), Registry("a"));

        Assert.Same(BerthBrushes.OpenIcon, Button(window, "a").Background);
        Assert.Same(BerthBrushes.ActiveHeader, ((DockTabHeader)TabHeader(window, "d1")).Background);
        Assert.Same(BerthBrushes.OpenIcon, ((DockTabHeader)TabHeader(window, "d3")).Background);

        window.Resources[BerthThemeKeys.OpenIcon] = Blue;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(Blue, Button(window, "a").Background);
        Assert.Same(Blue, ((DockTabHeader)TabHeader(window, "d3")).Background);
    }

    private static ControlTheme MarkerTheme<T>(string markerName)
        where T : TemplatedControl
        => new(typeof(T))
        {
            Setters =
            {
                new Setter(
                    TemplatedControl.TemplateProperty,
                    new FuncControlTemplate<T>((_, _) => new Border { Name = markerName })),
            },
        };
}
