using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// The quick access «⋯» list: all registered tool windows whose stripe icon is hidden, sorted
/// by title — ordinal, case-insensitive, so the order does not depend on the machine locale —
/// with ties broken by id. Every listed window is closed. Sleeping states are not listed:
/// without a registration there is no title to show. The UI hides the «⋯» button while the
/// list is empty.
/// </summary>
public static class QuickAccess
{
    /// <summary>Computes the quick access list for the current layout and registrations.</summary>
    /// <param name="state">Current layout.</param>
    /// <param name="registry">Live registrations supplying the titles.</param>
    public static ImmutableArray<ToolWindowDescriptor> List(LayoutState state, ToolWindowRegistry registry)
    {
        // TW-8.2: hidden-icon windows sorted by Title (ordinal), tie-break by Id.
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);

        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in state.ToolWindows)
        {
            if (!window.IsIconVisible)
            {
                hidden.Add(window.Id);
            }
        }

        return
        [
            .. registry.Descriptors
                .Where(d => hidden.Contains(d.Id))
                .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Id, StringComparer.Ordinal),
        ];
    }
}
