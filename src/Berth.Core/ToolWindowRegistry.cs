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
    /// overlap — an overlap is an application error detected at owner resolution.
    /// </summary>
    public void RegisterDockContent(ITabContentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _dockContent.Add(factory);
    }

    /// <summary>
    /// Resolves the owner of a tab id over the current claims (spec TW-9.7, TW-9.11): the dock
    /// area, a tool window, or null when no live registration claims the id — the tab sleeps
    /// (spec DA-9.4).
    /// </summary>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    public TabOwner? ResolveTabOwner(string tabId) =>
        TryResolveTabClaim(tabId, out _, out var owner) ? owner : null;

    /// <summary>The claim resolution behind <see cref="ResolveTabOwner"/>, exposing the claiming factory.</summary>
    /// <exception cref="InvalidOperationException">Two live registrations claim the id (spec TW-9.11).</exception>
    internal bool TryResolveTabClaim(
        string tabId, [NotNullWhen(true)] out ITabContentFactory? factory, out TabOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        factory = null;
        owner = default;
        string? claimant = null;
        foreach (var dockFactory in _dockContent)
        {
            if (dockFactory.OwnsTab(tabId))
            {
                ThrowIfClaimed(tabId, claimant, "the dock area");
                factory = dockFactory;
                owner = TabOwner.DockArea;
                claimant = "the dock area";
            }
        }

        foreach (var descriptor in _descriptors.Values)
        {
            if (descriptor.TabFactory is { } tabFactory && tabFactory.OwnsTab(tabId))
            {
                ThrowIfClaimed(tabId, claimant, $"tool window '{descriptor.Id}'");
                factory = tabFactory;
                owner = TabOwner.ToolWindow(descriptor.Id);
                claimant = $"tool window '{descriptor.Id}'";
            }
        }

        return factory is not null;
    }

    private static void ThrowIfClaimed(string tabId, string? claimant, string next)
    {
        if (claimant is not null)
        {
            throw new InvalidOperationException(
                $"Tab '{tabId}' is claimed by both {claimant} and {next}; " +
                "ownership must be unique across live registrations (spec TW-9.11).");
        }
    }
}
