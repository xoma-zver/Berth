using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Validation of the layout invariants INV-1…INV-7 (spec section 4).
/// Checked after every operation; a violation is a core bug, not a user error.
/// </summary>
public static class LayoutInvariants
{
    /// <summary>
    /// Validates the layout against the invariants. Returns an empty array for a valid layout.
    /// Sleeping states — ids without a registered descriptor — are legal (spec TW-10.2).
    /// </summary>
    public static ImmutableArray<InvariantViolation> Validate(LayoutState state, ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);

        var violations = ImmutableArray.CreateBuilder<InvariantViolation>();
        CheckStatesUnique(state, violations);
        CheckRegisteredHaveState(state, registry, violations);
        CheckSlotLayers(state, violations);
        CheckOrderDensity(state, violations);
        CheckFractions(state, violations);
        CheckActiveToolWindow(state, violations);
        CheckIconVisibility(state, violations);
        CheckLastInternalMode(state, violations);
        return violations.ToImmutable();
    }

    /// <summary>INV-1: each tool window has exactly one state.</summary>
    private static void CheckStatesUnique(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in state.ToolWindows)
        {
            if (!seen.Add(window.Id))
            {
                violations.Add(new InvariantViolation(
                    "INV-1", window.Id, $"Tool window '{window.Id}' has more than one state."));
            }
        }
    }

    /// <summary>INV-1: each registered tool window has a state.</summary>
    private static void CheckRegisteredHaveState(
        LayoutState state, ToolWindowRegistry registry, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var descriptor in registry.Descriptors)
        {
            if (!state.ToolWindows.Any(w => string.Equals(w.Id, descriptor.Id, StringComparison.Ordinal)))
            {
                violations.Add(new InvariantViolation(
                    "INV-1", descriptor.Id, $"Registered tool window '{descriptor.Id}' has no state."));
            }
        }
    }

    /// <summary>INV-2: per slot, at most one open window in the docked layer and one in the overlay layer.</summary>
    private static void CheckSlotLayers(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var group in state.ToolWindows
                     .Where(w => w.IsOpen && w.Mode.GetLayer() != ToolWindowLayer.Floating)
                     .GroupBy(w => (w.Slot, Layer: w.Mode.GetLayer()))
                     .Where(g => g.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(w => $"'{w.Id}'"));
            violations.Add(new InvariantViolation(
                "INV-2",
                ToolWindowId: null,
                $"Slot {group.Key.Slot.Side}.{group.Key.Slot.Group} has more than one open window " +
                $"in the {group.Key.Layer} layer: {ids}."));
        }
    }

    /// <summary>INV-3: orders form a dense 0..n−1 sequence within each slot, over all its windows.</summary>
    private static void CheckOrderDensity(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var slotGroup in state.ToolWindows.GroupBy(w => w.Slot))
        {
            var orders = slotGroup.Select(w => w.Order).Order().ToList();
            if (!orders.SequenceEqual(Enumerable.Range(0, orders.Count)))
            {
                violations.Add(new InvariantViolation(
                    "INV-3",
                    ToolWindowId: null,
                    $"Slot {slotGroup.Key.Side}.{slotGroup.Key.Group} orders [{string.Join(", ", orders)}] " +
                    $"are not the dense sequence 0..{orders.Count - 1}."));
            }
        }
    }

    /// <summary>INV-4: side weights and ratios, pair ratios and undock weights are strictly within (0..1).</summary>
    private static void CheckFractions(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var side in new[] { ToolWindowSide.Left, ToolWindowSide.Right, ToolWindowSide.Bottom })
        {
            var geometry = state.GetSide(side);
            CheckFraction(violations, geometry.Weight, $"{side} side Weight", toolWindowId: null);
            CheckFraction(violations, geometry.CurrentRatio, $"{side} side CurrentRatio", toolWindowId: null);
        }

        foreach (var window in state.ToolWindows)
        {
            CheckFraction(violations, window.PairRatio, $"PairRatio of '{window.Id}'", window.Id);
            CheckFraction(violations, window.UndockWeight, $"UndockWeight of '{window.Id}'", window.Id);
        }
    }

    private static void CheckFraction(
        ImmutableArray<InvariantViolation>.Builder violations, double value, string what, string? toolWindowId)
    {
        if (!(value > 0 && value < 1))
        {
            violations.Add(new InvariantViolation(
                "INV-4", toolWindowId, $"{what} = {value} is outside the open interval (0..1)."));
        }
    }

    /// <summary>INV-5: the active tool window id refers to an existing open window or is null.</summary>
    private static void CheckActiveToolWindow(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        if (state.ActiveToolWindowId is not { } activeId)
        {
            return;
        }

        var active = state.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, activeId, StringComparison.Ordinal));
        if (active is null)
        {
            violations.Add(new InvariantViolation(
                "INV-5", activeId, $"ActiveToolWindowId '{activeId}' does not refer to an existing tool window."));
        }
        else if (!active.IsOpen)
        {
            violations.Add(new InvariantViolation(
                "INV-5", activeId, $"ActiveToolWindowId '{activeId}' refers to a closed tool window."));
        }
    }

    /// <summary>INV-6: an open tool window has a visible stripe icon.</summary>
    private static void CheckIconVisibility(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var window in state.ToolWindows.Where(w => w.IsOpen && !w.IsIconVisible))
        {
            violations.Add(new InvariantViolation(
                "INV-6", window.Id, $"Tool window '{window.Id}' is open but its stripe icon is hidden."));
        }
    }

    /// <summary>INV-7: the last internal mode is internal and equals the mode while the mode is internal.</summary>
    private static void CheckLastInternalMode(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var window in state.ToolWindows)
        {
            if (!window.LastInternalMode.IsInternal())
            {
                violations.Add(new InvariantViolation(
                    "INV-7", window.Id,
                    $"LastInternalMode of '{window.Id}' is {window.LastInternalMode}, which is not internal."));
            }
            else if (window.Mode.IsInternal() && window.LastInternalMode != window.Mode)
            {
                violations.Add(new InvariantViolation(
                    "INV-7", window.Id,
                    $"Tool window '{window.Id}' has internal Mode {window.Mode} " +
                    $"but LastInternalMode {window.LastInternalMode}."));
            }
        }
    }
}
