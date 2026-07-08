namespace Berth;

/// <summary>Half of a side occupied by a tool window (spec TW-1.1).</summary>
public enum ToolWindowGroup
{
    /// <summary>Primary group: top part of a side panel, left part of the bottom panel.</summary>
    Primary,

    /// <summary>Secondary group: bottom part of a side panel, right part of the bottom panel.</summary>
    Secondary,
}
