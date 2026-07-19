namespace Berth;

/// <summary>
/// One document window of the dock area: own screen bounds plus a tab-group tree of the shared
/// model. A document window exists only while its tree holds at least one tab, so its current
/// tab is always defined.
/// </summary>
public sealed record DocumentWindowState
{
    /// <summary>Creates a document window state.</summary>
    /// <param name="bounds">Screen bounds of the window; pixels come from the UI, the core never invents them.</param>
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

    /// <summary>Screen bounds of the window; validated against actual screens on restore.</summary>
    public FloatingBounds Bounds { get; init; }

    /// <summary>Root of the window's tab-group tree — the same model as every host.</summary>
    public TabTreeNode Root { get; init; }

    /// <summary>Current tab of the window; defined for the window's whole lifetime.</summary>
    public string CurrentTabId { get; init; }
}
