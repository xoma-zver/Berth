using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Immutable state of the whole layout: per-window states plus layout-level side geometry,
/// quick access side, the active window (spec TW-3.1) and the dock area (spec DA-2.5).
/// </summary>
public sealed record LayoutState
{
    /// <summary>Empty layout: no tool windows, default side geometry, an empty dock area.</summary>
    public static LayoutState Empty { get; } = new();

    /// <summary>States of all tool windows, including sleeping ones (spec TW-10.2). Ids are unique (INV-1).</summary>
    public ImmutableArray<ToolWindowState> ToolWindows { get; init; } = [];

    /// <summary>Geometry of the left side.</summary>
    public SideState Left { get; init; } = new();

    /// <summary>Geometry of the right side.</summary>
    public SideState Right { get; init; } = new();

    /// <summary>Geometry of the bottom side.</summary>
    public SideState Bottom { get; init; } = new();

    /// <summary>Stripe hosting the quick access «⋯» button (spec TW-8.1).</summary>
    public QuickAccessSide QuickAccessSide { get; init; } = QuickAccessSide.Left;

    /// <summary>Id of the active tool window, or null when a document is active (INV-5, spec TW-6.5).</summary>
    public string? ActiveToolWindowId { get; init; }

    /// <summary>State of the document area: host trees, document windows, per-host current tabs and the active dock host (spec DA-2.5).</summary>
    public DockAreaState DockArea { get; init; } = DockAreaState.Empty;

    /// <summary>Geometry of the given side.</summary>
    public SideState GetSide(ToolWindowSide side) => side switch
    {
        ToolWindowSide.Left => Left,
        ToolWindowSide.Right => Right,
        ToolWindowSide.Bottom => Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, message: null),
    };
}
