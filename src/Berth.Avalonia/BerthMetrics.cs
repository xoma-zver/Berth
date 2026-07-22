using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Berth.Controls;

/// <summary>
/// UI-layer size constants: pixels live at the UI boundary only (ADR-0002); minimum content
/// sizes are render-time clamps that never touch the state (TW-2.8). The chrome sizes
/// (stripe, icon, header, tab strip, splitter) are the built-in defaults of the size tokens
/// (<see cref="BerthThemeKeys"/>) and apply only as fallbacks — like <see cref="BerthBrushes"/>;
/// the behavioural constants below them are not tokens by design.
/// </summary>
internal static class BerthMetrics
{
    /// <summary>Width of a stripe (the vertical icon bar). Default of <see cref="BerthThemeKeys.StripeWidth"/>.</summary>
    public const double StripeWidth = 36;

    /// <summary>Square size of a stripe icon button, margins excluded. Default of <see cref="BerthThemeKeys.StripeButtonSize"/>.</summary>
    public const double StripeButtonSize = 28;

    /// <summary>Minimum rendered extent of a tool window pane and of the dock area (TW-2.8).</summary>
    public const double MinPaneSize = 48;

    /// <summary>Height of a tool window decorator header. Default of <see cref="BerthThemeKeys.HeaderHeight"/>.</summary>
    public const double HeaderHeight = 28;

    /// <summary>Height of a dock tab strip — leaf chrome of a tab group (DA-9.6). Default of <see cref="BerthThemeKeys.TabStripHeight"/>.</summary>
    public const double TabStripHeight = 28;

    /// <summary>Thickness of a splitter separator between panes. Default of <see cref="BerthThemeKeys.SplitterThickness"/>.</summary>
    public const double SplitterThickness = 4;

    /// <summary>
    /// Pointer travel from the press point turning a press into a drag gesture (TW-5.17);
    /// below it the gesture stays a click. Logical pixels are DPI-independent — the analogue of
    /// the reference's scaled MouseDragHelper.DRAG_START_DEADZONE = 7.
    /// </summary>
    public const double DragStartThreshold = 7;

    /// <summary>Thickness of the insertion marker line over the active drop target (TW-5.17).</summary>
    public const double DropMarkerThickness = 2;

    /// <summary>
    /// Largest edge of the content miniature shown by the ghost outside every target (TW-5.17, DA-9.7) — the reference's drag thumbnail cap.
    /// </summary>
    public const double GhostMiniatureMaxSize = 220;

    /// <summary>
    /// Depth of an edge wedge of a tab group as a fraction of the group's extent (DA-9.7);
    /// = the reference's dragToSplitRatio 0.2.
    /// </summary>
    public const double SplitWedgeRatio = 0.2;

    /// <summary>
    /// Guard of drag-committed fractions: a fraction derived from rendered bounds is clamped
    /// into [Min, 1−Min] before entering a core command, which requires the open interval
    /// (0..1) (TW-5.9, INV-4). Fractions of any realistic drag pass through untouched —
    /// the render minimums (<see cref="MinPaneSize"/>) keep them far from the edges.
    /// </summary>
    public const double MinCommittedFraction = 0.01;

    /// <summary>Clamps a drag-committed fraction into the guard range of <see cref="MinCommittedFraction"/>.</summary>
    public static double ClampFraction(double fraction) =>
        Math.Clamp(fraction, MinCommittedFraction, 1 - MinCommittedFraction);
}

/// <summary>
/// Built-in defaults of the design tokens (<see cref="BerthThemeKeys"/>): theme-agnostic
/// translucent grays legible on light and dark backgrounds alike, plus the per-variant opaque
/// overlay surfaces. Controls never read these directly — they resolve the token resources
/// through <see cref="ThemeTokens"/>, and these values apply only as fallbacks when the
/// application defines no resource under the key.
/// </summary>
internal static class BerthBrushes
{
    /// <summary>Subtle background of panes, stripes and headers.</summary>
    public static readonly IBrush Pane = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    /// <summary>Separators, splitters and borders.</summary>
    public static readonly IBrush Separator = new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));

    /// <summary>Highlight of an open stripe icon (TW-6.4).</summary>
    public static readonly IBrush OpenIcon = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    /// <summary>Header accent of the active tool window — the theme-discretion indication of TW-6.4.</summary>
    public static readonly IBrush ActiveHeader = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    /// <summary>Insertion marker over the active drop target (TW-5.17) — stronger than <see cref="Separator"/> to read as a live cue.</summary>
    public static readonly IBrush DropMarker = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));

    /// <summary>Translucent area preview of a wedge or center drop target (DA-9.7) — the content stays legible underneath.</summary>
    public static readonly IBrush DropAreaPreview = new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0x80, 0x80, 0x80));

    /// <summary>Opaque backdrop of overlay windows in the light theme variant (TW-3.3: panels must not show through).</summary>
    public static readonly IBrush LightOverlaySurface = new ImmutableSolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

    /// <summary>Opaque backdrop of overlay windows in the dark theme variant (TW-3.3).</summary>
    public static readonly IBrush DarkOverlaySurface = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));

    /// <summary>Workspace canvas behind panes and the dock area: transparent by default — the window background shows through.</summary>
    public static readonly IBrush WorkspaceBackground = new ImmutableSolidColorBrush(Colors.Transparent);
}
