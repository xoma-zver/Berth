namespace Berth;

/// <summary>Complete layout state of one tool window (spec TW-3.1).</summary>
public sealed record ToolWindowState
{
    /// <summary>Creates a state with the given placement and defaults for everything else.</summary>
    /// <param name="id">Stable registration identifier; must be non-empty.</param>
    /// <param name="slot">Placement slot.</param>
    /// <param name="order">Position within the slot (and on the stripe); must be non-negative.</param>
    /// <exception cref="ArgumentException">The id is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The order is negative.</exception>
    public ToolWindowState(string id, ToolWindowSlot slot, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        Id = id;
        Slot = slot;
        Order = order;
    }

    /// <summary>Stable registration identifier (never empty).</summary>
    public string Id { get; }

    /// <summary>Placement slot: side and group (spec TW-1.1).</summary>
    public ToolWindowSlot Slot { get; init; }

    /// <summary>Position within the slot and on the stripe segment (INV-3: dense 0..n−1 per slot).</summary>
    public int Order { get; init; }

    /// <summary>Presentation mode (spec TW-3.2).</summary>
    public ToolWindowMode Mode { get; init; } = ToolWindowMode.DockPinned;

    /// <summary>
    /// Last internal mode — the target of returning from Float/Window (spec TW-5.6).
    /// Bound to <see cref="Mode"/> by INV-7.
    /// </summary>
    public ToolWindowMode LastInternalMode { get; init; } = LayoutDefaults.LastInternalMode;

    /// <summary>Whether the tool window is open (shown), in any mode.</summary>
    public bool IsOpen { get; init; }

    /// <summary>Whether the stripe icon is present (spec TW-5.10; INV-6: open implies visible icon).</summary>
    public bool IsIconVisible { get; init; } = true;

    /// <summary>Own preferred share of the side within a group pair, in (0..1) (spec TW-2.5); the pair's effective ratio derives from both preferences (rule R1, <see cref="LayoutState.GetPairRatio"/>).</summary>
    public double PairRatio { get; init; } = LayoutDefaults.PairRatio;

    /// <summary>Saved screen bounds for Float/Window modes, if any (spec TW-3.1).</summary>
    public FloatingBounds? FloatingBounds { get; init; }

    /// <summary>
    /// Content tree of the tool window — tab groups of the shared tree model (spec TW-9.5,
    /// DA-1.1). The degenerate tree is one group holding the body tab, whose id equals the
    /// window's <see cref="Id"/> (TW-9.5); an empty tree is legal (DA-8.4). The tree may only
    /// hold tabs owned by this window or unclaimed, sleeping ones (INV-D5).
    /// </summary>
    public TabTreeNode ContentTree { get; init; } = TabGroupNode.Empty;
}
