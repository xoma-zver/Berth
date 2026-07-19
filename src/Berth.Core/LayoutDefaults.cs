namespace Berth;

/// <summary>Default values of the layout model, in one place.</summary>
public static class LayoutDefaults
{
    /// <summary>Default side weight: fraction of the workspace a side occupies, shared by the docked layer and the Undock overlay.</summary>
    public const double SideWeight = 0.33; // the IDEA default (TW-2.5)

    /// <summary>Default pair preference of a tool window. The defaults form a consistent pair, so the derived pair ratio is 0.5.</summary>
    public const double PairRatio = 0.5;

    /// <summary>Default last internal mode.</summary>
    public const ToolWindowMode LastInternalMode = ToolWindowMode.DockPinned;
}
