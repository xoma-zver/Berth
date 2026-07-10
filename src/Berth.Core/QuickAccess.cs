using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// The quick access «⋯» list (spec TW-8.2): all registered tool windows whose stripe icon is
/// hidden, sorted by title — ordinal, case-insensitive, so the order does not depend on the
/// machine locale — with ties broken by id. Every listed window is closed (INV-6). Sleeping
/// states are not listed: without a registration there is no title to show (ADR-0003). The UI
/// hides the «⋯» button while the list is empty (spec TW-8.4).
/// </summary>
public static class QuickAccess
{
    /// <summary>Computes the quick access list for the current layout and registrations (spec TW-8.2).</summary>
    /// <param name="state">Current layout.</param>
    /// <param name="registry">Live registrations supplying the titles.</param>
    public static ImmutableArray<ToolWindowDescriptor> List(LayoutState state, ToolWindowRegistry registry)
    {
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
