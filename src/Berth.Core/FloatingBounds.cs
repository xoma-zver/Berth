namespace Berth;

/// <summary>
/// Saved screen bounds of a <see cref="ToolWindowMode.Float"/>/<see cref="ToolWindowMode.Window"/>
/// tool window. Screen coordinates are persisted state, not layout geometry; their validation
/// against actual screens happens at the UI boundary.
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
    /// Guards a bounds argument of a core command: a non-finite component is a caller error —
    /// states produced by the core must stay serializable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is NaN or infinity.</exception>
    internal void ThrowIfNotFinite(string paramName)
    {
        // TW-5.9: NaN/∞ must never enter the state.
        if (!IsFinite)
        {
            throw new ArgumentOutOfRangeException(
                paramName, this, "Every bounds component must be a finite number.");
        }
    }
}
