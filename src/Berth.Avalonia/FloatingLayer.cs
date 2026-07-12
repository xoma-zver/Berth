namespace Berth.Controls;

/// <summary>
/// Hosting seam of the floating layer (ADR-0006): the desktop materializes floating tool
/// windows and document windows as real OS windows (<see cref="FloatingWindowLayer"/>,
/// spec TW-7.1, TW-7.2, DA-7.3), the browser — as pseudo-windows in the workspace overlay
/// (<see cref="OverlayWindowLayer"/>, TW-7.7, DA-7.5). The workspace picks the implementation
/// from its TopLevel and drives both identically: one reconciliation pass per projection and a
/// command-free teardown when the workspace goes away (TW-7.5, DA-7.6).
/// </summary>
internal interface IFloatingLayer
{
    /// <summary>
    /// Whether the layer hosts real OS windows: independent top-levels exist
    /// (<see cref="BerthWorkspace.CanUseWindowed"/>, TW-7.6) and saved bounds live in screen
    /// coordinates; a pseudo-window layer works in workspace coordinates instead (TW-7.7).
    /// </summary>
    public bool IsWindowed { get; }

    /// <summary>The reconciliation pass, run from the workspace projection (spec TW-9.13, DA-9.6).</summary>
    public void Update(LayoutState state, ToolWindowRegistry registry);

    /// <summary>Closes every floating window without commands: the state keeps them open for the next session (TW-7.5, DA-7.6). Idempotent.</summary>
    public void Teardown();
}
