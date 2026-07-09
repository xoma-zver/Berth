using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Validation of the layout invariants: INV-1…INV-7 of tool windows (spec tool-windows,
/// section 4) and INV-D1…INV-D6 of the dock area (spec document-area, section 4).
/// Checked after every operation; a violation is a core bug, not a user error.
/// INV-D5 has nothing to validate yet: dock-area hosts accept every tab, and panel content
/// trees with the tab owner registry are not part of the model so far.
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
        CheckDockTreesCanonical(state, violations);
        CheckDockTabsUnique(state, violations);
        CheckDockSplitShares(state, violations);
        CheckDockActivity(state, violations);
        CheckDocumentWindowsNonEmpty(state, violations);
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

    /// <summary>
    /// INV-D1: dock-area trees are canonical — no empty groups outside the root, no splits with
    /// fewer than two children, no child split repeating its parent's orientation.
    /// </summary>
    private static void CheckDockTreesCanonical(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var (root, host) in EnumerateDockTrees(state))
        {
            CheckNodeCanonical(root, isRoot: true, parentOrientation: null, host, violations);
        }
    }

    private static void CheckNodeCanonical(
        TabTreeNode node,
        bool isRoot,
        SplitOrientation? parentOrientation,
        string host,
        ImmutableArray<InvariantViolation>.Builder violations)
    {
        switch (node)
        {
            case TabGroupNode group:
                if (!isRoot && group.Tabs.IsEmpty)
                {
                    violations.Add(new InvariantViolation(
                        "INV-D1", ToolWindowId: null,
                        $"The tree of {host} contains an empty tab group outside the root."));
                }

                break;
            case SplitNode split:
                if (split.Children.Length < 2)
                {
                    violations.Add(new InvariantViolation(
                        "INV-D1", ToolWindowId: null,
                        $"The tree of {host} contains a split with {split.Children.Length} children."));
                }

                if (split.Orientation == parentOrientation)
                {
                    violations.Add(new InvariantViolation(
                        "INV-D1", ToolWindowId: null,
                        $"The tree of {host} contains a {split.Orientation} split nested in a {split.Orientation} split."));
                }

                foreach (var child in split.Children)
                {
                    CheckNodeCanonical(child.Node, isRoot: false, split.Orientation, host, violations);
                }

                break;
        }
    }

    /// <summary>INV-D2: each tab id occurs at most once across the trees of all dock-area hosts.</summary>
    private static void CheckDockTabsUnique(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, _) in EnumerateDockTrees(state))
        {
            foreach (var group in TabTreeTraversal.EnumerateGroups(root))
            {
                foreach (var tab in group.Tabs)
                {
                    if (!seen.Add(tab))
                    {
                        violations.Add(new InvariantViolation(
                            "INV-D2", ToolWindowId: null,
                            $"Tab '{tab}' occurs more than once across the dock-area trees."));
                    }
                }
            }
        }
    }

    /// <summary>INV-D3: the shares of every split are within (0..1) and sum to 1 with the core tolerance.</summary>
    private static void CheckDockSplitShares(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var (root, host) in EnumerateDockTrees(state))
        {
            foreach (var split in TabTreeTraversal.EnumerateSplits(root))
            {
                var sum = 0.0;
                var allValid = true;
                foreach (var child in split.Children)
                {
                    if (!(child.Share > 0 && child.Share < 1))
                    {
                        violations.Add(new InvariantViolation(
                            "INV-D3", ToolWindowId: null,
                            $"The tree of {host} contains a split share {child.Share} outside the open interval (0..1)."));
                        allValid = false;
                    }

                    sum += child.Share;
                }

                if (allValid && Math.Abs(sum - 1) > TabTreeNormalization.ShareSumTolerance)
                {
                    violations.Add(new InvariantViolation(
                        "INV-D3", ToolWindowId: null,
                        $"The tree of {host} contains a split whose shares sum to {sum} instead of 1."));
                }
            }
        }
    }

    /// <summary>
    /// INV-D4: the active tab of every non-empty group belongs to the group (null only when
    /// empty); the current tab of every host exists in it and is the active tab of its group;
    /// the main window current tab is null exactly when its tree holds no tabs; the active dock
    /// host refers to an existing host.
    /// </summary>
    private static void CheckDockActivity(LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        foreach (var (root, host) in EnumerateDockTrees(state))
        {
            foreach (var group in TabTreeTraversal.EnumerateGroups(root))
            {
                if (group.Tabs.IsEmpty)
                {
                    if (group.ActiveTabId is not null)
                    {
                        violations.Add(new InvariantViolation(
                            "INV-D4", ToolWindowId: null,
                            $"The tree of {host} contains an empty group with active tab '{group.ActiveTabId}'."));
                    }
                }
                else if (group.ActiveTabId is null
                    || !group.Tabs.Contains(group.ActiveTabId, StringComparer.Ordinal))
                {
                    violations.Add(new InvariantViolation(
                        "INV-D4", ToolWindowId: null,
                        $"The tree of {host} contains a group whose active tab '{group.ActiveTabId}' is not one of its tabs."));
                }
            }
        }

        var dock = state.DockArea;
        if (dock.CurrentTabId is null && TabTreeTraversal.HasTabs(dock.Root))
        {
            violations.Add(new InvariantViolation(
                "INV-D4", ToolWindowId: null,
                "The main window current tab is null while its tree contains tabs."));
        }

        CheckCurrentTab(dock.Root, dock.CurrentTabId, "the main window", violations);
        for (var i = 0; i < dock.Windows.Length; i++)
        {
            CheckCurrentTab(dock.Windows[i].Root, dock.Windows[i].CurrentTabId, $"document window {i}", violations);
        }

        if (dock.ActiveDockHost.DocumentWindowIndex is { } index && index >= dock.Windows.Length)
        {
            violations.Add(new InvariantViolation(
                "INV-D4", ToolWindowId: null,
                $"ActiveDockHost refers to document window {index}, but only {dock.Windows.Length} exist."));
        }
    }

    private static void CheckCurrentTab(
        TabTreeNode root, string? currentTabId, string host, ImmutableArray<InvariantViolation>.Builder violations)
    {
        if (currentTabId is null)
        {
            // The null-with-tabs case of the main window is reported by the caller.
            return;
        }

        var group = TabTreeTraversal.FindGroupContaining(root, currentTabId);
        if (group is null)
        {
            violations.Add(new InvariantViolation(
                "INV-D4", ToolWindowId: null,
                $"Current tab '{currentTabId}' of {host} does not exist in its tree."));
        }
        else if (!string.Equals(group.ActiveTabId, currentTabId, StringComparison.Ordinal))
        {
            violations.Add(new InvariantViolation(
                "INV-D4", ToolWindowId: null,
                $"Current tab '{currentTabId}' of {host} is not the active tab of its group."));
        }
    }

    /// <summary>INV-D6: every document window's tree contains at least one tab.</summary>
    private static void CheckDocumentWindowsNonEmpty(
        LayoutState state, ImmutableArray<InvariantViolation>.Builder violations)
    {
        for (var i = 0; i < state.DockArea.Windows.Length; i++)
        {
            if (!TabTreeTraversal.HasTabs(state.DockArea.Windows[i].Root))
            {
                violations.Add(new InvariantViolation(
                    "INV-D6", ToolWindowId: null,
                    $"Document window {i} has a tree without tabs."));
            }
        }
    }

    private static IEnumerable<(TabTreeNode Root, string Host)> EnumerateDockTrees(LayoutState state)
    {
        yield return (state.DockArea.Root, "the main window");
        for (var i = 0; i < state.DockArea.Windows.Length; i++)
        {
            yield return (state.DockArea.Windows[i].Root, $"document window {i}");
        }
    }
}
