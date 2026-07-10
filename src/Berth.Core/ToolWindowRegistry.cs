using System.Diagnostics.CodeAnalysis;

namespace Berth;

/// <summary>
/// Registry of tool window descriptors and tab ownership claims (spec TW-9.1, TW-9.11).
/// Each tool window id is registered at most once (INV-1). <see cref="Register"/> and
/// <see cref="Unregister"/> are the low-level primitives for assembling the registry before a
/// layout exists; in a live session use <see cref="ContentLifecycle.Register"/> and
/// <see cref="ContentLifecycle.Unregister"/>, which atomically reconcile the layout state
/// (spec TW-10.3, TW-9.4). Single-threaded by design.
/// </summary>
public sealed class ToolWindowRegistry
{
    private readonly Dictionary<string, ToolWindowDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly List<ITabContentFactory> _dockContent = [];

    /// <summary>All registered descriptors, in registration order.</summary>
    public IReadOnlyCollection<ToolWindowDescriptor> Descriptors => _descriptors.Values;

    /// <summary>Registers a descriptor.</summary>
    /// <exception cref="ArgumentException">A descriptor with the same id is already registered.</exception>
    public void Register(ToolWindowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_descriptors.TryAdd(descriptor.Id, descriptor))
        {
            throw new ArgumentException($"Tool window '{descriptor.Id}' is already registered.", nameof(descriptor));
        }
    }

    /// <summary>
    /// Removes a registration; the tool window's saved state becomes sleeping (spec TW-9.4,
    /// TW-10.2). Low-level: in a live session use <see cref="ContentLifecycle.Unregister"/>,
    /// which also closes the window, releases content and closes the owner's tabs.
    /// </summary>
    /// <exception cref="ArgumentException">No descriptor with the given id is registered.</exception>
    public void Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_descriptors.Remove(id))
        {
            throw new ArgumentException($"No tool window '{id}' is registered.", nameof(id));
        }
    }

    /// <summary>Returns the descriptor registered under the given id, if any.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out ToolWindowDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _descriptors.TryGetValue(id, out descriptor);
    }

    /// <summary>
    /// Registers a dock-area content factory: its claimed tabs are documents (spec TW-9.11,
    /// DA-1). Several dock-area registrations are allowed as long as their claims do not
    /// overlap — an overlap is an application error detected at owner resolution. Low-level:
    /// in a live session use <see cref="ContentLifecycle.RegisterDockContent"/>, which also
    /// reconciles the layout state (spec TW-10.3, DA-9.2).
    /// </summary>
    public void RegisterDockContent(ITabContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _dockContent.Add(factory);
    }

    /// <summary>
    /// Resolves the owner of a tab id over the current claims (spec TW-9.7, TW-9.11): the dock
    /// area, a tool window, or null when no live registration claims the id — the tab sleeps
    /// (spec DA-9.4). A tool window's registration with a body factory implicitly claims the
    /// window's own id — its body tab (spec TW-9.5).
    /// </summary>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public TabOwner? ResolveTabOwner(string tabId)
    {
        var found = ResolveTabClaim(tabId, out var claim, out var conflict);
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }

        return found ? claim.Owner : null;
    }

    /// <summary>
    /// The claim resolution behind <see cref="ResolveTabOwner"/> (spec TW-9.11). Returns true
    /// with the single confirmed claim; false with a null <paramref name="conflictMessage"/>
    /// when nothing claims the id (a sleeping owner), and false with a message when several
    /// live registrations claim it — the caller decides whether to throw (operations,
    /// materialization) or to treat the owner as unconfirmed (Apply, invariant validation).
    /// Claims of one registration — the implicit body claim and its own tab predicate — unite
    /// into a single claim with the body bridge taking precedence (spec TW-9.5).
    /// </summary>
    internal bool ResolveTabClaim(string tabId, out TabClaim claim, out string? conflictMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        claim = default;
        conflictMessage = null;
        string? claimant = null;
        foreach (var dockFactory in _dockContent)
        {
            if (!dockFactory.OwnsTab(tabId))
            {
                continue;
            }

            if (claimant is not null)
            {
                claim = default;
                conflictMessage = ConflictMessage(tabId, claimant, "the dock area");
                return false;
            }

            claimant = "the dock area";
            claim = new TabClaim(TabOwner.DockArea, dockFactory, bodyFactory: null);
        }

        foreach (var descriptor in _descriptors.Values)
        {
            var isBody = descriptor.ContentFactory is not null
                && string.Equals(descriptor.Id, tabId, StringComparison.Ordinal);
            if (!isBody && !(descriptor.TabFactory is { } tabFactory && tabFactory.OwnsTab(tabId)))
            {
                continue;
            }

            if (claimant is not null)
            {
                claim = default;
                conflictMessage = ConflictMessage(tabId, claimant, $"tool window '{descriptor.Id}'");
                return false;
            }

            claimant = $"tool window '{descriptor.Id}'";
            claim = isBody
                ? new TabClaim(TabOwner.ToolWindow(descriptor.Id), tabFactory: null, descriptor.ContentFactory)
                : new TabClaim(TabOwner.ToolWindow(descriptor.Id), descriptor.TabFactory, bodyFactory: null);
        }

        return claimant is not null;
    }

    private static string ConflictMessage(string tabId, string first, string second) =>
        $"Tab '{tabId}' is claimed by both {first} and {second}; " +
        "ownership must be unique across live registrations (spec TW-9.11).";
}
