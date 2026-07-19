namespace Berth;

/// <summary>
/// Internal address of one tab-tree host: a dock-area host — the main window or a document
/// window — or a tool window's content tree. The dock-area subset is the public
/// <see cref="DockHost"/>; the public API addresses panels by their id
/// (<see cref="DockGroupRef.PanelRoot"/>), so this union stays internal.
/// </summary>
internal readonly record struct TreeHost
{
    private TreeHost(DockHost dock, string? panelId)
    {
        Dock = dock;
        PanelId = panelId;
    }

    /// <summary>Wraps a dock-area host.</summary>
    public static TreeHost OfDock(DockHost host) => new(host, panelId: null);

    /// <summary>The content tree of the given tool window.</summary>
    public static TreeHost Panel(string toolWindowId) => new(default, toolWindowId);

    /// <summary>The dock-area host; meaningful only when <see cref="IsPanel"/> is false.</summary>
    public DockHost Dock { get; }

    /// <summary>Id of the tool window hosting the tree, or null for a dock-area host.</summary>
    public string? PanelId { get; }

    /// <summary>Whether the host is a tool window's content tree.</summary>
    public bool IsPanel => PanelId is not null;
}
