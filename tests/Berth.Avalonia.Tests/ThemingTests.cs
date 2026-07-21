using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    // ---- size tokens ----

    [AvaloniaFact]
    public void Size_tokens_default_to_the_built_in_metrics()
    {
        var window = Show(OpenPanelState(), Registry("a"));

        Assert.Equal(
            BerthMetrics.SplitterThickness, Part(window, "PART_LeftSideSplitter").Bounds.Width);
        var headerRow = (Control)((Border)Part(window, "PART_Header")).Child!;
        Assert.Equal(BerthMetrics.HeaderHeight, headerRow.Bounds.Height);
    }

    [AvaloniaFact]
    public void A_size_token_override_resizes_the_chrome()
    {
        var window = ShowConfigured(OpenPanelState(), Registry("a"), w =>
        {
            w.Resources[BerthThemeKeys.SplitterThickness] = 8.0;
            w.Resources[BerthThemeKeys.HeaderHeight] = 40.0;
            w.Resources[BerthThemeKeys.StripeWidth] = 60.0;
        });

        Assert.Equal(8.0, Part(window, "PART_LeftSideSplitter").Bounds.Width);
        var headerRow = (Control)((Border)Part(window, "PART_Header")).Child!;
        Assert.Equal(40.0, headerRow.Bounds.Height);
        // The stripe border adds its 1px edge around the 60px content column.
        Assert.Equal(61.0, Part(window, "PART_LeftStripe").Bounds.Width);
    }

    [AvaloniaFact]
    public void A_size_token_honors_an_integer_resource()
    {
        // «32» next to «32.0» is a natural slip: an int resource is coerced, not dropped.
        var window = ShowConfigured(OpenPanelState(), Registry("a"), w =>
            w.Resources[BerthThemeKeys.HeaderHeight] = 40); // int, not 40.0

        var headerRow = (Control)((Border)Part(window, "PART_Header")).Child!;
        Assert.Equal(40.0, headerRow.Bounds.Height);
    }

    [AvaloniaFact]
    public void A_runtime_size_change_relayouts_the_live_chrome()
    {
        var window = Show(OpenPanelState(), Registry("a"));
        var splitter = Part(window, "PART_LeftSideSplitter");
        Assert.Equal(BerthMetrics.SplitterThickness, splitter.Bounds.Width);

        window.Resources[BerthThemeKeys.SplitterThickness] = 10.0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10.0, splitter.Bounds.Width);
    }

    [AvaloniaFact]
    public void A_size_token_drives_the_stripe_drop_zone_geometry()
    {
        // The stripe drop marker is button-sized (TW-5.17): the catalog resolves the size
        // token one-shot per build (ThemeTokens.Size) — a distinct path from the bindings.
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = ShowConfigured(state, Registry("a"), w => w.Resources[BerthThemeKeys.StripeButtonSize] = 44.0);

        var start = Center(Button(window, "a"), window);
        var stripe = BoundsIn(Part(window, "PART_RightStripe"), window);
        window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(stripe.Center.X, stripe.Top + 5));
        Dispatcher.UIThread.RunJobs();

        var marker = (Border)Part(window, "PART_DropMarker");
        Assert.True(marker.IsVisible);
        Assert.Equal(44.0, marker.Height); // the position fill is one button tall

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(new Point(stripe.Center.X, stripe.Top + 5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_transient_drop_marker_resolves_its_token_at_show_time()
    {
        // The drop marker is a transient gesture visual: its brush alternates per show
        // (area vs. line), so it resolves the token one-shot through ThemeTokens.Brush
        // (TryFindResource) rather than a binding — a distinct path from the bound tokens.
        // A stripe zone paints the area marker with DropAreaPreview; DropMarker (the line)
        // rides the identical one-shot mechanism, only the key differs.
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = ShowConfigured(state, Registry("a"), w => w.Resources[BerthThemeKeys.DropAreaPreview] = Red);

        // Drag the icon to the empty Right.Primary stripe zone — the area marker shows there.
        var start = Center(Button(window, "a"), window);
        var stripe = BoundsIn(Part(window, "PART_RightStripe"), window);
        window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(stripe.Center.X, stripe.Top + 5));
        Dispatcher.UIThread.RunJobs();

        var marker = (Border)Part(window, "PART_DropMarker");
        Assert.True(marker.IsVisible);
        Assert.Same(Red, marker.Background); // the override is honored at show time

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(new Point(stripe.Center.X, stripe.Top + 5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_transient_drop_marker_defaults_to_the_built_in_brush()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, Registry("a"));

        var start = Center(Button(window, "a"), window);
        var stripe = BoundsIn(Part(window, "PART_RightStripe"), window);
        window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(stripe.Center.X, stripe.Top + 5));
        Dispatcher.UIThread.RunJobs();

        var marker = (Border)Part(window, "PART_DropMarker");
        Assert.True(marker.IsVisible);
        Assert.Same(BerthBrushes.DropAreaPreview, marker.Background);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(new Point(stripe.Center.X, stripe.Top + 5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
