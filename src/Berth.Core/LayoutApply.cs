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
    /// the active tool window resets to null. The report lists every applied fix exactly once
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
    /// order; default side geometry and an empty dock area. To reset the placement without
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

        return LayoutState.Empty with { ToolWindows = windows.ToImmutable() };
    }

    private static ApplyResult ApplyFull(
        LayoutState snapshot, ToolWindowRegistry registry, BoundsValidator? validateBounds)
    {
        var fixes = ImmutableArray.CreateBuilder<AppliedFix>();
        var state = Reconcile(snapshot, registry);
        CollectDefects(state, registry, ApplyScope.Full, fixes);
        state = NormalizeToolWindows(state);
        state = ValidateToolWindowBounds(state, validateBounds, onlyIds: null, fixes);
        state = NormalizeDockArea(state, fixes);
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

        CollectDefects(state, registry, ApplyScope.Arrangement, fixes);
        state = NormalizeToolWindows(state);
        state = ValidateToolWindowBounds(state, validateBounds, newSleeping, fixes);
        return new ApplyResult(state, fixes.ToImmutable());
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

    private static ToolWindowState FromDescriptor(ToolWindowDescriptor descriptor, int order) =>
        new ToolWindowState(descriptor.Id, descriptor.DefaultSlot, order) with
        {
            Mode = descriptor.DefaultMode,
            LastInternalMode = descriptor.DefaultMode.IsInternal()
                ? descriptor.DefaultMode
                : LayoutDefaults.LastInternalMode,
            PairRatio = descriptor.DefaultPairRatio,
        };

    /// <summary>
    /// Turns pre-repair invariant violations into report entries — every fix is reported
    /// exactly once (TW-10.4, DA-9.2). In the Full scope INV-D2 and INV-D6 are skipped
    /// wholesale: the dock pipeline emits its own, more precise entries (DA-9.2 deduplication,
    /// INV-D6 window removal) that supersede them. In the Arrangement scope every dock
    /// invariant is skipped — the dock area is not touched (DA-9.1) — together with INV-1,
    /// whose only post-merge case (registered without a state) belongs to the caller's current
    /// state, not to the snapshot.
    /// </summary>
    private static void CollectDefects(
        LayoutState state, ToolWindowRegistry registry, ApplyScope scope, ImmutableArray<AppliedFix>.Builder fixes)
    {
        foreach (var violation in LayoutInvariants.Validate(state, registry))
        {
            var skip = scope == ApplyScope.Full
                ? violation.InvariantId is "INV-D2" or "INV-D6"
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

            if (!IsValidFraction(window.UndockWeight))
            {
                window = window with { UndockWeight = LayoutDefaults.UndockWeight };
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

    private static SideState NormalizeSide(SideState side) => new(
        IsValidFraction(side.Weight) ? side.Weight : LayoutDefaults.SideWeight,
        IsValidFraction(side.CurrentRatio) ? side.CurrentRatio : LayoutDefaults.CurrentRatio);

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

            if (!IsFinite(bounds))
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
    /// The dock area part of the Full apply: deduplication (DA-9.2), explicit entries for
    /// document windows about to disappear (INV-D6), then the shared zone normalization —
    /// N1–N5, window removal, current-tab and active-host fallbacks. Secondary activity
    /// reassignments caused by these fixes produce no entries of their own (DA-9.2).
    /// </summary>
    private static LayoutState NormalizeDockArea(LayoutState state, ImmutableArray<AppliedFix>.Builder fixes)
    {
        var area = DeduplicateDockTabs(state.DockArea, fixes);
        for (var i = 0; i < area.Windows.Length; i++)
        {
            if (!TabTreeTraversal.HasTabs(area.Windows[i].Root))
            {
                fixes.Add(new AppliedFix(
                    "INV-D6", SubjectId: null,
                    $"Document window {i} has a tree without tabs and was removed."));
            }
        }

        return state with { DockArea = TabTreeNormalization.Normalize(area) };
    }

    /// <summary>
    /// DA-9.2: a tab occurring more than once keeps its first occurrence in traversal order —
    /// the main window, then document windows in list order; depth-first left-to-right within
    /// a tree, tab order within a group. A group active dangling after a removal is healed by
    /// normalization (N5) without an entry of its own.
    /// </summary>
    private static DockAreaState DeduplicateDockTabs(DockAreaState area, ImmutableArray<AppliedFix>.Builder fixes)
    {
        var firstHost = new Dictionary<string, string>(StringComparer.Ordinal);
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

        return ReferenceEquals(mainRoot, area.Root) && windows == area.Windows
            ? area
            : area with { Root = mainRoot, Windows = windows };
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

    private static bool IsFinite(FloatingBounds bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
}
