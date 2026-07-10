namespace Berth;

/// <summary>
/// Factory of tab content with an ownership claim (spec TW-9.11; ADR-0003). A registration —
/// dock-area content via <see cref="ToolWindowRegistry.RegisterDockContent"/> or a tool window
/// via <see cref="ToolWindowDescriptor.TabFactory"/> — claims tab ids by predicate; the owner of
/// an id is the single claiming live registration (spec TW-9.7, DA-1). A claim by two live
/// registrations is an application error detected when the owner of a concrete id is resolved.
/// </summary>
public interface ITabContentFactory
{
    /// <summary>
    /// The ownership claim (spec TW-9.11). Must be pure and stable while the registration lives;
    /// the granularity of tab ids is the application's choice (spec DA-2.4).
    /// </summary>
    /// <param name="tabId">Id of the tab whose ownership is being resolved.</param>
    public bool OwnsTab(string tabId);

    /// <summary>
    /// Creates the content of a tab, or refuses with null — the tab is then closed by a regular
    /// CloseTab, uniformly for restored and freshly opened tabs (spec DA-9.3).
    /// </summary>
    /// <param name="tabId">Id of the tab being materialized.</param>
    public object? CreateContent(string tabId);

    /// <summary>
    /// Releases previously created tab content. Called when the tab id leaves the layout — never
    /// on a move between groups, hosts or windows (spec DA-5.4: a move is not close-plus-open).
    /// </summary>
    /// <param name="tabId">Id of the tab.</param>
    /// <param name="content">The exact object returned by <see cref="CreateContent"/>, passed back once.</param>
    public void ReleaseContent(string tabId, object content);
}
