using System.Diagnostics.CodeAnalysis;

namespace Berth;

/// <summary>
/// Registry of tool window descriptors. Each id is registered at most once (INV-1).
/// Unregistration and sleeping-state semantics arrive with the content lifecycle
/// and persistence tasks (spec TW-9.4, TW-10.2).
/// </summary>
public sealed class ToolWindowRegistry
{
    private readonly Dictionary<string, ToolWindowDescriptor> _descriptors = new(StringComparer.Ordinal);

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

    /// <summary>Returns the descriptor registered under the given id, if any.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out ToolWindowDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _descriptors.TryGetValue(id, out descriptor);
    }
}
