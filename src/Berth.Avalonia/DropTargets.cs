using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// One drop target of a drag gesture (spec TW-5.17): the hit zone and the insertion marker in
/// workspace coordinates, plus the core command of the drop (ADR-0004). The commit factory is
/// written defensively against its input state — the catalog may predate an external state
/// change that arrived without a pointer move in between — and returns the state unchanged
/// when the drop is an identity or its precondition vanished (TW-5.17).
/// </summary>
internal sealed record DropTarget(Rect HitRect, Rect MarkerRect, Func<LayoutState, LayoutState> Commit);

/// <summary>
/// Catalog builder of the stripe drop targets (spec TW-5.17): the six slot segments of the two
/// stripes, receiving stripe icons and tool window headers alike — every drop reduces to one
/// Move (TW-5.7). Zones cover each stripe column entirely: insertion positions split a segment
/// at the midpoints of its neighbouring icons (= IDEA, AbstractDroppableStripe), free space is
/// divided between the adjacent segments, and an empty segment gets the zone of its zero
/// position — reachable by drag, unlike the reference. Positions are encoded as the visible
/// predecessor's id and mapped into the dense order at commit time (TW-1.5), so the mapping
/// survives state changes between the catalog build and the drop. Bottom segments grow upward
/// (TW-1.4) — their zones are mirrored accordingly.
/// </summary>
internal static class StripeDropTargets
{
    public static List<DropTarget> Build(BerthWorkspace workspace, LayoutState state, string draggedId)
    {
        var targets = new List<DropTarget>();
        foreach (var stripe in workspace.GetVisualDescendants().OfType<ToolWindowStripe>())
        {
            var isLeft = string.Equals(stripe.Name, "PART_LeftStripe", StringComparison.Ordinal);
            AddStripe(
                targets,
                workspace,
                state,
                draggedId,
                stripe,
                isLeft ? ToolWindowSide.Left : ToolWindowSide.Right,
                new ToolWindowSlot(ToolWindowSide.Bottom, isLeft ? ToolWindowGroup.Primary : ToolWindowGroup.Secondary));
        }

        return targets;
    }

    private static void AddStripe(
        List<DropTarget> targets,
        BerthWorkspace workspace,
        LayoutState state,
        string draggedId,
        ToolWindowStripe stripe,
        ToolWindowSide side,
        ToolWindowSlot bottomSlot)
    {
        if (BoundsIn(stripe, workspace) is not { } rect || rect.Height <= 0)
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
                if (window is null || BoundsIn(button, workspace) is not { } bounds)
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
                && BoundsIn(control, workspace) is { } chromeBounds)
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

        AddSegmentZones(targets, draggedId, rect, rect.Top, boundary,
            primary, new ToolWindowSlot(side, ToolWindowGroup.Primary));
        AddSegmentZones(targets, draggedId, rect, boundary, topEnd,
            secondary, new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        AddBottomZones(targets, draggedId, rect, freeMid, bottom, bottomSlot);
    }

    /// <summary>Insertion zones of a top-down segment: gaps at the midpoints of the icons (spec TW-5.17).</summary>
    private static void AddSegmentZones(
        List<DropTarget> targets,
        string draggedId,
        Rect stripeRect,
        double zoneStart,
        double zoneEnd,
        List<(string Id, Rect Rect)> buttons,
        ToolWindowSlot slot)
    {
        if (buttons.Count == 0)
        {
            Add(targets, stripeRect, zoneStart, zoneEnd,
                Math.Min(zoneStart + BerthMetrics.StripeButtonSize / 2, (zoneStart + zoneEnd) / 2),
                draggedId, slot, predecessorId: null);
            return;
        }

        var previous = zoneStart;
        for (var i = 0; i < buttons.Count; i++)
        {
            var mid = buttons[i].Rect.Center.Y;
            var anchor = i == 0
                ? buttons[0].Rect.Top - BerthMetrics.DropMarkerThickness
                : (buttons[i - 1].Rect.Bottom + buttons[i].Rect.Top) / 2;
            Add(targets, stripeRect, previous, mid, anchor,
                draggedId, slot, i == 0 ? null : buttons[i - 1].Id);
            previous = mid;
        }

        Add(targets, stripeRect, previous, zoneEnd,
            buttons[^1].Rect.Bottom + BerthMetrics.DropMarkerThickness,
            draggedId, slot, buttons[^1].Id);
    }

    /// <summary>
    /// Insertion zones of a bottom segment, which grows upward from the edge (spec TW-1.4):
    /// the topmost icon has the highest order, so the zone above it appends to the slot end and
    /// the zone at the edge inserts at position zero.
    /// </summary>
    private static void AddBottomZones(
        List<DropTarget> targets,
        string draggedId,
        Rect stripeRect,
        double zoneStart,
        List<(string Id, Rect Rect)> buttons,
        ToolWindowSlot slot)
    {
        var zoneEnd = stripeRect.Bottom;
        if (buttons.Count == 0)
        {
            Add(targets, stripeRect, zoneStart, zoneEnd,
                Math.Max(zoneEnd - BerthMetrics.StripeButtonSize / 2, (zoneStart + zoneEnd) / 2),
                draggedId, slot, predecessorId: null);
            return;
        }

        var previous = zoneStart;
        for (var i = 0; i < buttons.Count; i++)
        {
            var mid = buttons[i].Rect.Center.Y;
            var anchor = i == 0
                ? buttons[0].Rect.Top - BerthMetrics.DropMarkerThickness
                : (buttons[i - 1].Rect.Bottom + buttons[i].Rect.Top) / 2;
            Add(targets, stripeRect, previous, mid, anchor,
                draggedId, slot, buttons[i].Id);
            previous = mid;
        }

        Add(targets, stripeRect, previous, zoneEnd,
            buttons[^1].Rect.Bottom + BerthMetrics.DropMarkerThickness,
            draggedId, slot, predecessorId: null);
    }

    private static void Add(
        List<DropTarget> targets,
        Rect stripeRect,
        double zoneStart,
        double zoneEnd,
        double markerAnchor,
        string draggedId,
        ToolWindowSlot slot,
        string? predecessorId)
    {
        if (zoneEnd - zoneStart <= 0)
        {
            return; // a degenerate zone of a crowded stripe — the neighbours cover the space
        }

        var markerY = Math.Clamp(markerAnchor, zoneStart, zoneEnd - BerthMetrics.DropMarkerThickness);
        targets.Add(new DropTarget(
            new Rect(stripeRect.X, zoneStart, stripeRect.Width, zoneEnd - zoneStart),
            new Rect(stripeRect.X + 2, markerY, stripeRect.Width - 4, BerthMetrics.DropMarkerThickness),
            MoveCommit(draggedId, slot, predecessorId)));
    }

    /// <summary>
    /// The commit of one stripe drop: the TW-1.5 mapping of the visible predecessor into the
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

    private static Rect? BoundsIn(Control control, Visual ancestor)
    {
        var origin = control.TranslatePoint(default, ancestor);
        return origin is null ? null : new Rect(origin.Value, control.Bounds.Size);
    }
}
