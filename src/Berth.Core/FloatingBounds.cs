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
public readonly record struct FloatingBounds(double X, double Y, double Width, double Height);
