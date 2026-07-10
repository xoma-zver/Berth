using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Berth.Controls;

/// <summary>
/// UI-layer size constants: pixels live at the UI boundary only (ADR-0002); minimum content
/// sizes are render-time clamps that never touch the state (spec TW-2.8).
/// </summary>
internal static class BerthMetrics
{
    /// <summary>Width of a stripe (the vertical icon bar).</summary>
    public const double StripeWidth = 36;

    /// <summary>Square size of a stripe icon button, margins excluded.</summary>
    public const double StripeButtonSize = 28;

    /// <summary>Minimum rendered extent of a tool window pane and of the dock area (spec TW-2.8).</summary>
    public const double MinPaneSize = 48;

    /// <summary>Height of a tool window decorator header.</summary>
    public const double HeaderHeight = 28;

    /// <summary>Thickness of a splitter separator between panes.</summary>
    public const double SplitterThickness = 4;
}

/// <summary>
/// Theme-agnostic brushes of the static skeleton: translucent grays legible on light and dark
/// backgrounds alike. Real theming is a later concern — nothing here is public.
/// </summary>
internal static class BerthBrushes
{
    /// <summary>Subtle background of panes, stripes and headers.</summary>
    public static readonly IBrush Pane = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    /// <summary>Separators, splitters and borders.</summary>
    public static readonly IBrush Separator = new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));

    /// <summary>Highlight of an open stripe icon (spec TW-6.4).</summary>
    public static readonly IBrush OpenIcon = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
}
