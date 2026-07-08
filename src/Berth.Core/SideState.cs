namespace Berth;

/// <summary>Geometry of one workspace side, in fractions (spec TW-2.5).</summary>
/// <param name="Weight">Fraction of the workspace the side occupies (width for Left/Right, height for Bottom).</param>
/// <param name="CurrentRatio">Effective share of the Primary content when both groups are open.</param>
public sealed record SideState(
    double Weight = LayoutDefaults.SideWeight,
    double CurrentRatio = LayoutDefaults.CurrentRatio);
