namespace Berth;

/// <summary>
/// A confirmed ownership claim of a tab id (spec TW-9.11): the owner plus the factory that
/// materializes the tab — <see cref="TabFactory"/> for a tab claimed by predicate, or
/// <see cref="BodyFactory"/> for the body tab of a tool window (the factory bridge of TW-9.5;
/// the bridge takes precedence over the same registration's own predicate). Exactly one of the
/// two factories is set.
/// </summary>
internal readonly struct TabClaim
{
    public TabClaim(TabOwner owner, ITabContentFactory? tabFactory, IToolWindowContentFactory? bodyFactory)
    {
        Owner = owner;
        TabFactory = tabFactory;
        BodyFactory = bodyFactory;
    }

    /// <summary>Owner confirmed by the claim.</summary>
    public TabOwner Owner { get; }

    /// <summary>Factory of a predicate-claimed tab, or null for a body claim.</summary>
    public ITabContentFactory? TabFactory { get; }

    /// <summary>Body factory of the owning tool window, set exactly for the body claim (spec TW-9.5).</summary>
    public IToolWindowContentFactory? BodyFactory { get; }

    /// <summary>Whether the claim is the implicit body claim of a tool window (spec TW-9.5, TW-9.11).</summary>
    public bool IsBody => BodyFactory is not null;
}
