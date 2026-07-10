namespace Berth;

/// <summary>
/// Factory of the body content of a tool window (spec TW-9.1, TW-9.2; ADR-0003). Creates and
/// releases the opaque content object of the panel as a whole — the slot layer treats the panel
/// as an atom (TW-9.6) and the core never inspects the object. The degenerate content tree's
/// single tab — the body tab, id equal to the window's id — represents the panel body, and its
/// materialization delegates here (the factory bridge of TW-9.5); additional and movable tabs
/// go through <see cref="ITabContentFactory"/>.
/// </summary>
public interface IToolWindowContentFactory
{
    /// <summary>Creates the content of the tool window; the moment is governed by <see cref="ToolWindowDescriptor.CreationPolicy"/> (spec TW-9.2).</summary>
    /// <param name="toolWindowId">Id of the tool window being materialized.</param>
    public object CreateContent(string toolWindowId);

    /// <summary>Releases previously created content; called per <see cref="ToolWindowDescriptor.RetentionPolicy"/> and on unregistration under any policy (spec TW-9.2, TW-9.4).</summary>
    /// <param name="toolWindowId">Id of the tool window.</param>
    /// <param name="content">The exact object returned by <see cref="CreateContent"/>, passed back once.</param>
    public void ReleaseContent(string toolWindowId, object content);
}
