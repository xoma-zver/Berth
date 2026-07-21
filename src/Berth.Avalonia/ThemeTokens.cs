using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Berth.Controls;

/// <summary>
/// Resource keys of the Berth design tokens — the customization surface of the built-in
/// appearance. Every brush the controls draw with is resolved through one of these keys over
/// the standard Avalonia resource lookup (the logical tree up to Application resources, theme
/// dictionaries included), so an application restyles Berth by defining resources under these
/// keys — at any level, in ThemeDictionaries for per-variant values — with no theme include
/// required: a key without a resource falls back to the built-in default. Values are resolved
/// dynamically: runtime resource changes and theme variant switches are picked up live.
/// </summary>
public static class BerthThemeKeys
{
    /// <summary>Background of chrome surfaces: stripes, tool window headers, tab strips. Default: translucent gray.</summary>
    public const string Pane = "BerthPaneBrush";

    /// <summary>Separators, splitters and borders. Default: translucent gray.</summary>
    public const string Separator = "BerthSeparatorBrush";

    /// <summary>Highlight of an open stripe icon and of the active tab of a group. Default: translucent gray.</summary>
    public const string OpenIcon = "BerthOpenIconBrush";

    /// <summary>Accent of the active tool window's header and of the current document's tab header. Default: translucent gray.</summary>
    public const string ActiveHeader = "BerthActiveHeaderBrush";

    /// <summary>Insertion marker of drag-and-drop targets. Default: strong translucent gray.</summary>
    public const string DropMarker = "BerthDropMarkerBrush";

    /// <summary>Translucent area preview of drop targets and of the strip insertion placeholder.</summary>
    public const string DropAreaPreview = "BerthDropAreaPreviewBrush";

    /// <summary>
    /// Opaque backdrop of overlay surfaces — Undock overlays, pseudo-windows, drag chips:
    /// panels underneath must not show through, so the value must stay opaque. The built-in
    /// default follows the theme variant (light and dark surfaces); an override is typically
    /// defined per variant in ThemeDictionaries.
    /// </summary>
    public const string OverlaySurface = "BerthOverlaySurfaceBrush";
}

/// <summary>
/// Consumption side of the design tokens (<see cref="BerthThemeKeys"/>): binds control
/// properties to token resources with the built-in defaults of <see cref="BerthBrushes"/> as
/// fallbacks. Bindings ride on the resource observable — the code-behind equivalent of
/// DynamicResource: they follow logical-tree attachment, runtime resource changes and, for the
/// variant-dependent surface, theme variant switches.
/// </summary>
internal static class ThemeTokens
{
    /// <summary>
    /// Binds a brush property to a token resource with a fallback: the resource value wins
    /// when a resource under the key is reachable from the control, else the fallback applies.
    /// The returned disposable removes the binding — needed only when a persistent control
    /// re-binds the same property conditionally; leaf chrome lets bindings die with the control.
    /// </summary>
    public static IDisposable BindBrush(Control target, AvaloniaProperty property, string key, IBrush fallback) =>
        target.Bind(property, target.GetResourceObservable(key, value => value as IBrush ?? fallback));

    /// <summary>
    /// One-shot token resolution against the current tree and theme variant — for transient
    /// gesture visuals assigned at show time (drop markers), where a binding would outlive the
    /// shown value. The anchor must be attached for overrides above it to be found.
    /// </summary>
    public static IBrush Brush(Control anchor, string key, IBrush fallback) =>
        anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;

    /// <summary>
    /// Binds an opaque overlay surface background (<see cref="BerthThemeKeys.OverlaySurface"/>)
    /// whose built-in fallback follows the theme variant. The binding is re-established on
    /// every variant change: the fallback is variant-dependent, and the re-bind also re-reads
    /// ThemeDictionaries overrides regardless of how the platform propagates variant changes
    /// through resource observables.
    /// </summary>
    public static void BindSurface(Border target)
    {
        IDisposable? binding = null;
        target.ActualThemeVariantChanged += (_, _) => Apply();
        Apply();

        void Apply()
        {
            binding?.Dispose();
            var fallback = target.ActualThemeVariant == ThemeVariant.Dark
                ? BerthBrushes.DarkOverlaySurface
                : BerthBrushes.LightOverlaySurface;
            binding = BindBrush(target, Border.BackgroundProperty, BerthThemeKeys.OverlaySurface, fallback);
        }
    }
}
