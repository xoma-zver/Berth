namespace Berth;

/// <summary>
/// Slot layer a mode belongs to. Each slot holds at most one open window per layer:
/// docked and overlay layers are independent, floating windows are unbounded (INV-2).
/// </summary>
public enum ToolWindowLayer
{
    /// <summary>Docked layer: <see cref="ToolWindowMode.DockPinned"/> and <see cref="ToolWindowMode.DockUnpinned"/>.</summary>
    Docked,

    /// <summary>Overlay layer: <see cref="ToolWindowMode.Undock"/>.</summary>
    Overlay,

    /// <summary>Floating windows: <see cref="ToolWindowMode.Float"/> and <see cref="ToolWindowMode.Window"/>.</summary>
    Floating,
}

/// <summary>Classification helpers for <see cref="ToolWindowMode"/> (spec TW-3.2, INV-2, INV-7).</summary>
public static class ToolWindowModeExtensions
{
    /// <summary>
    /// Whether the mode is internal (lives inside the main window layout):
    /// <see cref="ToolWindowMode.DockPinned"/>, <see cref="ToolWindowMode.DockUnpinned"/>
    /// or <see cref="ToolWindowMode.Undock"/>.
    /// </summary>
    public static bool IsInternal(this ToolWindowMode mode) =>
        mode is ToolWindowMode.DockPinned or ToolWindowMode.DockUnpinned or ToolWindowMode.Undock;

    /// <summary>Slot layer the mode occupies when open (INV-2).</summary>
    public static ToolWindowLayer GetLayer(this ToolWindowMode mode) => mode switch
    {
        ToolWindowMode.DockPinned or ToolWindowMode.DockUnpinned => ToolWindowLayer.Docked,
        ToolWindowMode.Undock => ToolWindowLayer.Overlay,
        _ => ToolWindowLayer.Floating,
    };
}
