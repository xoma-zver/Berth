namespace Berth;

/// <summary>Geometry of one workspace side, in fractions (spec TW-2.5).</summary>
/// <param name="Weight">
/// Fraction of the workspace the side occupies (width for Left/Right, height for Bottom);
/// shared by the docked layer and the Undock overlay (spec TW-3.3). The effective share of
/// the Primary content within an open pair is not stored — it derives from the pair's
/// preferences (rule R1, <see cref="LayoutState.GetPairRatio"/>).
/// </param>
public sealed record SideState(double Weight = LayoutDefaults.SideWeight);
