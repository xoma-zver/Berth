namespace Berth;

/// <summary>
/// Saved screen bounds of a <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/>
/// tool window (spec TW-3.1). Screen coordinates are persisted state, not layout geometry;
/// their validation against actual screens happens at the UI boundary (spec TW-7.4).
/// </summary>
/// <param name="X">Left edge in screen coordinates.</param>
/// <param name="Y">Top edge in screen coordinates.</param>
/// <param name="Width">Window width.</param>
/// <param name="Height">Window height.</param>
public readonly record struct FloatingBounds(double X, double Y, double Width, double Height)
{
    /// <summary>Whether every component is a finite number — neither NaN nor infinity.</summary>
    internal bool IsFinite =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);

    /// <summary>
    /// Guards a bounds argument of a core command (spec TW-5.9): a non-finite component is a
    /// caller error — states produced by the core must stay serializable (TW-10.1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is NaN or infinity.</exception>
    internal void ThrowIfNotFinite(string paramName)
    {
        if (!IsFinite)
        {
            throw new ArgumentOutOfRangeException(
                paramName, this, "Every bounds component must be a finite number.");
        }
    }
}
