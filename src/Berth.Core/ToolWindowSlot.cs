using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// One of the six fixed placements for tool windows: a (side, group) pair (spec TW-1.1).
/// </summary>
/// <param name="Side">Side of the workspace.</param>
/// <param name="Group">Half of the side.</param>
public readonly record struct ToolWindowSlot(ToolWindowSide Side, ToolWindowGroup Group)
{
    /// <summary>All six slots, in (side, group) order (spec TW-1.1).</summary>
    public static ImmutableArray<ToolWindowSlot> All { get; } =
    [
        new(ToolWindowSide.Left, ToolWindowGroup.Primary),
        new(ToolWindowSide.Left, ToolWindowGroup.Secondary),
        new(ToolWindowSide.Right, ToolWindowGroup.Primary),
        new(ToolWindowSide.Right, ToolWindowGroup.Secondary),
        new(ToolWindowSide.Bottom, ToolWindowGroup.Primary),
        new(ToolWindowSide.Bottom, ToolWindowGroup.Secondary),
    ];
}
