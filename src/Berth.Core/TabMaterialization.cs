namespace Berth;

/// <summary>Outcome kind of <see cref="ContentLifecycle.MaterializeTab"/>.</summary>
public enum TabMaterializationKind
{
    /// <summary>The tab has live content — just created or created earlier.</summary>
    Materialized,

    /// <summary>The tab's owner has no live registration: the tab sleeps, nothing changed (spec DA-9.4); the UI shows a placeholder.</summary>
    Sleeping,

    /// <summary>The factory refused the tab: it was closed by a regular CloseTab (spec DA-9.3).</summary>
    Refused,
}

/// <summary>
/// Result of <see cref="ContentLifecycle.MaterializeTab"/>: the outcome kind, the live content
/// for a materialized tab, and the resulting layout. After a refusal the layout is the state
/// after the regular CloseTab (spec DA-9.3) — re-read node paths and any planned
/// materializations from it, as after every operation (spec DA-1.3); otherwise it is the input
/// state unchanged.
/// </summary>
public sealed class TabMaterialization
{
    internal TabMaterialization(TabMaterializationKind kind, object? content, LayoutState state)
    {
        Kind = kind;
        Content = content;
        State = state;
    }

    /// <summary>Outcome kind.</summary>
    public TabMaterializationKind Kind { get; }

    /// <summary>The live tab content; non-null exactly for <see cref="TabMaterializationKind.Materialized"/>.</summary>
    public object? Content { get; }

    /// <summary>The resulting layout; a new state only after a refusal.</summary>
    public LayoutState State { get; }
}
