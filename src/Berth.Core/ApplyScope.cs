namespace Berth;

/// <summary>
/// Scope of <see cref="LayoutApply.Apply"/> (spec TW-5.14, TW-10.6).
/// </summary>
public enum ApplyScope
{
    /// <summary>
    /// The whole state: tool window placement, side geometry, the dock area with all its trees
    /// and document windows. Used for session save and restore (spec TW-10.6).
    /// </summary>
    Full,

    /// <summary>
    /// Placement only: slots, orders, modes, openness and icons of the mentioned tool windows,
    /// side geometry and the quick access side — without content trees and open tabs. Used by
    /// named layouts; the dock area is not touched at all (spec TW-10.6, TW-10.7, DA-9.1).
    /// </summary>
    Arrangement,
}
