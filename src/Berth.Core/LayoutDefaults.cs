namespace Berth;

/// <summary>Default values of the layout model, in one place (spec TW-2.5, TW-3.1, TW-3.3).</summary>
public static class LayoutDefaults
{
    /// <summary>Default side weight: fraction of the workspace a side occupies, shared by the docked layer and the Undock overlay (spec TW-2.5, TW-3.3; IDEA default).</summary>
    public const double SideWeight = 0.33;

    /// <summary>Default pair preference of a tool window (spec TW-2.5). Defaults form a consistent pair, so rule R1 yields 0.5.</summary>
    public const double PairRatio = 0.5;

    /// <summary>Default last internal mode (spec TW-3.1).</summary>
    public const ToolWindowMode LastInternalMode = ToolWindowMode.DockPinned;
}
