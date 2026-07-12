using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// Drag gestures of stripe icons and tool window headers (spec TW-5.17; ADR-0004): the
/// threshold splitting clicks from drags, drop zones of the stripe segments with the TW-1.5
/// mapping (E22), the deferred header activation (TW-6.6), the pointer-path auto-hide
/// exemption (TW-6.2), gesture survival across external state changes and the no-trace
/// cancellation (Esc, release outside targets, vanished subject).
/// </summary>
public class DragTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static ToolWindowState Get(Window window, string id) =>
        St(window).ToolWindows.First(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    private static Rect StripeRect(Window window, string part) => BoundsIn(Part(window, part), window);

    /// <summary>Presses at <paramref name="from"/> and moves past the threshold to <paramref name="to"/> without releasing.</summary>
    private static void DragTo(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Release(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- the threshold: click vs drag (TW-5.17) ----

    [AvaloniaFact]
    public void TW_5_17_sub_threshold_movement_stays_a_click()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)] };
        var window = Show(state, Registry("a"));

        var start = Center(Button(window, "a"), window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 3, start.Y + 3));
        Release(window, new Point(start.X + 3, start.Y + 3));

        Assert.True(Get(window, "a").IsOpen); // the TW-5.4 toggle acted
    }

    [AvaloniaFact]
    public void TW_7_8_icon_release_outside_targets_floats_at_the_point()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)] };
        var window = Show(state, Registry("a"));

        var start = Center(Button(window, "a"), window);
        var dockCenter = new Point(400, 300); // the dock area — no drop target there
        DragTo(window, start, dockCenter);
        Release(window, dockCenter);

        // The take-out of TW-7.8: the closed panel opens floating at the release point with
        // the platform default size, activated — not the TW-5.4 toggle of a click.
        var a = Get(window, "a");
        Assert.Equal(ToolWindowMode.Float, a.Mode);
        Assert.True(a.IsOpen);
        Assert.Equal(new FloatingBounds(400, 300, 600, 400), a.FloatingBounds);
        Assert.Equal("a", St(window).ActiveToolWindowId);
        Assert.Equal(ToolWindowMode.DockPinned, a.LastInternalMode); // the way back (E27)
    }

    // ---- stripe drop zones (TW-5.17, TW-1.5) ----

    [AvaloniaFact]
    public void TW_5_17_icon_reorder_within_the_segment_commits_one_move()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var workspace = Workspace(window);
        var changes = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.Property == BerthWorkspace.StateProperty)
            {
                changes++;
            }
        };

        var start = Center(Button(window, "a"), window);
        var below = BoundsIn(Button(window, "b"), window);
        var target = new Point(below.Center.X, below.Bottom + 4); // the gap after b
        DragTo(window, start, target);
        Assert.Equal(0, changes); // pure visualization until the release (ADR-0004)
        Release(window, target);

        Assert.Equal(1, changes); // exactly one command per drop
        Assert.Equal(0, Get(window, "b").Order);
        Assert.Equal(1, Get(window, "a").Order);
    }

    [AvaloniaFact]
    public void TW_1_5_drop_between_visible_neighbours_lands_after_the_visible_predecessor() // E22
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("w1", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("w2", ToolWindowSide.Left, ToolWindowGroup.Primary, 1) with { IsIconVisible = false },
                Win("w3", ToolWindowSide.Left, ToolWindowGroup.Primary, 2) with { IsIconVisible = false },
                Win("w4", ToolWindowSide.Left, ToolWindowGroup.Primary, 3) with { IsIconVisible = false },
                Win("w5", ToolWindowSide.Left, ToolWindowGroup.Primary, 4),
                Win("x", ToolWindowSide.Left, ToolWindowGroup.Primary, 5),
            ],
        };
        var window = Show(state, Registry("w1", "w2", "w3", "w4", "w5", "x"));

        var start = Center(Button(window, "x"), window);
        var first = BoundsIn(Button(window, "w1"), window);
        var target = new Point(first.Center.X, first.Bottom + 2); // between visible w1 and w5
        DragTo(window, start, target);
        Release(window, target);

        // X lands right after w1, before the hidden run: [w1, X, w2, w3, w4, w5].
        Assert.Equal(0, Get(window, "w1").Order);
        Assert.Equal(1, Get(window, "x").Order);
        Assert.Equal(2, Get(window, "w2").Order);
        Assert.Equal(5, Get(window, "w5").Order);
    }

    [AvaloniaFact]
    public void TW_5_17_empty_stripe_offers_the_zero_positions_of_all_its_segments()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)] };
        var window = Show(state, Registry("a"));

        // The right stripe is empty: its top quarter is Right.Primary, the lower half belongs
        // to the bottom segment — Bottom.Secondary on the right stripe (TW-1.2).
        var stripe = StripeRect(window, "PART_RightStripe");
        var start = Center(Button(window, "a"), window);
        DragTo(window, start, new Point(stripe.Center.X, stripe.Top + 5));
        Release(window, new Point(stripe.Center.X, stripe.Top + 5));
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), Get(window, "a").Slot);

        var start2 = Center(Button(window, "a"), window);
        DragTo(window, start2, new Point(stripe.Center.X, stripe.Bottom - 5));
        Release(window, new Point(stripe.Center.X, stripe.Bottom - 5));
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Secondary), Get(window, "a").Slot);
    }

    [AvaloniaFact]
    public void TW_5_17_empty_primary_above_a_flush_secondary_stays_reachable()
    {
        // Both top-block icons sit in the Secondary segment: the Primary above them is empty
        // and their buttons start flush at the stripe top, yet the zero position of Primary
        // must stay a drop target (TW-5.17) — the reference keeps the hidden position-0
        // separator alive as the DnD cue (TW-1.3 tracing).
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("s1", ToolWindowSide.Left, ToolWindowGroup.Secondary, 0),
                Win("s2", ToolWindowSide.Left, ToolWindowGroup.Secondary, 1),
            ],
        };
        var window = Show(state, Registry("s1", "s2"));

        var stripe = StripeRect(window, "PART_LeftStripe");
        var start = Center(Button(window, "s2"), window);
        var topEdge = new Point(stripe.Center.X, stripe.Top + 2);
        DragTo(window, start, topEdge);
        Release(window, topEdge);

        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary), Get(window, "s2").Slot);
        Assert.Equal(0, Get(window, "s2").Order);
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary), Get(window, "s1").Slot);
    }

    [AvaloniaFact]
    public void TW_5_17_front_of_a_flush_secondary_stays_reachable_below_the_primary_band()
    {
        // The counterpart of the reserved top band: the zone between it and the first
        // icon's midpoint still reorders to the front of Secondary.
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("s1", ToolWindowSide.Left, ToolWindowGroup.Secondary, 0),
                Win("s2", ToolWindowSide.Left, ToolWindowGroup.Secondary, 1),
            ],
        };
        var window = Show(state, Registry("s1", "s2"));

        var stripe = StripeRect(window, "PART_LeftStripe");
        var first = BoundsIn(Button(window, "s1"), window);
        var band = (stripe.Top + first.Center.Y) / 2; // the reserved Primary-0 boundary
        var target = new Point(stripe.Center.X, (band + first.Center.Y) / 2);
        var start = Center(Button(window, "s2"), window);
        DragTo(window, start, target);
        Release(window, target);

        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Secondary), Get(window, "s2").Slot);
        Assert.Equal(0, Get(window, "s2").Order);
        Assert.Equal(1, Get(window, "s1").Order);
    }

    [AvaloniaFact]
    public void TW_1_4_bottom_segment_zones_grow_upward_from_the_edge()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("x", ToolWindowSide.Bottom, ToolWindowGroup.Primary, 0),
                Win("y", ToolWindowSide.Bottom, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "x", "y"));

        // The very edge of the left stripe is position zero of Bottom.Primary (TW-1.4).
        var stripe = StripeRect(window, "PART_LeftStripe");
        var start = Center(Button(window, "a"), window);
        var target = new Point(stripe.Center.X, stripe.Bottom - 2);
        DragTo(window, start, target);
        Release(window, target);

        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Bottom, ToolWindowGroup.Primary), Get(window, "a").Slot);
        Assert.Equal(0, Get(window, "a").Order);
        Assert.Equal(1, Get(window, "x").Order);
        Assert.Equal(2, Get(window, "y").Order);
    }

    [AvaloniaFact]
    public void TW_5_17_identity_drop_is_no_command()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var before = St(window);

        // Dropping a into the zone before itself is its current position. The point sits
        // just inside the stripe top — above it lies no zone at all (the outside take-out
        // of TW-7.8 since task 6.2).
        var first = BoundsIn(Button(window, "a"), window);
        var start = first.Center;
        var target = new Point(first.Center.X, first.Top + 1);
        DragTo(window, start, target);
        Release(window, target);

        Assert.Same(before, St(window));
    }

    [AvaloniaFact]
    public void TW_5_17_drop_right_after_own_icon_is_identity()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
                Win("c", ToolWindowSide.Left, ToolWindowGroup.Primary, 2),
            ],
        };
        var window = Show(state, Registry("a", "b", "c"));
        var before = St(window);

        // A short downward nudge: the gap between a's midpoint and b's midpoint encodes
        // «after a» — inserting the dragged window after itself is an identity (TW-1.5),
        // not a move to the slot end.
        var own = BoundsIn(Button(window, "a"), window);
        var target = new Point(own.Center.X, own.Bottom + 2);
        DragTo(window, own.Center, target);
        Release(window, target);

        Assert.Same(before, St(window));
    }

    // ---- cancellation without trace (TW-5.17, ср. DA-E22) ----

    [AvaloniaFact]
    public void TW_5_17_escape_cancels_and_the_release_does_nothing()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var before = St(window);

        var start = Center(Button(window, "a"), window);
        var below = BoundsIn(Button(window, "b"), window);
        var target = new Point(below.Center.X, below.Bottom + 4); // a valid drop zone
        DragTo(window, start, target);
        var drag = Workspace(window).Drag!;
        Assert.True(drag.GhostVisible); // the windowed ghost is an OS window (task 6.2)

        PressEscape(window);
        Assert.False(drag.GhostVisible); // the visuals are gone with the cancellation

        Release(window, target); // over the once-valid target
        Assert.Same(before, St(window)); // no command, no toggle — no trace
    }

    [AvaloniaFact]
    public void TW_5_17_ghost_and_marker_appear_only_while_dragging()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var drag = Workspace(window).Drag!;
        var marker = Part(window, "PART_DropMarker");
        Assert.False(drag.GhostVisible);
        Assert.False(marker.IsVisible);

        var start = Center(Button(window, "a"), window);
        var below = BoundsIn(Button(window, "b"), window);
        var overZone = new Point(below.Center.X, below.Bottom + 4);
        DragTo(window, start, overZone);
        Assert.True(drag.GhostVisible); // the windowed ghost is an OS window (task 6.2)
        Assert.True(marker.IsVisible);

        window.MouseMove(new Point(400, 300)); // off every target
        Dispatcher.UIThread.RunJobs();
        Assert.True(drag.GhostVisible);
        Assert.False(marker.IsVisible);

        PressEscape(window); // cancel: the visuals-only test must not take the panel out
        Release(window, new Point(400, 300));
        Assert.False(drag.GhostVisible);
    }

    // ---- the deferred header activation (TW-6.6) ----

    [AvaloniaFact]
    public void TW_6_6_header_click_activates_on_the_release()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }] };
        var window = Show(state, Registry("a"));

        var header = Part(Decorator(window, "a"), "PART_Header");
        var point = new Point(BoundsIn(header, window).X + 40, BoundsIn(header, window).Center.Y);
        window.MouseDown(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(St(window).ActiveToolWindowId); // the press alone no longer activates

        Release(window, point);
        Assert.Equal("a", St(window).ActiveToolWindowId); // the click completes on the release
    }

    [AvaloniaFact]
    public void TW_6_6_cancelled_header_drag_leaves_no_activation()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }] };
        var window = Show(state, Registry("a"));
        var before = St(window);

        var header = Part(Decorator(window, "a"), "PART_Header");
        var start = new Point(BoundsIn(header, window).X + 40, BoundsIn(header, window).Center.Y);
        var outside = new Point(400, 300);
        DragTo(window, start, outside);
        PressEscape(window); // cancellation: Esc or a lost capture (TW-5.17, task 6.2)
        Release(window, outside);

        Assert.Same(before, St(window));
        Assert.Null(St(window).ActiveToolWindowId);
    }

    [AvaloniaFact]
    public void TW_5_17_header_drop_on_a_stripe_moves_without_activation()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }] };
        var window = Show(state, Registry("a"));

        var header = Part(Decorator(window, "a"), "PART_Header");
        var start = new Point(BoundsIn(header, window).X + 40, BoundsIn(header, window).Center.Y);
        var stripe = StripeRect(window, "PART_RightStripe");
        var target = new Point(stripe.Center.X, stripe.Top + 5);
        DragTo(window, start, target);
        Release(window, target);

        var moved = Get(window, "a");
        Assert.Equal(new ToolWindowSlot(ToolWindowSide.Right, ToolWindowGroup.Primary), moved.Slot);
        Assert.True(moved.IsOpen); // Move keeps openness (TW-5.7)
        Assert.Null(St(window).ActiveToolWindowId); // a stripe drop does not activate (= IDEA)
    }

    // ---- gesture vs external state changes (TW-5.17) ----

    [AvaloniaFact]
    public void TW_5_17_external_change_mid_drag_rebuilds_targets_and_the_gesture_continues()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
                Win("c", ToolWindowSide.Left, ToolWindowGroup.Primary, 2),
            ],
        };
        var window = Show(state, Registry("a", "b", "c"));
        var workspace = Workspace(window);

        var start = Center(Button(window, "a"), window);
        DragTo(window, start, new Point(400, 300));

        // An external change mid-gesture: b's icon disappears, the stripe re-projects.
        workspace.State = workspace.State!.SetIconVisible("b", false);
        Dispatcher.UIThread.RunJobs();

        // The gesture continues over the updated geometry: drop after the now-adjacent c.
        var last = BoundsIn(Button(window, "c"), window);
        var target = new Point(last.Center.X, last.Bottom + 4);
        window.MouseMove(target);
        Dispatcher.UIThread.RunJobs();
        Release(window, target);

        Assert.Equal(2, Get(window, "a").Order); // after c: [b, c, a] in dense order
        Assert.Equal(0, Get(window, "b").Order);
        Assert.Equal(1, Get(window, "c").Order);
    }

    [AvaloniaFact]
    public void TW_5_17_vanished_subject_cancels_the_gesture()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary, 0),
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var workspace = Workspace(window);

        var start = Center(Button(window, "a"), window);
        var below = BoundsIn(Button(window, "b"), window);
        var target = new Point(below.Center.X, below.Bottom + 4);
        DragTo(window, start, target);

        var external = workspace.State! with
        {
            ToolWindows = [.. workspace.State!.ToolWindows.Where(w => !string.Equals(w.Id, "a", StringComparison.Ordinal))],
        };
        workspace.State = external;
        Dispatcher.UIThread.RunJobs();

        Release(window, target); // over a once-valid zone
        Assert.Same(external, St(window)); // the gesture died with its subject — no command
    }

    // ---- auto-hide integration (TW-6.2, TW-6.1) ----

    [AvaloniaFact]
    public void TW_6_2_drag_release_is_not_a_click_for_auto_hide()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Primary, 0) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                },
                Win("b", ToolWindowSide.Left, ToolWindowGroup.Primary, 1),
            ],
        };
        var window = Show(state, Registry("u", "b"));

        // A drag of b's icon released over the dock area: without the exemption the pointer
        // path would close u — the release lands outside the unpinned window.
        var start = Center(Button(window, "b"), window);
        var outside = new Point(400, 300);
        DragTo(window, start, outside);
        Release(window, outside);

        Assert.True(Get(window, "u").IsOpen);
    }

    [AvaloniaFact]
    public void TW_6_1_drag_from_the_unpinned_window_itself_does_not_close_it()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("u", ToolWindowSide.Left, ToolWindowGroup.Primary) with
                {
                    IsOpen = true,
                    Mode = ToolWindowMode.DockUnpinned,
                    LastInternalMode = ToolWindowMode.DockUnpinned,
                },
            ],
        };
        var window = Show(state, Registry("u"));

        var header = Part(Decorator(window, "u"), "PART_Header");
        var start = new Point(BoundsIn(header, window).X + 40, BoundsIn(header, window).Center.Y);
        var outside = new Point(400, 300);
        DragTo(window, start, outside);
        PressEscape(window);
        Release(window, outside); // the cancelled gesture is not an outside click

        Assert.True(Get(window, "u").IsOpen);
        Assert.Equal(ToolWindowMode.DockUnpinned, Get(window, "u").Mode); // cancelled: no take-out
    }
}

