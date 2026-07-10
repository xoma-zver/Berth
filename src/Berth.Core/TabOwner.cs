namespace Berth;

/// <summary>
/// Owner of a tab as resolved by the registry (spec TW-9.7, TW-9.11, DA-1): the dock area —
/// such a tab is a document — or a specific tool window. The default value is the dock area.
/// </summary>
public readonly record struct TabOwner
{
    private readonly string? _toolWindowId;

    private TabOwner(string? toolWindowId) => _toolWindowId = toolWindowId;

    /// <summary>The dock-area owner: the tab is a document.</summary>
    public static TabOwner DockArea => default;

    /// <summary>The tool window owning the tab.</summary>
    /// <param name="toolWindowId">Id of the owning tool window.</param>
    /// <exception cref="ArgumentException">The id is empty or whitespace.</exception>
    public static TabOwner ToolWindow(string toolWindowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolWindowId);
        return new TabOwner(toolWindowId);
    }

    /// <summary>Id of the owning tool window, or null for the dock area.</summary>
    public string? ToolWindowId => _toolWindowId;

    /// <summary>Whether the owner is the dock area.</summary>
    public bool IsDockArea => _toolWindowId is null;
}
