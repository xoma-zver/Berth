namespace Berth;

/// <summary>
/// Registration descriptor of a tool window (spec TW-9.1). Content lifecycle policy and
/// the content factory are added by the content lifecycle contracts (backlog task 1.7).
/// </summary>
public sealed record ToolWindowDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    /// <param name="id">Stable identifier; must be non-empty.</param>
    /// <param name="title">Human-readable title, used for quick access sorting (spec TW-8.2).</param>
    /// <param name="defaultSlot">Slot the window is placed into when no saved state exists (spec TW-10.3).</param>
    /// <exception cref="ArgumentException">The id or title is empty or whitespace.</exception>
    public ToolWindowDescriptor(string id, string title, ToolWindowSlot defaultSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        Title = title;
        DefaultSlot = defaultSlot;
    }

    /// <summary>Stable identifier (never empty).</summary>
    public string Id { get; }

    /// <summary>Human-readable title.</summary>
    public string Title { get; }

    /// <summary>
    /// Icon key interpreted by the materialization layer or the application
    /// (ADR-0003: the core stores identifiers, not UI objects).
    /// </summary>
    public string? IconKey { get; init; }

    /// <summary>Default placement slot.</summary>
    public ToolWindowSlot DefaultSlot { get; init; }

    /// <summary>
    /// Default position within the slot; null means after the existing windows of the slot (spec TW-10.3).
    /// </summary>
    public int? DefaultOrder { get; init; }

    /// <summary>Default presentation mode.</summary>
    public ToolWindowMode DefaultMode { get; init; } = ToolWindowMode.DockPinned;

    /// <summary>Default share of the side within a group pair (spec TW-2.5).</summary>
    public double DefaultPairRatio { get; init; } = LayoutDefaults.PairRatio;
}
