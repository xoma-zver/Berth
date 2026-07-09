namespace Berth;

/// <summary>
/// One document window of the dock area: own screen bounds plus a tab-group tree of the shared
/// model (spec DA-7.1, DA-2.5). A document window exists only while its tree holds at least one
/// tab (INV-D6), so its current tab is always defined (INV-D4).
/// </summary>
public sealed record DocumentWindowState
{
    /// <summary>Creates a document window state.</summary>
    /// <param name="bounds">Screen bounds of the window; pixels come from the UI, the core never invents them (ADR-0002).</param>
    /// <param name="root">Root of the window's tab-group tree.</param>
    /// <param name="currentTabId">Current tab of the window; must be non-empty.</param>
    /// <exception cref="ArgumentException">The current tab id is empty or whitespace.</exception>
    public DocumentWindowState(FloatingBounds bounds, TabTreeNode root, string currentTabId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTabId);
        Bounds = bounds;
        Root = root;
        CurrentTabId = currentTabId;
    }

    /// <summary>Screen bounds of the window; validated against actual screens on restore (spec DA-7.4).</summary>
    public FloatingBounds Bounds { get; init; }

    /// <summary>Root of the window's tab-group tree — the same model as every host (spec DA-1.1).</summary>
    public TabTreeNode Root { get; init; }

    /// <summary>Current tab of the window (spec DA-2.5); defined for the window's whole lifetime (INV-D6).</summary>
    public string CurrentTabId { get; init; }
}
