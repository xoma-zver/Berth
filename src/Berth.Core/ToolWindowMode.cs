using System.Diagnostics.CodeAnalysis;

namespace Berth;

/// <summary>Presentation mode of a tool window.</summary>
public enum ToolWindowMode
{
    /// <summary>Docked, takes layout space, stays open on focus loss.</summary>
    DockPinned,

    /// <summary>Docked, takes layout space, auto-hides on focus loss.</summary>
    DockUnpinned,

    /// <summary>Overlay over the workspace, auto-hides on focus loss.</summary>
    Undock,

    /// <summary>Separate window owned by the main window, always above it.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Float' is the spec term for the mode (TW-3.2).")]
    Float,

    /// <summary>Independent top-level window.</summary>
    Window,
}
