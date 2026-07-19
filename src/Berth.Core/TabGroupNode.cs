using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Leaf of a tab-group tree: an ordered list of tab ids plus the group's active tab.
/// The tree stores identifiers only; content belongs to factories.
/// </summary>
public sealed record TabGroupNode : TabTreeNode
{
    /// <summary>Empty group — the minimal tree of the main window and of a tool window.</summary>
    public static TabGroupNode Empty { get; } = new();

    /// <summary>Ordered tab ids of the group.</summary>
    public ImmutableArray<string> Tabs { get; init; } = [];

    /// <summary>Active tab of the group — one of <see cref="Tabs"/>; null only when the group is empty.</summary>
    public string? ActiveTabId { get; init; }
}
