namespace Berth;

/// <summary>
/// Result of <see cref="LayoutCompositionBuilder.Build"/>: the assembled registry, the content
/// coordinator wired to it, and the initial layout state — the three inputs a workspace needs.
/// <see cref="State"/> is immutable and therefore doubles as the snapshot of the application's
/// defaults (spec TW-5.14): keep it to reset the layout later without losing open documents —
/// <c>current.Apply(composition.State, ApplyScope.Arrangement, composition.Registry)</c>
/// restores the default placement and openness while the dock area stays untouched (TW-10.6).
/// </summary>
public sealed class LayoutComposition
{
    internal LayoutComposition(ToolWindowRegistry registry, ContentLifecycle lifecycle, LayoutState state)
    {
        Registry = registry;
        Lifecycle = lifecycle;
        State = state;
    }

    /// <summary>The registry holding every registered descriptor and dock content claim.</summary>
    public ToolWindowRegistry Registry { get; }

    /// <summary>The content coordinator over <see cref="Registry"/>; eager content is already created (spec TW-9.2).</summary>
    public ContentLifecycle Lifecycle { get; }

    /// <summary>
    /// The initial layout: the default arrangement of the registered descriptors — the same
    /// rule as <see cref="LayoutApply.ResetToDefaults"/> (spec TW-10.3) — with the builder's
    /// initial commands applied on top: opened tool windows, documents and panel tabs.
    /// </summary>
    public LayoutState State { get; }
}
