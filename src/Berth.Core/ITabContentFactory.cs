namespace Berth;

/// <summary>
/// Factory of tab content with an ownership claim. A registration — dock-area content via
/// <see cref="ToolWindowRegistry.RegisterDockContent"/> or a tool window via
/// <see cref="ToolWindowDescriptor.TabFactory"/> — claims tab ids by predicate; the owner of
/// an id is the single claiming live registration. A claim by two live registrations is an
/// application error detected when the owner of a concrete id is resolved.
/// </summary>
public interface ITabContentFactory
{
    /// <summary>
    /// The ownership claim. Must be pure and stable while the registration lives; the
    /// granularity of tab ids is the application's choice.
    /// </summary>
    /// <param name="tabId">Id of the tab whose ownership is being resolved.</param>
    public bool OwnsTab(string tabId);

    /// <summary>
    /// Creates the content of a tab, or refuses with null — the tab is then closed by a regular
    /// CloseTab, uniformly for restored and freshly opened tabs.
    /// </summary>
    /// <param name="tabId">Id of the tab being materialized.</param>
    public object? CreateContent(string tabId);

    /// <summary>
    /// Releases previously created tab content. Called when the tab id leaves the layout —
    /// never on a move between groups, hosts or windows: a move is not close-plus-open.
    /// </summary>
    /// <param name="tabId">Id of the tab.</param>
    /// <param name="content">The exact object returned by <see cref="CreateContent"/>, passed back once.</param>
    public void ReleaseContent(string tabId, object content);
}
