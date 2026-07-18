using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// One drop target of a drag gesture (spec TW-5.17, DA-9.7): the hit zone and the marker in
/// gesture coordinates (screen on the windowed platform, workspace on the overlay one — see
/// <see cref="GestureSpace"/>), plus the commit of the drop — the core command(s) of the
/// gesture, run through the workspace funnel (ADR-0004). The commit is written defensively
/// against the live state — the catalog may predate an external state change that arrived
/// without a pointer move in between — and does nothing when the drop is an identity or its
/// precondition vanished (TW-5.17, DA-E40). <see cref="HitTest"/> optionally refines the
/// rectangular zone — the diagonal wedges of DA-9.7 are not expressible as rectangles. The
/// v0.26 visual language rides on the target: the optional post-drop zone preview
/// (<see cref="ZoneRect"/>, computed off the drop's command sequence run in memory) and the
/// hint label (<see cref="Hint"/>), shown exactly while the target is active.
/// </summary>
internal sealed record DropTarget(
    Rect HitRect, Rect MarkerRect, Action<BerthWorkspace> Commit, Func<Point, bool>? HitTest = null)
{
    /// <summary>
    /// Whether the marker is a translucent area fill — the stripe position fill of TW-5.17
    /// v0.26 and the half-group and whole-group previews of DA-9.7 — rather than the opaque
    /// insertion line of a tab strip.
    /// </summary>
    public bool AreaMarker { get; init; }

    /// <summary>
    /// Key of the workspace window containing the target (spec TW-5.17, task 6.2): the
    /// TopLevel on the windowed platform, the containing <see cref="PseudoWindow"/> or null
    /// (the base surface) on the overlay one. A target hits only while its window is the top
    /// window at the pointer — the zone of an occluded window never fires.
    /// </summary>
    public object? WindowKey { get; init; }

    /// <summary>
    /// Translucent preview of the zone the panel occupies after the drop, in gesture
    /// coordinates (spec TW-5.17 v0.26): read off the drop's command sequence run on the
    /// current state in memory — never assigned — so the preview agrees with the actual
    /// outcome by construction, the derived R1 pair share included. Null — no zone: the drop
    /// leaves the panel closed, or the target is a tab target (stage 1 previews slot drops
    /// only).
    /// </summary>
    public Rect? ZoneRect { get; init; }

    /// <summary>The «Move to {slot}» hint of a stripe target (spec TW-5.17 v0.26); null — no hint (tab targets, v2).</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// The live reorder-preview payload of a strip insertion zone (spec DA-9.7 v0.18): over
    /// such a target the gesture visual replaces the marker with the strip reorder preview —
    /// the headers move apart around the framed insertion placeholder while the pointer
    /// ghost keeps riding at the cursor. Null — a non-strip target (wedges, centers, stripe
    /// zones) keeps the stage-1 visuals.
    /// </summary>
    public StripReorderPreview? StripPreview { get; init; }

    /// <summary>Whether the pointer position lies in the target's zone.</summary>
    public bool Contains(Point position) => HitRect.Contains(position) && HitTest?.Invoke(position) != false;
}

/// <summary>
/// Geometry context of one target catalog build (spec TW-5.17, task 6.2): enumerates the
/// visual roots hosting sources and targets — the workspace, plus every floating TopLevel on
/// the windowed platform — and converts control bounds into gesture coordinates, tagging each
/// target with its window key for the top-window hit-test.
/// </summary>
internal sealed class DropZoneSpace
{
    private readonly BerthWorkspace _workspace;
    private readonly bool _windowed;

    public DropZoneSpace(BerthWorkspace workspace, bool windowed)
    {
        _workspace = workspace;
        _windowed = windowed;
    }

    /// <summary>Visual roots to walk for sources and targets.</summary>
    public IEnumerable<Visual> Roots
    {
        get
        {
            yield return _workspace;
            if (_windowed)
            {
                foreach (var root in _workspace.FloatingRoots)
                {
                    yield return root;
                }
            }
        }
    }

    /// <summary>Window key of the main window's own targets.</summary>
    public object? MainWindowKey => _windowed ? TopLevel.GetTopLevel(_workspace) : null;

    /// <summary>Window key of the window containing the visual (see <see cref="DropTarget.WindowKey"/>).</summary>
    public object? KeyOf(Visual visual) => _windowed
        ? TopLevel.GetTopLevel(visual)
        : visual.FindAncestorOfType<PseudoWindow>(includeSelf: true);

    /// <summary>Bounds of a control in gesture coordinates, or null while it is detached.</summary>
    public Rect? RectOf(Control control)
    {
        if (_windowed)
        {
            if (TopLevel.GetTopLevel(control) is not { } root
                || control.TranslatePoint(default, root) is not { } origin)
            {
                return null;
            }

            return GestureSpace.FromTopLevel(root, new Rect(origin, control.Bounds.Size));
        }

        var local = control.TranslatePoint(default, _workspace);
        return local is null ? null : new Rect(local.Value, control.Bounds.Size);
    }

    /// <summary>A workspace-local rectangle in gesture coordinates, or null while the workspace is detached.</summary>
    public Rect? FromWorkspaceRect(Rect rect)
    {
        if (!_windowed)
        {
            return rect; // the overlay gesture space is the workspace itself
        }

        if (TopLevel.GetTopLevel(_workspace) is not { } root
            || _workspace.TranslatePoint(rect.TopLeft, root) is not { } origin)
        {
            return null;
        }

        return GestureSpace.FromTopLevel(root, new Rect(origin, rect.Size));
    }
}

/// <summary>
/// Catalog builder of the stripe drop targets (spec TW-5.17): the six slot segments of the two
/// stripes, receiving stripe icons and tool window headers alike — from any window of the
/// workspace (task 6.2). A drop of an internal-mode window reduces to one Move (TW-5.7); a
/// drop of a floating-mode window docks it — Move plus SetMode to the last internal mode
/// (TW-7.8, = the reference: dragging a floating tool window onto a stripe docks it). Zones
/// cover each stripe column entirely: insertion positions split a segment at the midpoints of
/// its neighbouring icons (= IDEA, AbstractDroppableStripe), free space is divided between the
/// adjacent segments, and an empty segment gets the zone of its zero position — reachable by
/// drag, unlike the reference. Positions are encoded as the visible predecessor's id and
/// mapped into the dense order at commit time (TW-1.5), so the mapping survives state changes
/// between the catalog build and the drop. Bottom segments grow upward (TW-1.4) — their zones
/// are mirrored accordingly. Each target carries the v0.26 visual language: the marker is the
/// position fill (= the reference), the hint names the slot, and the zone preview is read off
/// the drop's command sequence run on the current state in memory.
/// </summary>
internal static class StripeDropTargets
{
    public static List<DropTarget> Build(
        BerthWorkspace workspace, LayoutState state, string draggedId, DropZoneSpace space)
    {
        var targets = new List<DropTarget>();
        var context = new BuildContext(state, space, draggedId, workspace.DockedAreaRect());
        foreach (var stripe in workspace.GetVisualDescendants().OfType<ToolWindowStripe>())
        {
            var isLeft = string.Equals(stripe.Name, "PART_LeftStripe", StringComparison.Ordinal);
            AddStripe(
                targets,
                state,
                context,
                stripe,
                isLeft ? ToolWindowSide.Left : ToolWindowSide.Right,
                new ToolWindowSlot(ToolWindowSide.Bottom, isLeft ? ToolWindowGroup.Primary : ToolWindowGroup.Secondary));
        }

        return targets;
    }

    /// <summary>Shared inputs of one catalog build: the state the previews run on and the docked center area.</summary>
    private sealed record BuildContext(
        LayoutState State, DropZoneSpace Space, string DraggedId, Rect? CenterRect);

    private static void AddStripe(
        List<DropTarget> targets,
        LayoutState state,
        BuildContext context,
        ToolWindowStripe stripe,
        ToolWindowSide side,
        ToolWindowSlot bottomSlot)
    {
        if (context.Space.RectOf(stripe) is not { } rect || rect.Height <= 0)
        {
            return;
        }

        var primary = new List<(string Id, Rect Rect)>();
        var secondary = new List<(string Id, Rect Rect)>();
        var bottom = new List<(string Id, Rect Rect)>();
        Rect? separator = null;
        var topBlockBottom = rect.Top;
        foreach (var visual in stripe.GetVisualDescendants())
        {
            if (visual is StripeButton button && button.IsEffectivelyVisible)
            {
                var window = state.ToolWindows.FirstOrDefault(
                    w => string.Equals(w.Id, button.ToolWindowId, StringComparison.Ordinal));
                if (window is null || context.Space.RectOf(button) is not { } bounds)
                {
                    continue;
                }

                if (window.Slot == new ToolWindowSlot(side, ToolWindowGroup.Primary))
                {
                    primary.Add((window.Id, bounds));
                    topBlockBottom = Math.Max(topBlockBottom, bounds.Bottom);
                }
                else if (window.Slot == new ToolWindowSlot(side, ToolWindowGroup.Secondary))
                {
                    secondary.Add((window.Id, bounds));
                    topBlockBottom = Math.Max(topBlockBottom, bounds.Bottom);
                }
                else if (window.Slot == bottomSlot)
                {
                    bottom.Add((window.Id, bounds));
                }
            }
            else if (visual is Control control
                && control.Name is "PART_StripeSeparator" or "PART_QuickAccess"
                && control.IsEffectivelyVisible
                && context.Space.RectOf(control) is { } chromeBounds)
            {
                if (control.Name is "PART_StripeSeparator")
                {
                    separator = chromeBounds;
                }

                topBlockBottom = Math.Max(topBlockBottom, chromeBounds.Bottom);
            }
        }

        primary.Sort((a, b) => a.Rect.Y.CompareTo(b.Rect.Y));
        secondary.Sort((a, b) => a.Rect.Y.CompareTo(b.Rect.Y));
        bottom.Sort((a, b) => a.Rect.Y.CompareTo(b.Rect.Y));

        // Free stripe space splits between the adjacent segments (TW-5.17): the upper half
        // belongs to the top block (the side segments), the lower to the bottom segment.
        var bottomBlockTop = bottom.Count > 0 ? bottom[0].Rect.Top : rect.Bottom;
        var freeMid = Math.Clamp((topBlockBottom + bottomBlockTop) / 2, rect.Top, rect.Bottom);

        // Boundary between the Primary and Secondary zones: the separator when present
        // (= IDEA: a virtual list element), else derived from the occupied segment.
        var topEnd = freeMid;
        double boundary;
        if (separator is { } sep)
        {
            boundary = sep.Center.Y;
        }
        else if (primary.Count > 0)
        {
            boundary = Math.Min(topEnd, primary[^1].Rect.Bottom + BerthMetrics.StripeButtonSize);
        }
        else if (secondary.Count > 0)
        {
            // An empty Primary above a flush Secondary keeps its zero-position zone
            // (TW-5.17): the top edge band is reserved — the reference likewise keeps the
            // hidden position-0 separator alive as the DnD cue (TW-1.3 tracing). A Secondary
            // starting lower than the band keeps its natural top boundary.
            boundary = Math.Max(secondary[0].Rect.Top, (rect.Top + secondary[0].Rect.Center.Y) / 2);
        }
        else
        {
            boundary = (rect.Top + topEnd) / 2;
        }

        var key = context.Space.MainWindowKey; // stripes live in the main window only (TW-1.1)
        AddSegmentZones(targets, context, key, rect, rect.Top, boundary,
            primary, new ToolWindowSlot(side, ToolWindowGroup.Primary));
        AddSegmentZones(targets, context, key, rect, boundary, topEnd,
            secondary, new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        AddBottomZones(targets, context, key, rect, freeMid, bottom, bottomSlot);
    }

    /// <summary>Insertion zones of a top-down segment: gaps at the midpoints of the icons (spec TW-5.17).</summary>
    private static void AddSegmentZones(
        List<DropTarget> targets,
        BuildContext context,
        object? windowKey,
        Rect stripeRect,
        double zoneStart,
        double zoneEnd,
        List<(string Id, Rect Rect)> buttons,
        ToolWindowSlot slot)
    {
        if (buttons.Count == 0)
        {
            Add(targets, context, stripeRect, zoneStart, zoneEnd,
                Math.Min(zoneStart + (BerthMetrics.StripeButtonSize / 2), (zoneStart + zoneEnd) / 2),
                windowKey, slot, predecessorId: null);
            return;
        }

        var previous = zoneStart;
        for (var i = 0; i < buttons.Count; i++)
        {
            var mid = buttons[i].Rect.Center.Y;
            var anchor = i == 0
                ? buttons[0].Rect.Top
                : (buttons[i - 1].Rect.Bottom + buttons[i].Rect.Top) / 2;
            Add(targets, context, stripeRect, previous, mid, anchor,
                windowKey, slot, i == 0 ? null : buttons[i - 1].Id);
            previous = mid;
        }

        Add(targets, context, stripeRect, previous, zoneEnd,
            buttons[^1].Rect.Bottom,
            windowKey, slot, buttons[^1].Id);
    }

    /// <summary>
    /// Insertion zones of a bottom segment, which grows upward from the edge (spec TW-1.4):
    /// the topmost icon has the highest order, so the zone above it appends to the slot end and
    /// the zone at the edge inserts at position zero.
    /// </summary>
    private static void AddBottomZones(
        List<DropTarget> targets,
        BuildContext context,
        object? windowKey,
        Rect stripeRect,
        double zoneStart,
        List<(string Id, Rect Rect)> buttons,
        ToolWindowSlot slot)
    {
        var zoneEnd = stripeRect.Bottom;
        if (buttons.Count == 0)
        {
            Add(targets, context, stripeRect, zoneStart, zoneEnd,
                Math.Max(zoneEnd - (BerthMetrics.StripeButtonSize / 2), (zoneStart + zoneEnd) / 2),
                windowKey, slot, predecessorId: null);
            return;
        }

        var previous = zoneStart;
        for (var i = 0; i < buttons.Count; i++)
        {
            var mid = buttons[i].Rect.Center.Y;
            var anchor = i == 0
                ? buttons[0].Rect.Top
                : (buttons[i - 1].Rect.Bottom + buttons[i].Rect.Top) / 2;
            Add(targets, context, stripeRect, previous, mid, anchor,
                windowKey, slot, buttons[i].Id);
            previous = mid;
        }

        Add(targets, context, stripeRect, previous, zoneEnd,
            buttons[^1].Rect.Bottom,
            windowKey, slot, predecessorId: null);
    }

    private static void Add(
        List<DropTarget> targets,
        BuildContext context,
        Rect stripeRect,
        double zoneStart,
        double zoneEnd,
        double markerAnchor,
        object? windowKey,
        ToolWindowSlot slot,
        string? predecessorId)
    {
        if (zoneEnd - zoneStart <= 0)
        {
            return; // a degenerate zone of a crowded stripe — the neighbours cover the space
        }

        // The marker fills the position rectangle (= the reference; v0.26): a button-sized
        // fill centered on the insertion gap, clamped into the stripe.
        var markerSize = BerthMetrics.StripeButtonSize;
        var markerY = Math.Clamp(
            markerAnchor - (markerSize / 2), stripeRect.Top, Math.Max(stripeRect.Top, stripeRect.Bottom - markerSize));
        var draggedId = context.DraggedId;
        var move = MoveCommit(draggedId, slot, predecessorId);
        targets.Add(new DropTarget(
            new Rect(stripeRect.X, zoneStart, stripeRect.Width, zoneEnd - zoneStart),
            new Rect(stripeRect.X + 2, markerY, stripeRect.Width - 4, markerSize),
            ws =>
            {
                // One Move per stripe drop of an internal-mode window (TW-5.17); an identity
                // returns the state unchanged inside the funnel.
                ws.Execute(move);
                // A floating-mode window docks after the move (TW-7.8, = the reference): the
                // stripe drop is a docking gesture, so the internal-mode identity above does
                // not exempt it. Each funnel call is one command with one report (ADR-0004).
                ws.Execute(s => DockIfFloating(s, draggedId));
            })
        {
            AreaMarker = true,
            WindowKey = windowKey,
            ZoneRect = PreviewZone(context, move),
            Hint = $"Move to {ToolWindowMenus.SlotHeader(slot)}",
        });
    }

    /// <summary>
    /// The post-drop zone preview of one stripe target (spec TW-5.17 v0.26): the drop's
    /// command sequence — the move plus the docking SetMode of a floating-mode window — runs
    /// on the current state in memory, never assigned, and the zone is read off the result:
    /// the preview agrees with the actual outcome by construction, pair formation and the
    /// derived R1 share included. Null — the result hosts no zone (the panel stays closed).
    /// </summary>
    private static Rect? PreviewZone(BuildContext context, Func<LayoutState, LayoutState> move)
    {
        if (context.CenterRect is not { } center)
        {
            return null;
        }

        var result = DockIfFloating(move(context.State), context.DraggedId);
        return DockZonePreview.ZoneOf(result, context.DraggedId, center) is { } zone
            ? context.Space.FromWorkspaceRect(zone)
            : null;
    }

    /// <summary>The docking step of a floating-mode drop (TW-7.8): SetMode to the last internal mode; a no-op otherwise.</summary>
    private static LayoutState DockIfFloating(LayoutState state, string id)
    {
        var window = state.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, id, StringComparison.Ordinal));
        return window is not null && window.Mode.GetLayer() == ToolWindowLayer.Floating
            ? state.SetMode(id, window.LastInternalMode)
            : state;
    }

    /// <summary>
    /// The move of one stripe drop: the TW-1.5 mapping of the visible predecessor into the
    /// dense order — the moved window lands right after it, before any hidden run (E22) — with
    /// the guards of TW-5.17: a vanished subject or an identity drop yields no command, a
    /// vanished predecessor falls back to the slot end.
    /// </summary>
    private static Func<LayoutState, LayoutState> MoveCommit(string id, ToolWindowSlot slot, string? predecessorId) =>
        state =>
        {
            var dragged = state.ToolWindows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
            if (dragged is null)
            {
                return state;
            }

            if (string.Equals(predecessorId, id, StringComparison.Ordinal))
            {
                // The gap right after the dragged window's own icon: inserting after itself
                // is an identity (TW-1.5) — and the icon exists only in its own slot, so the
                // encoded target slot is the current one. Without this the predecessor lookup
                // below misses (the dragged window is excluded) and falls back to the slot end.
                return state;
            }

            var others = state.ToolWindows
                .Where(w => w.Slot == slot && !string.Equals(w.Id, id, StringComparison.Ordinal))
                .OrderBy(w => w.Order)
                .ToList();
            int index;
            if (predecessorId is null)
            {
                index = 0;
            }
            else
            {
                var at = others.FindIndex(w => string.Equals(w.Id, predecessorId, StringComparison.Ordinal));
                index = at < 0 ? int.MaxValue : at + 1;
            }

            if (dragged.Slot == slot && others.Count(w => w.Order < dragged.Order) == Math.Min(index, others.Count))
            {
                return state; // an identity drop is no command (TW-5.17)
            }

            return state.Move(id, slot, index);
        };
}

/// <summary>
/// Dock assist of a panel window's live move gesture (spec TW-7.7 extension; TW-7.1 for the
/// frameless Float window of Windows): while the header drags the window, the same stripe
/// drop targets of TW-5.17 light up so a release over a stripe docks the panel instead of
/// merely moving it. The window keeps moving live under the pointer — the pseudo-window and
/// the frameless OS window own their visuals, unlike the ghost of the slot gesture — so this
/// guide only hit-tests the stripe zones under the pointer, drives the target visuals —
/// the position-fill marker, the post-drop zone preview and the «Move to {slot}» hint, the
/// shared visual language of the stripe catalog (v0.26) — through the gesture visual, and
/// hands the resolved target back to the release. The commit itself is the target's own — the
/// docking <c>Move</c> + <c>SetMode(LastInternalMode)</c> sequence that already backs the
/// reverse icon/header drop of TW-7.8. Points arrive in the gesture space the targets were
/// built in (workspace coordinates on the overlay platform, screen coordinates on the
/// windowed one — see <see cref="BerthWorkspace.BeginPanelDockGuide"/>); the hit-test is
/// direct and geometric — occlusion is irrelevant: the stripes are the only targets (TW-7.7).
/// </summary>
internal sealed class PanelDockGuide
{
    private readonly IDragVisual _visual;
    private readonly List<DropTarget> _targets;

    public PanelDockGuide(IDragVisual visual, List<DropTarget> targets)
    {
        _visual = visual;
        _targets = targets;
    }

    /// <summary>The stripe zone under the gesture point, or null — a pure geometric hit-test.</summary>
    public DropTarget? Resolve(Point gesturePoint)
    {
        foreach (var target in _targets)
        {
            if (target.Contains(gesturePoint))
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Live target visuals while the panel moves: the marker, zone preview and hint of the
    /// stripe zone under the pointer, or nothing while suppressed (the <c>Ctrl</c> parking of
    /// TW-7.7) or off every stripe. The guide never shows a ghost — the window itself is the
    /// moving visual — so the hint anchors at the marker (v0.26).
    /// </summary>
    public void Update(Point gesturePoint, bool suppressed) =>
        _visual.UpdateTarget(suppressed ? null : Resolve(gesturePoint));

    /// <summary>Hides the target visuals — the gesture ended, cancelled or moved off the stripes.</summary>
    public void Hide() => _visual.UpdateTarget(null);
}
