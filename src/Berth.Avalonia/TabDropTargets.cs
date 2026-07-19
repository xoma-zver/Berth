using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Catalog builder of the tab drop targets (DA-9.7): the strips, edge wedges and centers of
/// every materialized tab group the dragged tab may enter, in every window of the workspace.
/// Priority is the list order: strip insertion zones, then the diagonal edge wedges limited
/// to <see cref="BerthMetrics.SplitWedgeRatio"/> of the group's extent, then the group
/// centers. canHost decides which trees offer zones at all (INV-D5, DA-8.2): the tree
/// currently hosting the tab, the dock-area hosts, and the confirmed owner panel's tree;
/// trees of foreign panels yield nothing, and a closed panel is simply not materialized.
/// Commits are encoded by tab ids and re-resolved against the live state at drop time with
/// guards, so they survive external state changes between the catalog build and the release;
/// an identity drop does nothing at all (DA-E40). Completed drops mirror the tab menu items:
/// the command sequence, then activation for a cross-host move, then the focus transfer.
/// </summary>
internal static class TabDropTargets
{
    public static List<DropTarget> Build(
        BerthWorkspace workspace, LayoutState state, string draggedId, DropZoneSpace space)
    {
        var targets = new List<DropTarget>();
        if (FindGroup(state, draggedId) is not { } source)
        {
            return targets; // the subject is not in a materialized host — nothing to offer
        }

        var ownerPanel = ConfirmedOwnerPanel(workspace.Registry, draggedId);
        bool Allowed(string? panelId) =>
            panelId is null
            || string.Equals(panelId, source.PanelId, StringComparison.Ordinal)
            || string.Equals(panelId, ownerPanel, StringComparison.Ordinal);

        AddStripZones(targets, workspace, draggedId, space, Allowed);

        var wedges = new List<DropTarget>();
        var centers = new List<DropTarget>();
        foreach (var root in space.Roots)
        {
            foreach (var view in root.GetVisualDescendants().OfType<TabGroupView>())
            {
                if (!view.IsEffectivelyVisible
                    || !Allowed(view.Context.PanelId)
                    || space.RectOf(view) is not { } rect
                    || rect.Width <= 0
                    || rect.Height <= 0)
                {
                    continue;
                }

                var key = space.KeyOf(view);
                if (view.Tabs.Count == 0)
                {
                    // The empty root group (DA-2.3) offers only its center; there is nothing
                    // to split (DA-9.7).
                    centers.Add(new DropTarget(rect, rect, RootCommit(draggedId, view.Context.PanelId))
                    {
                        AreaMarker = true,
                        WindowKey = key,
                    });
                    continue;
                }

                var anchor = view.Tabs.FirstOrDefault(
                    t => !string.Equals(t, draggedId, StringComparison.Ordinal)) ?? draggedId;
                wedges.Add(Wedge(rect, SplitDirection.Left, draggedId, anchor, key));
                wedges.Add(Wedge(rect, SplitDirection.Right, draggedId, anchor, key));
                wedges.Add(Wedge(rect, SplitDirection.Up, draggedId, anchor, key));
                wedges.Add(Wedge(rect, SplitDirection.Down, draggedId, anchor, key));
                centers.Add(new DropTarget(rect, rect, CenterCommit(draggedId, anchor))
                {
                    AreaMarker = true,
                    WindowKey = key,
                });
            }
        }

        targets.AddRange(wedges);
        targets.AddRange(centers);
        return targets;
    }

    // ---- strip insertion zones ----

    /// <summary>
    /// Insertion zones of every allowed strip — the group bars and the decorator header rows
    /// hosting a panel root group's strip (TW-9.5) — across every root of the gesture space.
    /// Zone boundaries are the midpoints of the headers (= IDEA); the first and last zones
    /// extend to the strip band's edges. Positions are encoded as the predecessor header's tab
    /// id (null — the group front) and mapped at commit time (DA-5.4).
    /// </summary>
    private static void AddStripZones(
        List<DropTarget> targets,
        BerthWorkspace workspace,
        string draggedId,
        DropZoneSpace space,
        Func<string?, bool> allowed)
    {
        var strips = new Dictionary<Control, List<(DockTabHeader Header, Rect Rect)>>();
        foreach (var root in space.Roots)
        {
            foreach (var header in root.GetVisualDescendants().OfType<DockTabHeader>())
            {
                if (!header.IsEffectivelyVisible
                    || !allowed(header.Context.PanelId)
                    || space.RectOf(header) is not { } rect
                    || StripBandOf(header) is not { } band)
                {
                    continue;
                }

                if (!strips.TryGetValue(band, out var entries))
                {
                    entries = [];
                    strips[band] = entries;
                }

                entries.Add((header, rect));
            }
        }

        // Band views of the catalog (stage 2 of DA-9.7 v0.18): headers sorted by X in
        // gesture coordinates. The donor band — the one holding the dragged header — travels
        // with every strip zone, so a cross-strip hover collapses it too; an external
        // re-projection rebuilds the views from the fresh leaf chrome, which reapplies the
        // overrides (the section 12 contract of tool-windows).
        var bands = new List<(StripBandView View, object? Key)>();
        StripBandView? donorBand = null;
        foreach (var (band, headers) in strips)
        {
            if (space.RectOf(band) is not { } bandRect || bandRect.Width <= 0)
            {
                continue;
            }

            headers.Sort((a, b) => a.Rect.X.CompareTo(b.Rect.X));
            var view = new StripBandView(
                band, bandRect, [.. headers.Select(h => new StripHeaderView(h.Header, h.Rect))]);
            bands.Add((view, space.KeyOf(band)));
            if (headers.Any(h => string.Equals(h.Header.TabId, draggedId, StringComparison.Ordinal)))
            {
                donorBand = view;
            }
        }

        foreach (var (view, key) in bands)
        {
            var headers = view.Headers;
            var anchor = headers.FirstOrDefault(
                    h => !string.Equals(h.Header.TabId, draggedId, StringComparison.Ordinal)).Header?.TabId
                ?? draggedId;
            // The receiver collapses the dragged header itself; the donor rides along only
            // for a cross-strip hover (DA-9.7 v0.18).
            var donor = ReferenceEquals(donorBand, view) ? null : donorBand;

            var previous = view.Rect.Left;
            for (var i = 0; i < headers.Length; i++)
            {
                var mid = headers[i].Rect.Center.X;
                var markerX = i == 0
                    ? headers[0].Rect.Left
                    : (headers[i - 1].Rect.Right + headers[i].Rect.Left) / 2;
                AddZone(targets, view, previous, mid, markerX,
                    draggedId, anchor, i == 0 ? null : headers[i - 1].Header.TabId, donor, key);
                previous = mid;
            }

            AddZone(targets, view, previous, view.Rect.Right, headers[^1].Rect.Right,
                draggedId, anchor, headers[^1].Header.TabId, donor, key);
        }
    }

    private static void AddZone(
        List<DropTarget> targets,
        StripBandView receiver,
        double zoneStart,
        double zoneEnd,
        double markerAnchor,
        string draggedId,
        string anchor,
        string? predecessorId,
        StripBandView? donor,
        object? windowKey)
    {
        if (zoneEnd - zoneStart <= 0)
        {
            return; // a degenerate zone of a crowded strip — the neighbours cover the space
        }

        var bandRect = receiver.Rect;
        var markerX = Math.Clamp(markerAnchor, bandRect.Left, bandRect.Right - BerthMetrics.DropMarkerThickness);
        targets.Add(new DropTarget(
            new Rect(zoneStart, bandRect.Y, zoneEnd - zoneStart, bandRect.Height),
            // The marker rect stays the stage-1 insertion line — the fallback of a gesture
            // without a captured source-header width, whose preview has no ghost to size.
            new Rect(markerX, bandRect.Y + 2, BerthMetrics.DropMarkerThickness, bandRect.Height - 4),
            StripCommit(draggedId, anchor, predecessorId))
        {
            WindowKey = windowKey,
            StripPreview = new StripReorderPreview(draggedId, receiver, predecessorId, donor),
        });
    }

    /// <summary>The band a strip's headers live in: the group's bar or the decorator header row.</summary>
    private static Control? StripBandOf(DockTabHeader header)
    {
        for (var node = header.GetVisualParent(); node is not null; node = node.GetVisualParent())
        {
            if (node is Control control && control.Name is "PART_TabStrip" or "PART_HeaderTabs")
            {
                return control;
            }
        }

        return null;
    }

    // ---- wedges ----

    /// <summary>
    /// One edge wedge of a group (DA-9.7): the coarse zone is the band of
    /// <see cref="BerthMetrics.SplitWedgeRatio"/> depth along the edge, refined by the
    /// diagonal condition — the point's nearest edge in normalized coordinates must be this
    /// one (= the reference's getDropSideFor). The marker previews the result: the half of
    /// the group on the direction side (= the reference's drop preview; owner decision).
    /// </summary>
    private static DropTarget Wedge(
        Rect rect, SplitDirection direction, string draggedId, string anchor, object? windowKey)
    {
        var depth = BerthMetrics.SplitWedgeRatio;
        var hit = direction switch
        {
            SplitDirection.Left => new Rect(rect.X, rect.Y, rect.Width * depth, rect.Height),
            SplitDirection.Right => new Rect(rect.Right - (rect.Width * depth), rect.Y, rect.Width * depth, rect.Height),
            SplitDirection.Up => new Rect(rect.X, rect.Y, rect.Width, rect.Height * depth),
            _ => new Rect(rect.X, rect.Bottom - (rect.Height * depth), rect.Width, rect.Height * depth),
        };
        var marker = direction switch
        {
            SplitDirection.Left => new Rect(rect.X, rect.Y, rect.Width / 2, rect.Height),
            SplitDirection.Right => new Rect(rect.X + (rect.Width / 2), rect.Y, rect.Width / 2, rect.Height),
            SplitDirection.Up => new Rect(rect.X, rect.Y, rect.Width, rect.Height / 2),
            _ => new Rect(rect.X, rect.Y + (rect.Height / 2), rect.Width, rect.Height / 2),
        };
        return new DropTarget(hit, marker, WedgeCommit(draggedId, anchor, direction), point =>
        {
            var u = (point.X - rect.X) / rect.Width;
            var v = (point.Y - rect.Y) / rect.Height;
            var distance = direction switch
            {
                SplitDirection.Left => u,
                SplitDirection.Right => 1 - u,
                SplitDirection.Up => v,
                _ => 1 - v,
            };
            return distance <= u && distance <= 1 - u && distance <= v && distance <= 1 - v;
        })
        {
            AreaMarker = true,
            WindowKey = windowKey,
        };
    }

    // ---- commits (all re-resolved against the live state, TW-5.17) ----

    /// <summary>
    /// The strip drop: one MoveTab into the receiving group at the encoded position (DA-5.4),
    /// with the DA-9.7 follow-ups. Inserting at the current position — including right after
    /// the dragged tab's own header — is an identity: nothing runs (DA-E40).
    /// </summary>
    private static Action<BerthWorkspace> StripCommit(string draggedId, string anchor, string? predecessorId) =>
        workspace =>
        {
            if (Resolve(workspace, draggedId, anchor) is not { } drop)
            {
                return;
            }

            var tabs = drop.Receiver.Group.Tabs;
            if (string.Equals(predecessorId, draggedId, StringComparison.Ordinal)
                && tabs.Contains(draggedId, StringComparer.Ordinal))
            {
                return; // the gap right after itself is its current position (DA-E40)
            }

            int index;
            if (predecessorId is null)
            {
                index = 0;
            }
            else
            {
                var at = IndexAmongOthers(tabs, predecessorId, draggedId);
                index = at < 0 ? int.MaxValue : at + 1;
            }

            if (drop.SameGroup)
            {
                var others = tabs.Length - 1; // the receiver holds the dragged tab
                if (Math.Min(index, others) == tabs.IndexOf(draggedId))
                {
                    return; // identity: the resulting position is the current one (DA-E40)
                }
            }

            CommitMove(workspace, drop, draggedId, index);
        };

    /// <summary>The group center drop: MoveTab to the end (DA-9.7); the own group's center is an identity (DA-E40, owner decision).</summary>
    private static Action<BerthWorkspace> CenterCommit(string draggedId, string anchor) =>
        workspace =>
        {
            if (Resolve(workspace, draggedId, anchor) is not { } drop || drop.SameGroup)
            {
                return;
            }

            CommitMove(workspace, drop, draggedId, int.MaxValue);
        };

    /// <summary>
    /// The center drop of an empty root group (DA-2.3): MoveTab addressed by the host root
    /// (DA-1.3). A group grown around the root meanwhile still receives at its end; a root
    /// turned split falls back to nothing — the catalog went stale (TW-5.17).
    /// </summary>
    private static Action<BerthWorkspace> RootCommit(string draggedId, string? panelId) =>
        workspace =>
        {
            if (workspace.State is not { } state
                || workspace.Registry is not { } registry
                || FindGroup(state, draggedId) is not { } source)
            {
                return;
            }

            TabGroupNode? rootGroup = panelId is null
                ? state.DockArea.Root as TabGroupNode
                : state.ToolWindows.FirstOrDefault(
                    w => string.Equals(w.Id, panelId, StringComparison.Ordinal))?.ContentTree as TabGroupNode;
            if (rootGroup is null || !OwnershipAllows(registry, draggedId, source.PanelId, panelId))
            {
                return;
            }

            if (rootGroup.Tabs.Contains(draggedId, StringComparer.Ordinal))
            {
                return; // the own group's center is an identity (DA-E40)
            }

            var target = panelId is null ? DockGroupRef.HostRoot(DockHost.MainWindow) : DockGroupRef.PanelRoot(panelId);
            workspace.Execute(s => s.MoveTab(draggedId, target, int.MaxValue, registry));
            var receiver = new TabSite(panelId, panelId is null ? -1 : PanelDockIndex, rootGroup);
            FollowUp(workspace, new DropSite(source, receiver, SameGroup: false), draggedId);
        };

    /// <summary>
    /// The wedge drop (DA-9.7): SplitTab for the own group's edge, the MoveTab + SplitTab
    /// composition for a foreign one (DA-E41) — each command its own funnel call, like the
    /// 4.1 menu items (the atomic variant stays deferred, document-area section 11).
    /// </summary>
    private static Action<BerthWorkspace> WedgeCommit(string draggedId, string anchor, SplitDirection direction) =>
        workspace =>
        {
            if (Resolve(workspace, draggedId, anchor) is not { } drop)
            {
                return;
            }

            if (drop.SameGroup)
            {
                workspace.Execute(s => s.SplitTab(draggedId, direction));
                workspace.FocusTab(draggedId);
                return;
            }

            var registry = workspace.Registry!;
            workspace.Execute(s => s.MoveTab(draggedId, DockGroupRef.AtTab(anchor), int.MaxValue, registry));
            workspace.Execute(s => s.SplitTab(draggedId, direction));
            FollowUp(workspace, drop, draggedId);
        };

    // ---- shared resolution and follow-ups ----

    /// <summary>Sentinel dock index of a panel site: hosts are compared as (PanelId, DockIndex) pairs.</summary>
    private const int PanelDockIndex = -2;

    /// <summary>
    /// Site of a tab: the hosting tree — a panel (by id) or a dock host (the main window is
    /// index −1, a document window its list index) — and the containing group. The dock index
    /// makes moves between dock hosts recognizably cross-host (DA-5.4, task 6.2).
    /// </summary>
    private readonly record struct TabSite(string? PanelId, int DockIndex, TabGroupNode Group);

    private sealed record DropSite(TabSite Source, TabSite Receiver, bool SameGroup)
    {
        public bool CrossHost =>
            !string.Equals(Source.PanelId, Receiver.PanelId, StringComparison.Ordinal)
            || Source.DockIndex != Receiver.DockIndex;
    }

    /// <summary>
    /// Re-resolves the drop against the live state (TW-5.17): both the dragged tab and the
    /// anchor must still live in materialized hosts, and a cross-host move into a panel must
    /// still be confirmed by ownership (INV-D5) — the claims may have changed since the
    /// catalog was built. Null means the drop does nothing.
    /// </summary>
    private static DropSite? Resolve(BerthWorkspace workspace, string draggedId, string anchor)
    {
        if (workspace.State is not { } state || workspace.Registry is not { } registry)
        {
            return null;
        }

        if (FindGroup(state, draggedId) is not { } source || FindGroup(state, anchor) is not { } receiver)
        {
            return null;
        }

        if (!OwnershipAllows(registry, draggedId, source.PanelId, receiver.PanelId))
        {
            return null;
        }

        return new DropSite(source, receiver, ReferenceEquals(source.Group, receiver.Group));
    }

    private static void CommitMove(BerthWorkspace workspace, DropSite drop, string draggedId, int index)
    {
        var registry = workspace.Registry!;
        var anchor = drop.Receiver.Group.Tabs[0];
        workspace.Execute(s => s.MoveTab(draggedId, DockGroupRef.AtTab(anchor), index, registry));
        FollowUp(workspace, drop, draggedId);
    }

    /// <summary>
    /// The follow-ups of a completed drop, mirroring the 4.1 menu items (DA-9.7): a cross-host
    /// move — between a panel and a dock host, or between dock hosts (task 6.2) — is activated
    /// with ActivateTab (DA-5.4 — activity follows the move), and keyboard focus transfers
    /// into the dropped tab's content (DA-6.4); within one host the command already made the
    /// tab active and only the focus follows.
    /// </summary>
    private static void FollowUp(BerthWorkspace workspace, DropSite drop, string draggedId)
    {
        if (drop.CrossHost)
        {
            workspace.Execute(s => DockTrees.LayoutContainsTab(s, draggedId) ? s.ActivateTab(draggedId) : s);
        }

        workspace.FocusTab(draggedId);
    }

    /// <summary>Site of the tab in any materialized tree: the main window, a document window or a panel tree.</summary>
    private static TabSite? FindGroup(LayoutState state, string tabId)
    {
        foreach (var group in DockTrees.Groups(state.DockArea.Root))
        {
            if (group.Tabs.Contains(tabId, StringComparer.Ordinal))
            {
                return new TabSite(PanelId: null, DockIndex: -1, group);
            }
        }

        for (var i = 0; i < state.DockArea.Windows.Length; i++)
        {
            foreach (var group in DockTrees.Groups(state.DockArea.Windows[i].Root))
            {
                if (group.Tabs.Contains(tabId, StringComparer.Ordinal))
                {
                    return new TabSite(PanelId: null, DockIndex: i, group);
                }
            }
        }

        foreach (var panel in state.ToolWindows)
        {
            foreach (var group in DockTrees.Groups(panel.ContentTree))
            {
                if (group.Tabs.Contains(tabId, StringComparer.Ordinal))
                {
                    return new TabSite(panel.Id, PanelDockIndex, group);
                }
            }
        }

        return null;
    }

    /// <summary>The confirmed tool window owner of the tab, or null: a document, an unclaimed id, or a conflicted claim (TW-9.11).</summary>
    private static string? ConfirmedOwnerPanel(ToolWindowRegistry? registry, string tabId)
    {
        if (registry is null)
        {
            return null;
        }

        try
        {
            return registry.ResolveTabOwner(tabId)?.ToolWindowId;
        }
        catch (InvalidOperationException)
        {
            return null; // a conflicted claim confirms nothing (TW-9.11)
        }
    }

    /// <summary>canHost of the commit (INV-D5, DA-8.2): a move into a panel from another host needs that panel confirmed as the owner.</summary>
    private static bool OwnershipAllows(
        ToolWindowRegistry registry, string draggedId, string? sourcePanelId, string? receiverPanelId)
    {
        if (receiverPanelId is null || string.Equals(sourcePanelId, receiverPanelId, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(ConfirmedOwnerPanel(registry, draggedId), receiverPanelId, StringComparison.Ordinal);
    }

    /// <summary>Index of a tab among the receiver's tabs with the dragged one taken out — the index space of MoveTab (DA-5.4).</summary>
    private static int IndexAmongOthers(ImmutableArray<string> tabs, string tabId, string draggedId)
    {
        var index = 0;
        foreach (var tab in tabs)
        {
            if (string.Equals(tab, draggedId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(tab, tabId, StringComparison.Ordinal))
            {
                return index;
            }

            index++;
        }

        return -1;
    }
}
