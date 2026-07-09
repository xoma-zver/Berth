namespace Berth;

/// <summary>
/// Reference to a tab group of a dock-area host (spec DA-1.3): a group is addressed either by
/// any tab it contains, or as the root group of a host — the latter is needed only to reach an
/// empty root group (spec DA-2.3). Groups have no identity beyond their content, so there are
/// no group ids by design.
/// </summary>
public readonly record struct DockGroupRef
{
    private readonly string? _tabId;
    private readonly DockHost _host;

    private DockGroupRef(string? tabId, DockHost host)
    {
        _tabId = tabId;
        _host = host;
    }

    /// <summary>The group containing the given tab (spec DA-1.3).</summary>
    /// <param name="tabId">Id of a tab of the addressed group.</param>
    /// <exception cref="ArgumentException">The tab id is empty or whitespace.</exception>
    public static DockGroupRef AtTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return new DockGroupRef(tabId, default);
    }

    /// <summary>
    /// The root group of the given host. Addressing a host whose root is a split this way is a
    /// caller error (spec DA-1.3): the reference exists for the empty root group.
    /// </summary>
    /// <param name="host">Host whose root group is addressed.</param>
    public static DockGroupRef HostRoot(DockHost host) => new(null, host);

    /// <summary>Tab addressing the group, or null for a host-root reference.</summary>
    public string? TabId => _tabId;

    /// <summary>Host of the addressed root group; meaningful only when <see cref="TabId"/> is null.</summary>
    public DockHost Host => _host;
}
