using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Leaf of a tab-group tree: an ordered list of tab ids plus the group's active tab
/// (spec DA-2.1, ADR-0005). The tree stores identifiers only; content belongs to factories
/// (ADR-0003).
/// </summary>
public sealed record TabGroupNode : TabTreeNode
{
    /// <summary>Empty group — the minimal tree of the main window and of a tool window (spec DA-2.3).</summary>
    public static TabGroupNode Empty { get; } = new();

    /// <summary>Ordered tab ids of the group.</summary>
    public ImmutableArray<string> Tabs { get; init; } = [];

    /// <summary>Active tab of the group — one of <see cref="Tabs"/>; null only when the group is empty (spec DA-2.1, N5).</summary>
    public string? ActiveTabId { get; init; }
}
