using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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

    /// <summary>
    /// Background of the whole workspace — the canvas behind the stripes, panes, splitters
    /// and the dock area. Default: transparent, the window background shows through; a theme
    /// painting an IDE-like uniform surface sets it to the pane color.
    /// </summary>
    public const string WorkspaceBackground = "BerthWorkspaceBackgroundBrush";

    // ---- size tokens (resource values are doubles — x:Double in XAML) ----

    /// <summary>Width of a stripe — the vertical icon bar (TW-1.1). Default: 36.</summary>
    public const string StripeWidth = "BerthStripeWidth";

    /// <summary>
    /// Square size of a stripe icon button, margins excluded; also sizes the stripe drop
    /// zones of the drag catalog (TW-5.17). Default: 28.
    /// </summary>
    public const string StripeButtonSize = "BerthStripeButtonSize";

    /// <summary>Height of the header row of a tool window decorator and of a document pseudo-window title bar. Default: 28.</summary>
    public const string HeaderHeight = "BerthHeaderHeight";

    /// <summary>Height of a tab strip — leaf chrome of a tab group (DA-9.6). Default: 28.</summary>
    public const string TabStripHeight = "BerthTabStripHeight";

    /// <summary>Thickness of a splitter separator between panes. Default: 4.</summary>
    public const string SplitterThickness = "BerthSplitterThickness";
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
    /// Templated chrome binds its own properties at <see cref="BindingPriority.Template"/>:
    /// the token stays the live default while a pseudo-class style of the application
    /// (StyleTrigger priority) overrides it — the selector-styling contract of styling.md.
    /// </summary>
    public static IDisposable BindBrush(
        Control target,
        AvaloniaProperty property,
        string key,
        IBrush fallback,
        BindingPriority priority = BindingPriority.LocalValue) =>
        target.Bind(property, target.GetResourceObservable(key, value => value as IBrush ?? fallback), priority);

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
    /// Binds a size property (Width, Height) to a size token with a fallback — the
    /// counterpart of <see cref="BindBrush"/> for the double-typed tokens. Layout follows the
    /// value live, like every token binding. An integer resource is honored too
    /// (see <see cref="AsSize"/>): «32» next to «32.0» is a natural slip, not an error.
    /// </summary>
    public static IDisposable BindSize(
        Control target,
        AvaloniaProperty property,
        string key,
        double fallback,
        BindingPriority priority = BindingPriority.LocalValue) =>
        target.Bind(property, target.GetResourceObservable(key, value => AsSize(value, fallback)), priority);

    /// <summary>
    /// One-shot size token resolution — for geometry computed per pass rather than bound to a
    /// property: the stripe drop zones of the drag catalog (TW-5.17). The anchor must be
    /// attached for overrides above it to be found. Honors an integer resource like
    /// <see cref="BindSize"/>.
    /// </summary>
    public static double Size(Control anchor, string key, double fallback) =>
        anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value)
            ? AsSize(value, fallback)
            : fallback;

    /// <summary>
    /// Coerces a size token resource to a double: a <see cref="double"/> as is, an
    /// <see cref="int"/> widened — «32» is the natural mistake next to «32.0» and clearly means
    /// 32 pixels, so it is accepted rather than silently dropped or thrown on (a throw inside a
    /// resource observable is worse than useless). Any other type — a genuinely wrong resource —
    /// falls back to the built-in default.
    /// </summary>
    private static double AsSize(object? value, double fallback) => value switch
    {
        double size => size,
        int size => size,
        _ => fallback,
    };

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
