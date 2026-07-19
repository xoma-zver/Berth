namespace Berth;

/// <summary>Registration descriptor of a tool window.</summary>
public sealed record ToolWindowDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    /// <param name="id">Stable identifier; must be non-empty.</param>
    /// <param name="title">Human-readable title, used for quick access sorting.</param>
    /// <param name="defaultSlot">Slot the window is placed into when no saved state exists.</param>
    /// <exception cref="ArgumentException">The id or title is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A member of <paramref name="defaultSlot"/> is outside its enum domain.</exception>
    public ToolWindowDescriptor(string id, string title, ToolWindowSlot defaultSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        EnumDomain.Require(defaultSlot, nameof(defaultSlot));
        Id = id;
        Title = title;
        DefaultSlot = defaultSlot;
    }

    /// <summary>Stable identifier (never empty).</summary>
    public string Id { get; }

    /// <summary>Human-readable title.</summary>
    public string Title { get; }

    /// <summary>
    /// Icon key interpreted by the materialization layer or the application; the core stores
    /// identifiers, not UI objects.
    /// </summary>
    public string? IconKey { get; init; }

    /// <summary>Default placement slot.</summary>
    public ToolWindowSlot DefaultSlot { get; init; }

    /// <summary>
    /// Default position within the slot; null means after the existing windows of the slot.
    /// </summary>
    public int? DefaultOrder { get; init; }

    /// <summary>Default presentation mode.</summary>
    public ToolWindowMode DefaultMode { get; init; } = ToolWindowMode.DockPinned;

    /// <summary>Default share of the side within a group pair.</summary>
    public double DefaultPairRatio { get; init; } = LayoutDefaults.PairRatio;

    /// <summary>When the body content is created.</summary>
    public ContentCreationPolicy CreationPolicy { get; init; } = ContentCreationPolicy.OnFirstOpen;

    /// <summary>How long the body content is retained.</summary>
    public ContentRetentionPolicy RetentionPolicy { get; init; } = ContentRetentionPolicy.KeepWhileRegistered;

    /// <summary>
    /// Factory of the tool window body content; null for a window whose content is materialized
    /// by the application shell outside the core-managed lifecycle.
    /// </summary>
    public IToolWindowContentFactory? ContentFactory { get; init; }

    /// <summary>
    /// Tab content factory claiming this window's tabs. Null claims nothing: previously
    /// sleeping tabs of this owner keep sleeping after registration.
    /// </summary>
    public ITabContentFactory? TabFactory { get; init; }
}
