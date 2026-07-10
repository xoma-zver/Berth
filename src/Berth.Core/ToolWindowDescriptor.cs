namespace Berth;

/// <summary>Registration descriptor of a tool window (spec TW-9.1).</summary>
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

    /// <summary>When the body content is created (spec TW-9.2).</summary>
    public ContentCreationPolicy CreationPolicy { get; init; } = ContentCreationPolicy.OnFirstOpen;

    /// <summary>How long the body content is retained (spec TW-9.2).</summary>
    public ContentRetentionPolicy RetentionPolicy { get; init; } = ContentRetentionPolicy.KeepWhileRegistered;

    /// <summary>
    /// Factory of the tool window body content; null for a window whose content is materialized
    /// by the application shell outside the core-managed lifecycle (spec TW-9.1, TW-9.3).
    /// </summary>
    public IToolWindowContentFactory? ContentFactory { get; init; }

    /// <summary>
    /// Tab content factory claiming this window's tabs (spec TW-9.11). Null claims nothing:
    /// previously sleeping tabs of this owner keep sleeping after registration (spec DA-9.4).
    /// </summary>
    public ITabContentFactory? TabFactory { get; init; }
}
