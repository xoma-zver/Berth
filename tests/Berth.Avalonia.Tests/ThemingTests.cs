using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Berth.Controls.Tests;

using static WorkspaceTestSupport;

/// <summary>
/// The design tokens and the styling contract: chrome brushes resolve through the public
/// <see cref="BerthThemeKeys"/> over the standard resource lookup — an application override
/// wins at any level, values follow runtime resource changes and theme variant switches, and
/// without any override the built-in defaults apply. The pseudo-classes complete the
/// contract: <c>:open</c> on stripe icons (TW-6.4; <c>:active</c>/<c>:current</c> of tab
/// headers are pinned by DA_6_2 in DockAreaLayoutTests).
/// </summary>
public class ThemingTests
{
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Colors.Red);
    private static readonly IBrush Green = new ImmutableSolidColorBrush(Colors.Green);
    private static readonly IBrush Blue = new ImmutableSolidColorBrush(Colors.Blue);

    /// <summary>Shows a workspace in a window configured (resources, variant) before the show.</summary>
    private static Window ShowConfigured(
        LayoutState state, ToolWindowRegistry registry, Action<Window> configure)
    {
        var window = new Window { Width = 800, Height = 600 };
        configure(window);
        window.Content = new BerthWorkspace { State = state, Registry = registry };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static LayoutState OpenPanelState() => LayoutState.Empty with
    {
        ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
    };

    [AvaloniaFact]
    public void Tokens_default_to_the_built_in_brushes_without_any_override()
    {
        var window = Show(OpenPanelState(), Registry("a"));

        var header = (Border)Part(window, "PART_Header");
        Assert.Same(BerthBrushes.Pane, header.Background);
        Assert.Same(BerthBrushes.Separator, header.BorderBrush);
    }

    [AvaloniaFact]
    public void A_token_resource_override_restyles_the_chrome()
    {
        var window = ShowConfigured(OpenPanelState(), Registry("a"), w =>
        {
            w.Resources[BerthThemeKeys.Pane] = Red;
            w.Resources[BerthThemeKeys.Separator] = Green;
        });

        var header = (Border)Part(window, "PART_Header");
        Assert.Same(Red, header.Background);
        Assert.Same(Green, header.BorderBrush);
        Assert.Same(Green, ((GridSplitter)Part(window, "PART_LeftSideSplitter")).Background);
    }

    [AvaloniaFact]
    public void A_runtime_token_change_updates_the_live_chrome()
    {
        var window = Show(OpenPanelState(), Registry("a"));
        var header = (Border)Part(window, "PART_Header");
        Assert.Same(BerthBrushes.Pane, header.Background);

        // The DynamicResource semantics of the token bindings: a resource assigned at
        // runtime restyles already materialized chrome without a re-projection.
        window.Resources[BerthThemeKeys.Pane] = Blue;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(Blue, header.Background);
    }

    [AvaloniaFact]
    public void The_overlay_surface_token_follows_the_theme_variant()
    {
        var light = new ImmutableSolidColorBrush(Colors.White);
        var dark = new ImmutableSolidColorBrush(Colors.Black);
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
        };
        var window = ShowConfigured(state, Registry("u"), w =>
        {
            w.RequestedThemeVariant = ThemeVariant.Light;
            var tokens = new ResourceDictionary();
            tokens.ThemeDictionaries[ThemeVariant.Light] =
                new ResourceDictionary { [BerthThemeKeys.OverlaySurface] = light };
            tokens.ThemeDictionaries[ThemeVariant.Dark] =
                new ResourceDictionary { [BerthThemeKeys.OverlaySurface] = dark };
            w.Resources.MergedDictionaries.Add(tokens);
        });

        var backdrop = (Border)Part(window, "PART_OverlayBackdrop");
        Assert.Same(light, backdrop.Background);

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(dark, backdrop.Background);
    }

    [AvaloniaFact]
    public void The_overlay_surface_default_follows_the_theme_variant_without_an_override()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.Undock,
                    LastInternalMode = ToolWindowMode.Undock,
                },
            ],
        };
        var window = ShowConfigured(state, Registry("u"), w => w.RequestedThemeVariant = ThemeVariant.Light);

        var backdrop = (Border)Part(window, "PART_OverlayBackdrop");
        Assert.Same(BerthBrushes.LightOverlaySurface, backdrop.Background);

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(BerthBrushes.DarkOverlaySurface, backdrop.Background);
    }

    [AvaloniaFact]
    public void TW_6_4_an_open_stripe_icon_carries_the_open_pseudo_class()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, order: 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));

        Assert.Contains(":open", Button(window, "a").Classes);
        Assert.DoesNotContain(":open", Button(window, "b").Classes);
    }
}
