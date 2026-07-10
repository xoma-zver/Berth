namespace Berth;

/// <summary>
/// Reference to a tab group of a tree host (spec DA-1.3): a group is addressed either by any
/// tab it contains, or as the root group of a host — the latter is needed only to reach an
/// empty root group (spec DA-2.3). Dock-area hosts are addressed by <see cref="DockHost"/>,
/// tool window trees — by the window's id (spec TW-9.5). Groups have no identity beyond their
/// content, so there are no group ids by design.
/// </summary>
public readonly record struct DockGroupRef
{
    private readonly string? _tabId;
    private readonly DockHost _host;
    private readonly string? _panelId;

    private DockGroupRef(string? tabId, DockHost host, string? panelId)
    {
        _tabId = tabId;
        _host = host;
        _panelId = panelId;
    }

    /// <summary>The group containing the given tab (spec DA-1.3).</summary>
    /// <param name="tabId">Id of a tab of the addressed group.</param>
    /// <exception cref="ArgumentException">The tab id is empty or whitespace.</exception>
    public static DockGroupRef AtTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return new DockGroupRef(tabId, default, panelId: null);
    }

    /// <summary>
    /// The root group of the given dock-area host. Addressing a host whose root is a split this
    /// way is a caller error (spec DA-1.3): the reference exists for the empty root group.
    /// </summary>
    /// <param name="host">Dock-area host whose root group is addressed.</param>
    public static DockGroupRef HostRoot(DockHost host) => new(null, host, panelId: null);

    /// <summary>
    /// The root group of the given tool window's content tree (spec DA-1.3, TW-9.5). Addressing
    /// a panel whose root is a split this way is a caller error, as with <see cref="HostRoot"/>.
    /// </summary>
    /// <param name="toolWindowId">Id of the tool window hosting the tree.</param>
    /// <exception cref="ArgumentException">The id is empty or whitespace.</exception>
    public static DockGroupRef PanelRoot(string toolWindowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolWindowId);
        return new DockGroupRef(tabId: null, default, toolWindowId);
    }

    /// <summary>Tab addressing the group, or null for a host-root reference.</summary>
    public string? TabId => _tabId;

    /// <summary>Dock-area host of the addressed root group; meaningful only when <see cref="TabId"/> and <see cref="PanelId"/> are null.</summary>
    public DockHost Host => _host;

    /// <summary>Id of the tool window whose root group is addressed, or null for other reference kinds.</summary>
    public string? PanelId => _panelId;
}
