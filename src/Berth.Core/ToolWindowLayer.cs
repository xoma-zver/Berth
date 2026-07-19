namespace Berth;

/// <summary>
/// Slot layer a mode belongs to. Each slot holds at most one open window per layer:
/// docked and overlay layers are independent, floating windows are unbounded.
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

/// <summary>Classification helpers for <see cref="ToolWindowMode"/>.</summary>
public static class ToolWindowModeExtensions
{
    /// <summary>
    /// Whether the mode is internal (lives inside the main window layout):
    /// <see cref="ToolWindowMode.DockPinned"/>, <see cref="ToolWindowMode.DockUnpinned"/>
    /// or <see cref="ToolWindowMode.Undock"/>.
    /// </summary>
    public static bool IsInternal(this ToolWindowMode mode) =>
        mode is ToolWindowMode.DockPinned or ToolWindowMode.DockUnpinned or ToolWindowMode.Undock;

    /// <summary>Slot layer the mode occupies when open.</summary>
    public static ToolWindowLayer GetLayer(this ToolWindowMode mode) => mode switch
    {
        ToolWindowMode.DockPinned or ToolWindowMode.DockUnpinned => ToolWindowLayer.Docked,
        ToolWindowMode.Undock => ToolWindowLayer.Overlay,
        _ => ToolWindowLayer.Floating,
    };

    /// <summary>
    /// Effective presentation mode under the platform capabilities: a stored
    /// <see cref="ToolWindowMode.Window"/> degrades to <see cref="ToolWindowMode.Float"/> when
    /// windowed hosting is unavailable, and further to <see cref="ToolWindowMode.Undock"/> when
    /// floating is unavailable too; internal modes never degrade. A pure function: the stored
    /// mode is never changed, so a layout carried back to a fuller platform restores the
    /// original behaviour. The degraded mode governs presentation only; behaviour such as
    /// auto-hiding follows the stored mode.
    /// </summary>
    /// <param name="mode">Stored presentation mode.</param>
    /// <param name="canFloat">Whether the platform hosts Float — a real owned window or an overlay pseudo-window.</param>
    /// <param name="canUseWindowed">Whether the platform hosts Window — an independent top-level window.</param>
    public static ToolWindowMode GetEffectiveMode(this ToolWindowMode mode, bool canFloat, bool canUseWindowed)
    {
        // TW-7.6: Window → Float → Undock; the capabilities come from the UI layer
        // (ADR-0002/0006 — the core knows nothing about platforms).
        if (mode == ToolWindowMode.Window && !canUseWindowed)
        {
            mode = ToolWindowMode.Float;
        }

        if (mode == ToolWindowMode.Float && !canFloat)
        {
            mode = ToolWindowMode.Undock;
        }

        return mode;
    }
}
