using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using static Berth.Controls.Tests.WorkspaceTestSupport;

namespace Berth.Controls.Tests;

/// <summary>
/// The rich visual language of drag gestures, stage 1 (spec TW-5.17 v0.26, DA-9.7 v0.17):
/// ghost mode switching — the light face (the stripe icon face of a panel, the title chip of
/// a tab) over any target, the content miniature outside every target and only for a subject
/// whose view was built at the gesture start; the «Move to {slot}» hint of stripe targets;
/// and the post-drop zone preview read off the drop's command sequence run in memory —
/// including the derived R1 pair share (TW-2.7). Everything observes through internal seams;
/// the miniature bitmap itself is not renderable headless (owner's live run) — a stub image
/// exercises the switching logic instead.
/// </summary>
public class DragVisualTests
{
    private static BerthWorkspace Workspace(Window window) => (BerthWorkspace)window.Content!;

    private static LayoutState St(Window window) => Workspace(window).State!;

    private static DragController Drag(Window window) => Workspace(window).Drag!;

    private static Rect StripeRect(Window window, string part) => BoundsIn(Part(window, part), window);

    /// <summary>The zone preview rectangle exactly as set — rendered bounds would add layout rounding.</summary>
    private static Rect SetRect(Control zone) =>
        new(Canvas.GetLeft(zone), Canvas.GetTop(zone), zone.Width, zone.Height);

    private static TabGroupNode Group(string? active, params string[] tabs) =>
        new() { Tabs = [.. tabs], ActiveTabId = active };

    private static LayoutState DockState(TabTreeNode root, string? current) =>
        LayoutState.Empty with { DockArea = new DockAreaState { Root = root, CurrentTabId = current } };

    /// <summary>Presses at <paramref name="from"/> and moves past the threshold to <paramref name="to"/> without releasing.</summary>
    private static void DragTo(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    private static void MoveTo(Window window, Point to)
    {
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Ends a visuals-only gesture without a command: cancel, then release (TW-5.17).</summary>
    private static void CancelAndRelease(Window window, Point at)
    {
        PressEscape(window);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A stub image for the miniature seam: headless cannot render offscreen (owner's live run).</summary>
    private sealed class FakeImage : IImage
    {
        public Size Size => new(120, 90);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    // ---- the slot hint and the position-fill marker (TW-5.17 v0.26) ----

    [AvaloniaFact]
    public void TW_5_17_stripe_target_shows_the_slot_hint_and_no_zone_for_a_closed_panel()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)] };
        var window = Show(state, Registry("a"));

        var stripe = StripeRect(window, "PART_RightStripe");
        var target = new Point(stripe.Center.X, stripe.Top + 5); // the empty Right.Primary zone
        DragTo(window, Center(Button(window, "a"), window), target);

        // The hint names the slot and is shown exactly over the target (v0.26).
        Assert.Equal("Move to Right Top", Drag(window).HintText);
        Assert.True(Part(window, "PART_DropMarker").IsVisible);
        // A closed panel stays closed after the drop — the in-memory result hosts no zone,
        // so no preview shows: the preview agrees with the outcome by construction.
        Assert.False(Part(window, "PART_DropZonePreview").IsVisible);

        MoveTo(window, new Point(400, 300)); // outside every target — no hint there (v0.26)
        Assert.Null(Drag(window).HintText);

        CancelAndRelease(window, new Point(400, 300));
    }

    [AvaloniaFact]
    public void TW_5_17_zone_preview_matches_the_in_memory_drop_result()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, Registry("a"));
        var workspace = Workspace(window);

        var stripe = StripeRect(window, "PART_RightStripe");
        var target = new Point(stripe.Center.X, stripe.Top + 5); // → Move(a, Right.Primary, 0)
        DragTo(window, Center(Button(window, "a"), window), target);

        // The preview equals the geometry of the drop result: the open panel docks on the
        // right and takes the side's weight of the docked center area (TW-2.5).
        var zone = Part(window, "PART_DropZonePreview");
        Assert.True(zone.IsVisible);
        var center = workspace.DockedAreaRect()!.Value;
        var expectedWidth = center.Width * St(window).Right.Weight;
        var bounds = SetRect(zone);
        Assert.Equal(center.Right - expectedWidth, bounds.X, 6);
        Assert.Equal(center.Y, bounds.Y, 6);
        Assert.Equal(expectedWidth, bounds.Width, 6);
        Assert.Equal(center.Height, bounds.Height, 6);

        CancelAndRelease(window, target);
        Assert.False(zone.IsVisible); // the cancelled gesture leaves no visuals
    }

    [AvaloniaFact]
    public void TW_2_7_pair_zone_preview_derives_the_r1_share()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows =
            [
                Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true, PairRatio = 0.75 },
                Win("b", ToolWindowSide.Right, ToolWindowGroup.Primary) with { IsOpen = true, PairRatio = 0.25 },
            ],
        };
        var window = Show(state, Registry("a", "b"));
        var workspace = Workspace(window);

        // Drop b into Left.Secondary: the in-memory result forms the pair a/b, whose ratio
        // derives by rule R1 — 0.75/(0.75+0.25) — so b's preview is the bottom quarter of
        // the left pane (TW-2.7, TW-2.3).
        var stripe = StripeRect(window, "PART_LeftStripe");
        var ownIcon = BoundsIn(Button(window, "a"), window);
        var target = new Point(stripe.Center.X, ownIcon.Bottom + 40); // the Secondary segment zone
        DragTo(window, Center(Button(window, "b"), window), target);

        Assert.Equal("Move to Left Bottom", Drag(window).HintText);
        var zone = Part(window, "PART_DropZonePreview");
        Assert.True(zone.IsVisible);
        var center = workspace.DockedAreaRect()!.Value;
        var paneWidth = center.Width * St(window).Left.Weight;
        var bounds = SetRect(zone);
        Assert.Equal(center.X, bounds.X, 6);
        Assert.Equal(center.Y + (center.Height * 0.75), bounds.Y, 6);
        Assert.Equal(paneWidth, bounds.Width, 6);
        Assert.Equal(center.Height * 0.25, bounds.Height, 6);

        CancelAndRelease(window, target);
    }

    // ---- ghost mode switching (TW-5.17 v0.26) ----

    [AvaloniaFact]
    public void TW_5_17_panel_ghost_is_light_over_a_target_and_a_miniature_outside()
    {
        var state = LayoutState.Empty with
        {
            ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary) with { IsOpen = true }],
        };
        var window = Show(state, Registry("a"));
        Drag(window).MiniatureRenderer = _ => new FakeImage(); // the headless seam (v0.26)

        var start = Center(Button(window, "a"), window);
        DragTo(window, start, new Point(start.X, start.Y + 10)); // the gap after its own icon — a target

        Assert.True(Drag(window).GhostVisible);
        Assert.False(Drag(window).GhostShowsMiniature); // over a target — always the light face

        MoveTo(window, new Point(400, 300)); // outside every target
        Assert.True(Drag(window).GhostShowsMiniature); // the take-out herald (TW-7.8)

        MoveTo(window, new Point(start.X, start.Y + 10)); // back over the target
        Assert.False(Drag(window).GhostShowsMiniature);

        CancelAndRelease(window, new Point(start.X, start.Y + 10));
    }

    [AvaloniaFact]
    public void TW_5_17_invisible_panel_keeps_the_icon_face_outside_targets()
    {
        var state = LayoutState.Empty with { ToolWindows = [Win("a", ToolWindowSide.Left, ToolWindowGroup.Primary)] };
        var window = Show(state, Registry("a"));
        var captured = false;
        Drag(window).MiniatureRenderer = _ =>
        {
            captured = true;
            return new FakeImage();
        };

        // The closed panel has no built view at the gesture start: no capture happens at all
        // and the ghost keeps the icon face everywhere (v0.26).
        var start = Center(Button(window, "a"), window);
        DragTo(window, start, new Point(400, 300));

        Assert.True(Drag(window).GhostVisible);
        Assert.False(Drag(window).GhostShowsMiniature);
        Assert.False(captured);

        CancelAndRelease(window, new Point(400, 300));
    }

    // ---- the tab side (DA-9.7 v0.17) ----

    [AvaloniaFact]
    public void DA_9_7_tab_ghost_is_a_chip_over_targets_and_a_miniature_outside()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        var lifecycle = new ContentLifecycle(registry);
        var window = Show(DockState(Group("d1", "d1", "d2"), "d1"), registry, lifecycle: lifecycle);
        Drag(window).MiniatureRenderer = _ => new FakeImage();

        // d1 is the active tab of a visible group: its view is built and attached (DA-9.6).
        var start = Center(TabHeader(window, "d1"), window);
        var center = BoundsIn(TabHost(window, "d1"), window).Center; // the group center, clear of the wedges
        DragTo(window, start, center);

        Assert.True(Part(window, "PART_DropMarker").IsVisible); // the center zone's area marker
        Assert.False(Drag(window).GhostShowsMiniature); // the familiar chip over any target
        Assert.Null(Drag(window).HintText); // tab targets carry no hint in stage 1 (v2)

        MoveTo(window, new Point(18, 300)); // the stripe area — no tab target there
        Assert.True(Drag(window).GhostShowsMiniature); // the take-out herald (DA-9.7)

        CancelAndRelease(window, new Point(18, 300));
    }

    [AvaloniaFact]
    public void DA_9_7_tab_without_a_built_view_keeps_the_chip_outside_targets()
    {
        var registry = new ToolWindowRegistry();
        registry.RegisterDockContent(new CountingTabFactory("d"));
        var lifecycle = new ContentLifecycle(registry);
        var window = Show(DockState(Group("d1", "d1", "d2"), "d1"), registry, lifecycle: lifecycle);
        var captured = false;
        Drag(window).MiniatureRenderer = _ =>
        {
            captured = true;
            return new FakeImage();
        };

        // d2 is inactive: only the active tab of a visible group materializes (TW-9.3), so
        // its view is not built at the start — the ghost stays the chip everywhere (v0.26).
        var start = Center(TabHeader(window, "d2"), window);
        DragTo(window, start, new Point(18, 300));

        Assert.True(Drag(window).GhostVisible);
        Assert.False(Drag(window).GhostShowsMiniature);
        Assert.False(captured);

        CancelAndRelease(window, new Point(18, 300));
    }
}
