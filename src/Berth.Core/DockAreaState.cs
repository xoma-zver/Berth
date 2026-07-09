using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// State of the document area (spec DA-2.5): the main-window tree, the document windows in
/// creation order, per-host current tabs and the active dock host.
/// </summary>
public sealed record DockAreaState
{
    /// <summary>Empty dock area: an empty root group in the main window and no document windows.</summary>
    public static DockAreaState Empty { get; } = new();

    /// <summary>Tree of the main window's dock area; exists always, minimally an empty root group (spec DA-2.3, TW-2.1).</summary>
    public TabTreeNode Root { get; init; } = TabGroupNode.Empty;

    /// <summary>Current tab of the main window — host memory (spec DA-6.1); null exactly when the tree holds no tabs (INV-D4).</summary>
    public string? CurrentTabId { get; init; }

    /// <summary>Document windows in creation order (spec DA-2.5); z-order and focus are UI concerns.</summary>
    public ImmutableArray<DocumentWindowState> Windows { get; init; } = [];

    /// <summary>Active host of the dock area — the host whose tab was activated last (spec DA-2.5, DA-6.1).</summary>
    public DockHost ActiveDockHost { get; init; } = DockHost.MainWindow;
}
