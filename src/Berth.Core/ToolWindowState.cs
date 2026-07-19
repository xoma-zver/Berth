namespace Berth;

/// <summary>Complete layout state of one tool window.</summary>
public sealed record ToolWindowState
{
    /// <summary>Creates a state with the given placement and defaults for everything else.</summary>
    /// <param name="id">Stable registration identifier; must be non-empty.</param>
    /// <param name="slot">Placement slot.</param>
    /// <param name="order">Position within the slot (and on the stripe); must be non-negative.</param>
    /// <exception cref="ArgumentException">The id is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The order is negative, or a member of <paramref name="slot"/> is outside its enum domain.</exception>
    public ToolWindowState(string id, ToolWindowSlot slot, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        EnumDomain.Require(slot, nameof(slot));
        Id = id;
        Slot = slot;
        Order = order;
    }

    /// <summary>Stable registration identifier (never empty).</summary>
    public string Id { get; }

    /// <summary>Placement slot: side and group.</summary>
    public ToolWindowSlot Slot { get; init; }

    /// <summary>Position within the slot and on the stripe segment; orders are dense 0..n−1 per slot.</summary>
    public int Order { get; init; }

    /// <summary>Presentation mode.</summary>
    public ToolWindowMode Mode { get; init; } = ToolWindowMode.DockPinned;

    /// <summary>
    /// Last internal mode — the target of returning from Float/Window. While <see cref="Mode"/>
    /// is internal the two are equal.
    /// </summary>
    public ToolWindowMode LastInternalMode { get; init; } = LayoutDefaults.LastInternalMode;

    /// <summary>Whether the tool window is open (shown), in any mode.</summary>
    public bool IsOpen { get; init; }

    /// <summary>Whether the stripe icon is present; an open window always shows its icon.</summary>
    public bool IsIconVisible { get; init; } = true;

    /// <summary>Own preferred share of the side within a group pair, in (0..1); the pair's effective ratio derives from both preferences (<see cref="LayoutState.GetPairRatio"/>).</summary>
    public double PairRatio { get; init; } = LayoutDefaults.PairRatio;

    /// <summary>Saved screen bounds for Float/Window modes, if any.</summary>
    public FloatingBounds? FloatingBounds { get; init; }

    /// <summary>
    /// Content tree of the tool window — tab groups of the shared tree model. The degenerate
    /// tree is one group holding the body tab, whose id equals the window's <see cref="Id"/>;
    /// an empty tree is legal. The tree may only hold tabs owned by this window or unclaimed,
    /// sleeping ones.
    /// </summary>
    public TabTreeNode ContentTree { get; init; } = TabGroupNode.Empty;
}
