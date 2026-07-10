using System.Collections.Immutable;

namespace Berth;

/// <summary>
/// Snapshot application and defaults (spec TW-5.14, TW-10.3…TW-10.7, DA-9.1…DA-9.4). A snapshot
/// is any <see cref="LayoutState"/>: the state is immutable, so holding a reference is taking a
/// snapshot — there is no copying API. <see cref="Apply"/> is the single normalization gate of
/// persistence: it turns any input into a valid state and reports every fix exactly once
/// (TW-10.4, DA-9.2); the serialization layer (<see cref="LayoutPersistence"/>) stays free of
/// repairs. Out-of-domain enum values in a programmatic snapshot are a caller error — on the
/// file path the domain is guaranteed by <see cref="LayoutPersistence.Deserialize"/> (TW-10.5).
/// </summary>
public static class LayoutApply
{
    /// <summary>
    /// Applies a snapshot (spec TW-5.14): an atomic replacement with normalization within the
    /// given scope. With <see cref="ApplyScope.Full"/> the snapshot replaces the whole state and
    /// <paramref name="current"/> is ignored; reconciliation with the registry follows TW-10.3
    /// (saved state wins over the descriptor; registered but not saved windows get descriptor
    /// defaults after the existing windows of their slot), unknown ids stay as sleeping states
    /// (TW-10.2) and unknown dock tabs stay as sleeping tabs (DA-9.4). With
    /// <see cref="ApplyScope.Arrangement"/> only the placement of the tool windows mentioned in
    /// the snapshot, the side geometry and the quick access side are merged into
    /// <paramref name="current"/> (TW-10.7); the dock area is not touched at all (DA-9.1) and
    /// the active tool window resets to null. In the Full scope registered windows with a body
    /// factory receive their body tab by the seeding rule of TW-9.5 (reconciliation, not
    /// reported), and a tab with a confirmed foreign owner in a panel tree relocates to the
    /// main window's current group (INV-D5, DA-9.2); in the Arrangement scope the trees of new
    /// sleeping records are cleaned instead — duplicates and confirmed-foreign tabs are removed
    /// with a report, because the dock area cannot receive them (DA-9.1). A conflicted
    /// ownership claim confirms nothing: the tab stays and the application error surfaces at
    /// the next operation or materialization (TW-9.11). The report lists every applied fix exactly once
    /// (TW-10.4, DA-9.2); an application wanting «a proper layout or nothing» rejects the result
    /// when <see cref="ApplyResult.Fixes"/> is non-empty.
    /// </summary>
    /// <param name="current">The live state; the merge base for <see cref="ApplyScope.Arrangement"/>, ignored for <see cref="ApplyScope.Full"/>.</param>
    /// <param name="snapshot">The snapshot to apply — saved, constructed or held earlier.</param>
    /// <param name="scope">Which part of the state the snapshot governs (TW-10.6).</param>
    /// <param name="registry">Registered descriptors for reconciliation (TW-10.3).</param>
    /// <param name="validateBounds">
    /// Optional UI validation of saved screen bounds (TW-7.4, DA-7.4). In the Full scope it runs
    /// over every saved floating bounds and every document window; in the Arrangement scope —
    /// only over new sleeping records created from the snapshot (existing windows keep their
    /// live, already valid bounds, TW-10.7). Null accepts all bounds as saved.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The scope is not a defined value.</exception>
    public static ApplyResult Apply(
        this LayoutState current,
        LayoutState snapshot,
        ApplyScope scope,
        ToolWindowRegistry registry,
        BoundsValidator? validateBounds = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        return scope switch
        {
            ApplyScope.Full => ApplyFull(snapshot, registry, validateBounds),
            ApplyScope.Arrangement => ApplyArrangement(current, snapshot, registry, validateBounds),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, message: null),
        };
    }

    /// <summary>
    /// Builds the default layout from the registration descriptors (spec TW-5.14, TW-10.3):
    /// every tool window closed in its default slot, mode and pair ratio; orders are dense per
    /// slot — explicit <see cref="ToolWindowDescriptor.DefaultOrder"/> first, then registration
    /// order; default side geometry and an empty dock area; tool windows with a body factory
    /// receive their body tab (the seeding rule of TW-9.5). To reset the placement without
    /// closing open documents, apply the result with the Arrangement scope:
    /// <c>current.Apply(LayoutApply.ResetToDefaults(registry), ApplyScope.Arrangement, registry)</c>
    /// — the dock area and content trees stay current (TW-10.6).
    /// </summary>
    /// <param name="registry">Registered descriptors to build the layout from.</param>
    public static LayoutState ResetToDefaults(ToolWindowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var windows = ImmutableArray.CreateBuilder<ToolWindowState>();
        foreach (var slotGroup in registry.Descriptors
                     .Select((descriptor, index) => (Descriptor: descriptor, Index: index))
                     .GroupBy(x => x.Descriptor.DefaultSlot))
        {
            var ordered = slotGroup
                .OrderBy(x => x.Descriptor.DefaultOrder ?? int.MaxValue)
                .ThenBy(x => x.Index)
                .ToList();
            for (var order = 0; order < ordered.Count; order++)
            {
                windows.Add(FromDescriptor(ordered[order].Descriptor, order));
            }
        }

        return SeedBodies(LayoutState.Empty with { ToolWindows = windows.ToImmutable() }, registry);
    }

    private static ApplyResult ApplyFull(
        LayoutState snapshot, ToolWindowRegistry registry, BoundsValidator? validateBounds)
    {
        var fixes = ImmutableArray.CreateBuilder<AppliedFix>();
        var state = Reconcile(snapshot, registry);
        state = SeedBodies(state, registry);
        CollectDefects(state, registry, ApplyScope.Full, fixes);
        state = NormalizeToolWindows(state);
        state = ValidateToolWindowBounds(state, validateBounds, onlyIds: null, fixes);
        state = NormalizeTrees(state, fixes);
        state = RelocateForeignPanelTabs(state, registry, fixes);
        state = ValidateDocumentWindowBounds(state, validateBounds, fixes);
        return new ApplyResult(state, fixes.ToImmutable());
    }

    private static ApplyResult ApplyArrangement(
        LayoutState current, LayoutState snapshot, ToolWindowRegistry registry, BoundsValidator? validateBounds)
    {
        var fixes = ImmutableArray.CreateBuilder<AppliedFix>();

        // Duplicate ids inside the layout snapshot: the first mention wins (TW-10.4).
        var mentioned = new List<ToolWindowState>(snapshot.ToolWindows.Length);
        var mentionedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in snapshot.ToolWindows)
        {
            if (mentionedIds.Add(window.Id))
            {
                mentioned.Add(window);
            }
            else
            {
                fixes.Add(new AppliedFix(
                    "INV-1", window.Id,
                    $"Tool window '{window.Id}' is mentioned more than once in the layout; the first mention was kept."));
            }
        }

        // Mentioned windows take placement from the snapshot and keep their own geometry and
        // content (TW-10.7); a mentioned id unknown to the current state becomes a new sleeping
        // record built from the snapshot entirely — there are no current values to keep.
        var newSleeping = new HashSet<string>(StringComparer.Ordinal);
        var windows = new List<ToolWindowState>(mentioned.Count + current.ToolWindows.Length);
        foreach (var window in mentioned)
        {
            var existing = current.ToolWindows.FirstOrDefault(
                w => string.Equals(w.Id, window.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                newSleeping.Add(window.Id);
                windows.Add(window);
            }
            else
            {
                windows.Add(existing with
                {
                    Slot = window.Slot,
                    Order = window.Order,
                    Mode = window.Mode,
                    LastInternalMode = window.LastInternalMode,
                    IsOpen = window.IsOpen,
                    IsIconVisible = window.IsIconVisible,
                });
            }
        }

        var mentionedCount = windows.Count;
        windows.AddRange(current.ToolWindows.Where(w => !mentionedIds.Contains(w.Id)));

        // Slot orders are rebuilt mechanically, not repaired: mentioned windows first in
        // snapshot order, then unmentioned in their previous relative order (TW-10.7, E25).
        foreach (var slotGroup in windows
                     .Select((w, i) => (Window: w, Index: i))
                     .GroupBy(x => x.Window.Slot))
        {
            var ordered = slotGroup
                .OrderBy(x => x.Index < mentionedCount ? 0 : 1)
                .ThenBy(x => x.Window.Order)
                .ThenBy(x => x.Index)
                .ToList();
            for (var order = 0; order < ordered.Count; order++)
            {
                windows[ordered[order].Index] = ordered[order].Window with { Order = order };
            }
        }

        // A window opened by the snapshot evicts unmentioned open windows of its slot layer —
        // regular TW-5.1 behaviour, not a fix (TW-10.7, E24). A layer the snapshot does not
        // occupy keeps its open window.
        foreach (var layerGroup in windows
                     .Select((w, i) => (Window: w, Index: i))
                     .Where(x => x.Window.IsOpen && x.Window.Mode.GetLayer() != ToolWindowLayer.Floating)
                     .GroupBy(x => (x.Window.Slot, Layer: x.Window.Mode.GetLayer())))
        {
            if (!layerGroup.Any(x => x.Index < mentionedCount))
            {
                continue;
            }

            foreach (var (window, index) in layerGroup.Where(x => x.Index >= mentionedCount))
            {
                windows[index] = window with { IsOpen = false };
            }
        }

        // Side geometry and the quick access side come from the snapshot; the activity reset is
        // part of the contract, not a fix (TW-10.7); the dock area stays current (DA-9.1).
        var state = current with
        {
            ToolWindows = [.. windows],
            Left = snapshot.Left,
            Right = snapshot.Right,
            Bottom = snapshot.Bottom,
            QuickAccessSide = snapshot.QuickAccessSide,
            ActiveToolWindowId = null,
        };

        state = CleanNewSleepingTrees(state, newSleeping, registry, fixes);

        CollectDefects(state, registry, ApplyScope.Arrangement, fixes);
        state = NormalizeToolWindows(state);
        state = ValidateToolWindowBounds(state, validateBounds, newSleeping, fixes);
        return new ApplyResult(state, fixes.ToImmutable());
    }

    /// <summary>
    /// Trees of new sleeping records created by an Arrangement snapshot (TW-10.7): normalized,
    /// deduplicated against the current layout and each other (DA-9.2), and stripped of tabs
    /// with a confirmed foreign owner — the Arrangement scope cannot move tabs into the dock
    /// area (DA-9.1), so removal with a report replaces the Full-scope relocation (INV-D5).
    /// </summary>
    private static LayoutState CleanNewSleepingTrees(
        LayoutState state,
        HashSet<string> newSleeping,
        ToolWindowRegistry registry,
        ImmutableArray<AppliedFix>.Builder fixes)
    {
        if (newSleeping.Count == 0)
        {
            return state;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in TabTreeTraversal.EnumerateGroups(state.DockArea.Root))
        {
            seen.UnionWith(group.Tabs);
        }

        foreach (var window in state.DockArea.Windows)
        {
            foreach (var group in TabTreeTraversal.EnumerateGroups(window.Root))
            {
                seen.UnionWith(group.Tabs);
            }
        }

        foreach (var panel in state.ToolWindows.Where(w => !newSleeping.Contains(w.Id)))
        {
            foreach (var group in TabTreeTraversal.EnumerateGroups(panel.ContentTree))
            {
                seen.UnionWith(group.Tabs);
            }
        }

        var panels = state.ToolWindows;
        for (var i = 0; i < panels.Length; i++)
        {
            if (!newSleeping.Contains(panels[i].Id))
            {
                continue;
            }

            var tree = TabTreeNormalization.Normalize(panels[i].ContentTree);
            List<string>? drop = null;
            foreach (var group in TabTreeTraversal.EnumerateGroups(tree))
            {
                foreach (var tab in group.Tabs)
                {
                    if (!seen.Add(tab))
                    {
                        (drop ??= []).Add(tab);
                        fixes.Add(new AppliedFix(
                            "DA-9.2", tab,
                            $"Duplicate tab '{tab}' was removed from the new sleeping record of tool window '{panels[i].Id}'; the occurrence in the current layout was kept."));
                    }
                    else if (registry.ResolveTabClaim(tab, out var claim, out _)
                        && claim.Owner != TabOwner.ToolWindow(panels[i].Id))
                    {
                        (drop ??= []).Add(tab);
                        fixes.Add(new AppliedFix(
                            "INV-D5", tab,
                            $"Tab '{tab}' in the new sleeping record of tool window '{panels[i].Id}' has a confirmed foreign owner and was removed: the Arrangement scope cannot move tabs into the dock area (DA-9.1)."));
                    }
                }
            }

            if (drop is not null)
            {
                foreach (var tab in drop)
                {
                    tree = RemoveTabFromTree(tree, tab);
                }

                tree = TabTreeNormalization.Normalize(tree);
            }

            if (!ReferenceEquals(tree, panels[i].ContentTree))
            {
                panels = panels.SetItem(i, panels[i] with { ContentTree = tree });
            }
        }

        return panels == state.ToolWindows ? state : state with { ToolWindows = panels };
    }

    /// <summary>
    /// TW-10.3: registered but not saved → descriptor defaults, order after the existing
    /// windows of the slot. Reconciliation is regular behaviour, not a fix — nothing is
    /// reported. Saved states, including sleeping ones, win over descriptors.
    /// </summary>
    private static LayoutState Reconcile(LayoutState snapshot, ToolWindowRegistry registry)
    {
        List<ToolWindowState>? added = null;
        foreach (var descriptor in registry.Descriptors)
        {
            if (snapshot.ToolWindows.Any(w => string.Equals(w.Id, descriptor.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            var slot = descriptor.DefaultSlot;
            var nextOrder = snapshot.ToolWindows
                .Concat(added ?? [])
                .Where(w => w.Slot == slot)
                .Select(w => w.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            (added ??= []).Add(FromDescriptor(descriptor, Math.Max(0, nextOrder)));
        }

        return added is null ? snapshot : snapshot with { ToolWindows = snapshot.ToolWindows.AddRange(added) };
    }

    /// <summary>The default state of a freshly registered tool window (spec TW-10.3); shared with the live registration path (<see cref="ContentLifecycle.Register"/>).</summary>
    internal static ToolWindowState FromDescriptor(ToolWindowDescriptor descriptor, int order) =>
        new ToolWindowState(descriptor.Id, descriptor.DefaultSlot, order) with
        {
            Mode = descriptor.DefaultMode,
            LastInternalMode = descriptor.DefaultMode.IsInternal()
                ? descriptor.DefaultMode
                : LayoutDefaults.LastInternalMode,
            PairRatio = descriptor.DefaultPairRatio,
        };

    /// <summary>
    /// The body seeding of TW-9.5, shared by the Full apply, <see cref="ResetToDefaults"/> and
    /// live registration (TW-10.3): a registered tool window with a body factory receives its
    /// body tab — id equal to the window's own id — as the root group of its tree, when that id
    /// is absent from every tree of the layout and the tree holds no tabs. A non-empty tree
    /// without the body is not touched (the body was closed deliberately), and a body living in
    /// another host cancels the seed (INV-D2). Seeding is reconciliation, not a repair —
    /// nothing is reported.
    /// </summary>
    internal static LayoutState SeedBodies(LayoutState state, ToolWindowRegistry registry)
    {
        foreach (var descriptor in registry.Descriptors)
        {
            state = SeedBody(state, descriptor);
        }

        return state;
    }

    /// <summary>The single-descriptor seeding behind <see cref="SeedBodies"/>; see the rule there.</summary>
    internal static LayoutState SeedBody(LayoutState state, ToolWindowDescriptor descriptor)
    {
        if (descriptor.ContentFactory is null)
        {
            return state;
        }

        var windows = state.ToolWindows;
        for (var i = 0; i < windows.Length; i++)
        {
            if (!string.Equals(windows[i].Id, descriptor.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (TabTreeTraversal.HasTabs(windows[i].ContentTree)
                || TabTreeTraversal.LayoutContainsTab(state, descriptor.Id))
            {
                return state;
            }

            return state with
            {
                ToolWindows = windows.SetItem(i, windows[i] with
                {
                    ContentTree = new TabGroupNode { Tabs = [descriptor.Id], ActiveTabId = descriptor.Id },
                }),
            };
        }

        return state;
    }

    /// <summary>
    /// The INV-D5 relocation of DA-9.2, shared by the Full apply (with report entries) and live
    /// registration (no report channel — the deliberate asymmetry of TW-10.3): a tab in a panel
    /// tree whose confirmed owner is someone else moves to the end of the main window's current
    /// group, into an empty tree — as the root group. Activity is not reassigned beyond what
    /// the invariants force on an empty tree (INV-D4); a conflicted claim confirms nothing and
    /// the tab stays (TW-9.11).
    /// </summary>
    internal static LayoutState RelocateForeignPanelTabs(
        LayoutState state, ToolWindowRegistry registry, ImmutableArray<AppliedFix>.Builder? fixes)
    {
        List<string>? moved = null;
        var panels = state.ToolWindows;
        for (var i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            List<string>? foreign = null;
            foreach (var group in TabTreeTraversal.EnumerateGroups(panel.ContentTree))
            {
                foreach (var tab in group.Tabs)
                {
                    if (registry.ResolveTabClaim(tab, out var claim, out _)
                        && claim.Owner != TabOwner.ToolWindow(panel.Id))
                    {
                        (foreign ??= []).Add(tab);
                    }
                }
            }

            if (foreign is null)
            {
                continue;
            }

            var tree = panel.ContentTree;
            foreach (var tab in foreign)
            {
                tree = RemoveTabFromTree(tree, tab);
                (moved ??= []).Add(tab);
                fixes?.Add(new AppliedFix(
                    "INV-D5", tab,
                    $"Tab '{tab}' in the tree of tool window '{panel.Id}' has a confirmed foreign owner and was moved to the main window."));
            }

            panels = panels.SetItem(i, panel with { ContentTree = tree });
        }

        if (moved is null)
        {
            return state;
        }

        state = AppendToMainCurrentGroup(state with { ToolWindows = panels }, moved);
        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// DA-9.2: appends relocated tabs to the end of the main window's current group without
    /// reassigning activity; an empty tree receives them as the root group and the invariants
    /// then force the assignment (INV-D4).
    /// </summary>
    private static LayoutState AppendToMainCurrentGroup(LayoutState state, List<string> tabs)
    {
        var area = state.DockArea;
        if (area.CurrentTabId is { } current
            && TabTreeTraversal.TryFindGroupPath(area.Root, current, out var path, out var group))
        {
            var appended = group with { Tabs = group.Tabs.AddRange(tabs) };
            return state with
            {
                DockArea = area with { Root = TabTreeTraversal.ReplaceNode(area.Root, path, appended) },
            };
        }

        // INV-D4: a null current tab means the normalized tree holds no tabs — the root group.
        var root = (TabGroupNode)area.Root;
        return state with { DockArea = area with { Root = root with { Tabs = root.Tabs.AddRange(tabs) } } };
    }

    /// <summary>Removes one tab from whichever group of the tree holds it; a dangling group active is healed by N5.</summary>
    private static TabTreeNode RemoveTabFromTree(TabTreeNode root, string tabId)
    {
        if (!TabTreeTraversal.TryFindGroupPath(root, tabId, out var path, out var group))
        {
            return root;
        }

        var updated = group with { Tabs = group.Tabs.RemoveAt(group.Tabs.IndexOf(tabId)) };
        return TabTreeTraversal.ReplaceNode(root, path, updated);
    }

    /// <summary>
    /// Turns pre-repair invariant violations into report entries — every fix is reported
    /// exactly once (TW-10.4, DA-9.2). In the Full scope INV-D2, INV-D5 and INV-D6 are skipped
    /// wholesale: the tree pipeline emits its own, more precise entries (DA-9.2 deduplication,
    /// INV-D5 relocation, INV-D6 window removal) that supersede them. In the Arrangement scope
    /// every dock invariant is skipped — the dock area is not touched (DA-9.1) and the new
    /// sleeping trees get their own cleanup entries — together with INV-1, whose only
    /// post-merge case (registered without a state) belongs to the caller's current state,
    /// not to the snapshot.
    /// </summary>
    private static void CollectDefects(
        LayoutState state, ToolWindowRegistry registry, ApplyScope scope, ImmutableArray<AppliedFix>.Builder fixes)
    {
        foreach (var violation in LayoutInvariants.Validate(state, registry))
        {
            var skip = scope == ApplyScope.Full
                ? violation.InvariantId is "INV-D2" or "INV-D5" or "INV-D6"
                : violation.InvariantId.StartsWith("INV-D", StringComparison.Ordinal)
                    || violation.InvariantId is "INV-1";
            if (!skip)
            {
                fixes.Add(new AppliedFix(violation.InvariantId, violation.ToolWindowId, violation.Message));
            }
        }
    }

    /// <summary>
    /// The tool window normalization of TW-10.4: duplicate ids keep the first occurrence,
    /// invalid fractions become field defaults, LastInternalMode is bound to the mode (INV-7),
    /// each slot layer keeps one open window with the minimal order (INV-2), an open window
    /// shows its icon (INV-6), orders are compacted per slot (INV-3), and the active id — checked
    /// after the openness conflicts are resolved — must refer to an existing open window (INV-5).
    /// </summary>
    private static LayoutState NormalizeToolWindows(LayoutState state)
    {
        var windows = new List<ToolWindowState>(state.ToolWindows.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in state.ToolWindows)
        {
            if (seen.Add(window.Id))
            {
                windows.Add(window);
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!IsValidFraction(window.PairRatio))
            {
                window = window with { PairRatio = LayoutDefaults.PairRatio };
            }

            if (window.Mode.IsInternal())
            {
                if (window.LastInternalMode != window.Mode)
                {
                    window = window with { LastInternalMode = window.Mode };
                }
            }
            else if (!window.LastInternalMode.IsInternal())
            {
                window = window with { LastInternalMode = LayoutDefaults.LastInternalMode };
            }

            windows[i] = window;
        }

        foreach (var layerGroup in windows
                     .Select((w, i) => (Window: w, Index: i))
                     .Where(x => x.Window.IsOpen && x.Window.Mode.GetLayer() != ToolWindowLayer.Floating)
                     .GroupBy(x => (x.Window.Slot, Layer: x.Window.Mode.GetLayer())))
        {
            var keeper = layerGroup.OrderBy(x => x.Window.Order).ThenBy(x => x.Index).First().Index;
            foreach (var (window, index) in layerGroup)
            {
                if (index != keeper)
                {
                    windows[index] = window with { IsOpen = false };
                }
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].IsOpen && !windows[i].IsIconVisible)
            {
                windows[i] = windows[i] with { IsIconVisible = true };
            }
        }

        foreach (var slotGroup in windows
                     .Select((w, i) => (Window: w, Index: i))
                     .GroupBy(x => x.Window.Slot))
        {
            var ordered = slotGroup.OrderBy(x => x.Window.Order).ThenBy(x => x.Index).ToList();
            for (var order = 0; order < ordered.Count; order++)
            {
                if (ordered[order].Window.Order != order)
                {
                    windows[ordered[order].Index] = ordered[order].Window with { Order = order };
                }
            }
        }

        var active = state.ActiveToolWindowId;
        if (active is not null
            && !windows.Any(w => string.Equals(w.Id, active, StringComparison.Ordinal) && w.IsOpen))
        {
            active = null;
        }

        return state with
        {
            ToolWindows = [.. windows],
            Left = NormalizeSide(state.Left),
            Right = NormalizeSide(state.Right),
            Bottom = NormalizeSide(state.Bottom),
            ActiveToolWindowId = active,
        };
    }

    private static SideState NormalizeSide(SideState side) =>
        new(IsValidFraction(side.Weight) ? side.Weight : LayoutDefaults.SideWeight);

    /// <summary>
    /// Saved floating bounds of tool windows: non-numeric values are reset to null (TW-10.4 —
    /// the next Float/Window transition takes screen bounds from the UI, TW-5.6), the rest go
    /// through the UI validator (TW-7.4). <paramref name="onlyIds"/> restricts the pass to new
    /// sleeping records in the Arrangement scope (TW-10.7).
    /// </summary>
    private static LayoutState ValidateToolWindowBounds(
        LayoutState state,
        BoundsValidator? validateBounds,
        HashSet<string>? onlyIds,
        ImmutableArray<AppliedFix>.Builder fixes)
    {
        var windows = state.ToolWindows;
        for (var i = 0; i < windows.Length; i++)
        {
            var window = windows[i];
            if (window.FloatingBounds is not { } bounds || (onlyIds is not null && !onlyIds.Contains(window.Id)))
            {
                continue;
            }

            if (!bounds.IsFinite)
            {
                windows = windows.SetItem(i, window with { FloatingBounds = null });
                fixes.Add(new AppliedFix(
                    "TW-10.4", window.Id,
                    $"Non-numeric FloatingBounds of '{window.Id}' were reset; the next Float/Window transition takes screen bounds from the UI."));
            }
            else if (validateBounds?.Invoke(bounds) is { } replacement)
            {
                windows = windows.SetItem(i, window with { FloatingBounds = replacement });
                fixes.Add(new AppliedFix(
                    "TW-7.4", window.Id,
                    $"Saved FloatingBounds of '{window.Id}' failed screen validation and were replaced."));
            }
        }

        return state with { ToolWindows = windows };
    }

    /// <summary>
    /// The tree part of the Full apply: deduplication across every tree (DA-9.2), explicit
    /// entries for document windows about to disappear (INV-D6), then the layout
    /// normalization — N1–N5 over every tree, window removal, current-tab and active-host
    /// fallbacks. Secondary activity reassignments caused by these fixes produce no entries of
    /// their own (DA-9.2).
    /// </summary>
    private static LayoutState NormalizeTrees(LayoutState state, ImmutableArray<AppliedFix>.Builder fixes)
    {
        state = DeduplicateTabs(state, fixes);
        for (var i = 0; i < state.DockArea.Windows.Length; i++)
        {
            if (!TabTreeTraversal.HasTabs(state.DockArea.Windows[i].Root))
            {
                fixes.Add(new AppliedFix(
                    "INV-D6", SubjectId: null,
                    $"Document window {i} has a tree without tabs and was removed."));
            }
        }

        return TabTreeNormalization.Normalize(state);
    }

    /// <summary>
    /// DA-9.2: a tab occurring more than once keeps its first occurrence in traversal order —
    /// the main window, document windows in list order, then panel trees in state order;
    /// depth-first left-to-right within a tree, tab order within a group. A group active
    /// dangling after a removal is healed by normalization (N5) without an entry of its own.
    /// </summary>
    private static LayoutState DeduplicateTabs(LayoutState state, ImmutableArray<AppliedFix>.Builder fixes)
    {
        var firstHost = new Dictionary<string, string>(StringComparer.Ordinal);
        var area = state.DockArea;
        var mainRoot = DeduplicateTree(area.Root, "the main window", firstHost, fixes);
        var windows = area.Windows;
        for (var i = 0; i < windows.Length; i++)
        {
            var root = DeduplicateTree(windows[i].Root, $"document window {i}", firstHost, fixes);
            if (!ReferenceEquals(root, windows[i].Root))
            {
                windows = windows.SetItem(i, windows[i] with { Root = root });
            }
        }

        var panels = state.ToolWindows;
        for (var i = 0; i < panels.Length; i++)
        {
            var tree = DeduplicateTree(panels[i].ContentTree, $"tool window '{panels[i].Id}'", firstHost, fixes);
            if (!ReferenceEquals(tree, panels[i].ContentTree))
            {
                panels = panels.SetItem(i, panels[i] with { ContentTree = tree });
            }
        }

        if (!ReferenceEquals(mainRoot, area.Root) || windows != area.Windows)
        {
            state = state with { DockArea = area with { Root = mainRoot, Windows = windows } };
        }

        return panels == state.ToolWindows ? state : state with { ToolWindows = panels };
    }

    private static TabTreeNode DeduplicateTree(
        TabTreeNode node, string host, Dictionary<string, string> firstHost, ImmutableArray<AppliedFix>.Builder fixes)
    {
        switch (node)
        {
            case TabGroupNode group:
            {
                var kept = ImmutableArray.CreateBuilder<string>(group.Tabs.Length);
                foreach (var tab in group.Tabs)
                {
                    if (firstHost.TryAdd(tab, host))
                    {
                        kept.Add(tab);
                    }
                    else
                    {
                        fixes.Add(new AppliedFix(
                            "DA-9.2", tab,
                            $"Duplicate tab '{tab}' was removed from {host}; the first occurrence in {firstHost[tab]} was kept."));
                    }
                }

                return kept.Count == group.Tabs.Length ? group : group with { Tabs = kept.ToImmutable() };
            }

            case SplitNode split:
            {
                var children = split.Children;
                for (var i = 0; i < children.Length; i++)
                {
                    var deduplicated = DeduplicateTree(children[i].Node, host, firstHost, fixes);
                    if (!ReferenceEquals(deduplicated, children[i].Node))
                    {
                        children = children.SetItem(i, children[i] with { Node = deduplicated });
                    }
                }

                return children == split.Children ? split : split with { Children = children };
            }

            default:
                return node;
        }
    }

    /// <summary>DA-7.4: bounds of document windows go through the UI validator; runs after zone normalization, so removed windows are not validated and indices are final.</summary>
    private static LayoutState ValidateDocumentWindowBounds(
        LayoutState state, BoundsValidator? validateBounds, ImmutableArray<AppliedFix>.Builder fixes)
    {
        if (validateBounds is null)
        {
            return state;
        }

        var windows = state.DockArea.Windows;
        for (var i = 0; i < windows.Length; i++)
        {
            if (validateBounds(windows[i].Bounds) is { } replacement)
            {
                windows = windows.SetItem(i, windows[i] with { Bounds = replacement });
                fixes.Add(new AppliedFix(
                    "DA-7.4", SubjectId: null,
                    $"Saved Bounds of document window {i} failed screen validation and were replaced."));
            }
        }

        return windows == state.DockArea.Windows
            ? state
            : state with { DockArea = state.DockArea with { Windows = windows } };
    }

    private static bool IsValidFraction(double value) => value > 0 && value < 1;
}
