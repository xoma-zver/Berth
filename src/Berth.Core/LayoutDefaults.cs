namespace Berth;

/// <summary>Default values of the layout model, in one place (spec TW-2.5, TW-3.1, TW-3.3).</summary>
public static class LayoutDefaults
{
    /// <summary>Default side weight: fraction of the workspace a side occupies (spec TW-2.5, IDEA default).</summary>
    public const double SideWeight = 0.33;

    /// <summary>Default effective share of the Primary content when both groups of a side are open (spec TW-2.5).</summary>
    public const double CurrentRatio = 0.5;

    /// <summary>Default share of a tool window within its side pair (spec TW-2.5).</summary>
    public const double PairRatio = 0.5;

    /// <summary>Default thickness of an Undock overlay: the default side weight (spec TW-3.3).</summary>
    public const double UndockWeight = SideWeight;

    /// <summary>Default last internal mode (spec TW-3.1).</summary>
    public const ToolWindowMode LastInternalMode = ToolWindowMode.DockPinned;
}
