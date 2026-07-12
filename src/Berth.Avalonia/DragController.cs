using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>Kind of a drag source (spec TW-5.17, DA-9.7).</summary>
internal enum DragSourceKind
{
    /// <summary>A stripe icon (spec TW-1.4).</summary>
    StripeIcon,

    /// <summary>The header bar of a tool window decorator (spec TW-5.17, TW-7.8).</summary>
    PanelHeader,

    /// <summary>A tab header of a materialized tree (spec DA-9.7).</summary>
    TreeTab,
}

/// <summary>
/// The dragged subject: what the gesture moves and what the ghost shows (spec TW-5.17).
/// <see cref="SubjectId"/> is a tool window id for a stripe icon or a header, and a tab id
/// for a tree tab (spec DA-9.7).
/// </summary>
internal readonly record struct DragSubject(DragSourceKind Kind, string SubjectId, string Title);

/// <summary>
/// The drag gesture controller (spec TW-5.17, ADR-0004): one instance per workspace, driving
/// every drag source through one state machine. Sources arm a candidate on their press; the
/// controller turns it into a drag when the pointer travels past
/// <see cref="BerthMetrics.DragStartThreshold"/>, re-capturing the pointer onto a stable root
/// of the source window — the <see cref="DragLayer"/> for the main window and its
/// pseudo-windows, the floating TopLevel itself for a real floating window (task 6.2) —
/// external re-projections rebuild leaf chrome but never tear the capture, and the release
/// always routes to the capture owner, whatever window the pointer ends up over. The gesture
/// lives in gesture coordinates (<see cref="GestureSpace"/>): screen on the windowed platform,
/// workspace on the overlay one. The gesture is pure visualization until the release: a ghost
/// chip and a target marker (<see cref="IDragVisual"/>), no state changes, no focus moves. The
/// drop target catalog spans every window of the workspace and is rebuilt when an external
/// state change re-projects the workspace mid-gesture — the gesture continues over the updated
/// targets and is cancelled only when the dragged subject leaves the layout (TW-5.17); a
/// target hits only while its window is the top window at the pointer — the zone of an
/// occluded window never fires, with the z-order of real windows approximated (floating
/// windows above the main one, MRU among themselves — a v1 assumption, TW-5.17) and of
/// pseudo-windows exact (the overlay child order). A drop commits through the workspace funnel — one Move (plus the docking SetMode of
/// a floating-mode window) for a stripe drop (TW-5.17), the menu-mirroring command sequence
/// for a tab drop (DA-9.7). A release outside every target is the take-out command of task
/// 6.2: a stripe icon or header floats at the release point (TW-7.8), a tab moves into a new
/// document window there (DA-9.7); cancellation — Esc or a lost capture — leaves no trace: no
/// command, no activation, no focus transfer (DA-E22). A gesture that became a drag also
/// consumes the click: sources and the auto-hide pointer path check
/// <see cref="GestureConsumedClick"/> (TW-6.2: a DnD gesture is not a click).
///
/// The controller also owns the press-focus deferral of tab headers (spec DA-9.7, the mirror
/// of the decorator's bare-header interception of TW-6.6): a tunnel handler on the workspace
/// root — and on every floating TopLevel — marks presses on tab headers handled before the
/// platform's press-focus class handler at the source can park focus on the nearest focusable
/// ancestor; the click semantics run on the release. Presses on interactive header children
/// (the «×» button) are left alone.
/// </summary>
internal sealed class DragController
{
    private enum Phase
    {
        /// <summary>No gesture in flight.</summary>
        Idle,

        /// <summary>A source press was armed; the pointer has not travelled past the threshold.</summary>
        Armed,

        /// <summary>The drag is live: ghost shown, targets built, pointer captured.</summary>
        Dragging,

        /// <summary>Cancelled mid-drag (Esc, subject vanished): visuals are gone, but the capture holds until the release so no source misreads it as a click.</summary>
        CancelledAwaitingRelease,
    }

    private readonly BerthWorkspace _workspace;
    private Phase _phase;
    private DragSubject _subject;
    private TopLevel? _sourceRoot;
    private Point _pressLocal;
    private List<DropTarget>? _targets;
    private DropTarget? _current;
    private bool _catalogDirty;
    private IDragVisual? _visual;
    private Interactive? _captureTarget;
    private TopLevel? _topLevel;

    public DragController(BerthWorkspace workspace)
    {
        _workspace = workspace;
        AttachRoot(workspace);
    }

    /// <summary>The ghost layer of the current skeleton; swapped on rebuild, null after a reset.</summary>
    public DragLayer? Layer { get; set; }

    /// <summary>
    /// Whether the current press gesture became a drag: its release performs no click action —
    /// neither the source's (toggle, activation) nor the auto-hide pointer close (spec TW-5.17,
    /// TW-6.2). Reset when the next press arms or passes by.
    /// </summary>
    public bool GestureConsumedClick { get; private set; }

    /// <summary>Whether the gesture ghost is currently shown — the test observation point (the windowed ghost is an OS window outside the main visual tree).</summary>
    public bool GhostVisible => _visual?.GhostVisible == true;

    /// <summary>Subscribes a floating TopLevel of the workspace (task 6.2): its sources arm and drive the same state machine.</summary>
    public void AttachWindow(TopLevel topLevel) => AttachRoot(topLevel);

    /// <summary>Removes the subscriptions of a closing floating TopLevel.</summary>
    public void DetachWindow(TopLevel topLevel)
    {
        topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPreviewPressed);
        topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        topLevel.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        topLevel.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        if (ReferenceEquals(_sourceRoot, topLevel) && _phase != Phase.Idle)
        {
            // The source window is going away mid-gesture: end with no trace (TW-5.17).
            EndGesture();
        }
    }

    private void AttachRoot(Interactive root)
    {
        root.AddHandler(
            InputElement.PointerPressedEvent, OnPreviewPressed, RoutingStrategies.Tunnel);
        root.AddHandler(
            InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(
            InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(
            InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Arms a drag candidate — called by a source from its press handler, before the press
    /// bubbles here (spec TW-5.17). The gesture starts only if the pointer travels past the
    /// threshold; otherwise the press stays an ordinary click. Sources in every window of the
    /// workspace arm alike (task 6.2); the header of a panel pseudo-window never reaches here —
    /// it is the pseudo-window's move handle (TW-7.7).
    /// </summary>
    public void Arm(DragSubject subject, PointerPressedEventArgs e)
    {
        GestureConsumedClick = false;
        _subject = subject;
        _sourceRoot = e.Source is Visual source
            ? TopLevel.GetTopLevel(source)
            : TopLevel.GetTopLevel(_workspace);
        if (_sourceRoot is null)
        {
            return;
        }

        // The threshold compares source-local logical distances, DPI-independent.
        _pressLocal = e.GetPosition(_sourceRoot);
        _phase = Phase.Armed;
    }

    /// <summary>
    /// Reacts to a state re-projection (spec TW-5.17): an external change mid-gesture rebuilds
    /// the targets — lazily, on the next pointer move over settled layout — and cancels the
    /// gesture only when the dragged subject left the layout.
    /// </summary>
    public void OnProjectionUpdated(LayoutState state)
    {
        if (_phase != Phase.Dragging)
        {
            return;
        }

        if (SubjectInLayout(state))
        {
            _catalogDirty = true;
        }
        else
        {
            CancelDrag();
        }
    }

    /// <summary>Full projection reset (Registry/Lifecycle swap): the gesture ends with no trace and the layer is forgotten.</summary>
    public void Reset()
    {
        if (_phase is Phase.Dragging or Phase.CancelledAwaitingRelease)
        {
            EndGesture();
        }

        _phase = Phase.Idle;
        Layer = null;
    }

    /// <summary>
    /// The press-focus deferral of tab headers (spec DA-9.7): a left or middle press whose
    /// target lies in a tab header is marked handled on the tunnel, before the platform's
    /// press-focus class handler at the source parks focus on the nearest focusable ancestor —
    /// on a panel tree that would activate the panel right on the press, leaving a trace
    /// behind a cancelled drag (TW-5.17). The header runs its own tunnel handler afterwards
    /// (handledEventsToo) to arm the gesture; the click semantics complete on the release.
    /// Presses on the «×» button keep the platform path (DA-9.6); right presses keep the
    /// context-menu gesture.
    /// </summary>
    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(sender as Visual).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (DockTabHeader.FindHeader(e.Source) is { } header && !header.IsPressOnInteractiveChild(e.Source))
        {
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_phase is Phase.Dragging or Phase.CancelledAwaitingRelease)
        {
            return; // a second-button press mid-gesture does not touch the gesture
        }

        // A press that armed nothing resets the leftover flag of the previous gesture; an
        // Armed phase here was set by this very press — the source's handlers run before
        // the press bubbles to the root.
        if (_phase == Phase.Idle)
        {
            GestureConsumedClick = false;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_phase == Phase.Armed && _sourceRoot is { } root)
        {
            var local = e.GetPosition(root);
            var dx = local.X - _pressLocal.X;
            var dy = local.Y - _pressLocal.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < BerthMetrics.DragStartThreshold)
            {
                return;
            }

            StartDrag(e);
        }

        if (_phase != Phase.Dragging)
        {
            return;
        }

        if (_catalogDirty)
        {
            RebuildCatalog();
            if (_phase != Phase.Dragging)
            {
                return; // the rebuild found the subject gone
            }
        }

        UpdateVisuals(GesturePoint(e));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Dragging:
                var target = _current;
                var point = GesturePoint(e);
                EndGesture();
                if (target is not null)
                {
                    // The commit runs through the workspace funnel (ADR-0004); the factory
                    // guards against a state that changed after the last catalog rebuild and
                    // does nothing for an identity drop (TW-5.17, DA-E40).
                    target.Commit(_workspace);
                }
                else
                {
                    CommitOutside(point);
                }

                break;
            case Phase.CancelledAwaitingRelease:
                EndGesture();
                break;
            case Phase.Armed:
                _phase = Phase.Idle; // a click — the source's release handler acts
                break;
        }
    }

    /// <summary>Whether the gesture lives in screen coordinates — the windowed platform (task 6.2).</summary>
    private bool Windowed => _workspace.CanUseWindowed;

    /// <summary>Pointer position in gesture coordinates (spec TW-5.17): events route within the source window.</summary>
    private Point GesturePoint(PointerEventArgs e)
    {
        var root = _sourceRoot ?? TopLevel.GetTopLevel(_workspace);
        if (root is null)
        {
            return e.GetPosition(_workspace);
        }

        return Windowed
            ? GestureSpace.FromTopLevel(root, e.GetPosition(root))
            : e.GetPosition(_workspace);
    }

    private void StartDrag(PointerEventArgs e)
    {
        if (Layer is not { } layer
            || _workspace.State is null
            || _sourceRoot is not { } sourceRoot)
        {
            _phase = Phase.Idle;
            return;
        }

        _phase = Phase.Dragging;
        GestureConsumedClick = true;
        // The capture moves to a stable root of the source window: re-projections rebuild
        // leaf chrome without tearing the gesture, and the release routes here — the source
        // never mistakes it for a click (TW-5.17). The main window captures on the DragLayer
        // (its bubble path feeds the workspace handlers); a floating window captures on its
        // own TopLevel, whose handlers were attached in AttachWindow.
        _captureTarget = ReferenceEquals(sourceRoot, TopLevel.GetTopLevel(_workspace))
            ? layer
            : sourceRoot;
        e.Pointer.Capture(_captureTarget as IInputElement);
        _captureTarget.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        _topLevel = sourceRoot;
        _topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _visual = Windowed ? new WindowedDragVisual(_workspace, layer) : new OverlayDragVisual(layer);
        RebuildCatalog();
        if (_phase != Phase.Dragging)
        {
            return;
        }

        _visual.ShowGhost(_subject.Title);
        UpdateVisuals(GesturePoint(e));
    }

    private void RebuildCatalog()
    {
        _catalogDirty = false;
        if (_workspace.State is not { } state || !SubjectInLayout(state))
        {
            CancelDrag();
            return;
        }

        // Zone geometry reads rendered bounds: settle the layout of the re-projection first,
        // in every window hosting targets.
        _workspace.UpdateLayout();
        var space = new DropZoneSpace(_workspace, Windowed);
        if (Windowed)
        {
            foreach (var root in _workspace.FloatingRoots)
            {
                (root as Control)?.UpdateLayout();
            }
        }

        _targets = _subject.Kind == DragSourceKind.TreeTab
            ? TabDropTargets.Build(_workspace, state, _subject.SubjectId, space)
            : StripeDropTargets.Build(_workspace, state, _subject.SubjectId, space);
        _current = null;
    }

    /// <summary>Whether the dragged subject is still present in the layout (spec TW-5.17, DA-9.7).</summary>
    private bool SubjectInLayout(LayoutState state) => _subject.Kind == DragSourceKind.TreeTab
        ? DockTrees.LayoutContainsTab(state, _subject.SubjectId)
        : state.ToolWindows.Any(w => string.Equals(w.Id, _subject.SubjectId, StringComparison.Ordinal));

    private void UpdateVisuals(Point position)
    {
        _visual?.MoveGhost(position);
        _current = null;
        if (_targets is not null)
        {
            // A target hits only while its window is the top window at the pointer: the zone
            // of an occluded window never fires (TW-5.17, task 6.2).
            var topKey = TopWindowKeyAt(position);
            foreach (var target in _targets)
            {
                if (Equals(target.WindowKey, topKey) && target.Contains(position))
                {
                    _current = target;
                    break;
                }
            }
        }

        if (_current is { } current)
        {
            _visual?.ShowMarker(current);
        }
        else
        {
            _visual?.HideMarker();
        }
    }

    /// <summary>
    /// Key of the top workspace window at the gesture point: the topmost real window by the
    /// MRU activation order (a v1 approximation — the OS z-order is not observable), or the
    /// topmost pseudo-window by the overlay child order; null — the point is over no window
    /// (windowed) or over the base surface (overlay).
    /// </summary>
    private object? TopWindowKeyAt(Point position)
    {
        if (Windowed)
        {
            foreach (var top in _workspace.WindowsTopMostFirst)
            {
                if (top is Window { IsVisible: false })
                {
                    continue;
                }

                var rect = GestureSpace.FromTopLevel(top, new Rect(top.ClientSize));
                if (rect.Contains(position))
                {
                    return top;
                }
            }

            return null;
        }

        if (_workspace.PseudoWindowLayer is { } canvas)
        {
            for (var i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is PseudoWindow pseudo && pseudo.IsVisible)
                {
                    var rect = new Rect(
                        Canvas.GetLeft(pseudo), Canvas.GetTop(pseudo), pseudo.Bounds.Width, pseudo.Bounds.Height);
                    if (rect.Contains(position))
                    {
                        return pseudo;
                    }
                }
            }
        }

        return null;
    }

    // ---- the take-out commits of a release outside every target (task 6.2) ----

    /// <summary>
    /// A release outside every target is not a cancellation (spec TW-5.17): a stripe icon or
    /// a header takes the panel out into Float at the release point (TW-7.8), a tab moves
    /// into a new document window there (DA-9.7). Guards re-read the live state; a platform
    /// without a materialized floating layer commits nothing.
    /// </summary>
    private void CommitOutside(Point point)
    {
        if (!_workspace.CanFloat || _workspace.State is null)
        {
            return;
        }

        if (_subject.Kind == DragSourceKind.TreeTab)
        {
            CommitTabToNewWindow(point);
        }
        else
        {
            CommitFloatOut(point);
        }
    }

    /// <summary>
    /// The TW-7.8 commit: SetFloatingBounds at the release point, SetMode(Float), then the
    /// activating Open with the focus transfer (= the reference: the only activation of the
    /// drag helper is the successful float drop). A panel already open in the floating layer
    /// is an identity — moving its window is the window's own gesture.
    /// </summary>
    private void CommitFloatOut(Point point)
    {
        var id = _subject.SubjectId;
        var window = _workspace.State!.ToolWindows.FirstOrDefault(
            w => string.Equals(w.Id, id, StringComparison.Ordinal));
        if (window is null || (window.IsOpen && window.Mode.GetLayer() == ToolWindowLayer.Floating))
        {
            return;
        }

        // The size: the hosted content of an open panel, else the platform default (TW-7.8).
        var size = _workspace.ScreenBoundsOf(id) is { } hosted
            ? (hosted.Width, hosted.Height)
            : (_workspace.DefaultFloatingBounds().Width, _workspace.DefaultFloatingBounds().Height);
        var bounds = new FloatingBounds(point.X, point.Y, size.Item1, size.Item2);
        _workspace.Execute(s => Exists(s, id) ? s.SetFloatingBounds(id, bounds) : s);
        _workspace.Execute(s => Exists(s, id) ? s.SetMode(id, ToolWindowMode.Float) : s);
        _workspace.Execute(s => Exists(s, id) ? s.Open(id) : s);
        _workspace.FocusToolWindow(id);

        static bool Exists(LayoutState state, string id) =>
            state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// The DA-9.7 outside-drop: MoveTabToNewWindow at the release point — the size comes from
    /// the donor group's rendered bounds (the UI supplies the pixels, ADR-0002) — with the
    /// menu-mirroring activation and focus follow-ups.
    /// </summary>
    private void CommitTabToNewWindow(Point point)
    {
        var id = _subject.SubjectId;
        var size = DonorGroupSize(id) ?? new Size(
            _workspace.DefaultFloatingBounds().Width, _workspace.DefaultFloatingBounds().Height);
        var bounds = new FloatingBounds(point.X, point.Y, size.Width, size.Height);
        _workspace.Execute(s => DockTrees.LayoutContainsTab(s, id) ? s.MoveTabToNewWindow(id, bounds) : s);
        _workspace.Execute(s => DockTrees.LayoutContainsTab(s, id) ? s.ActivateTab(id) : s);
        _workspace.FocusTab(id);
    }

    /// <summary>Rendered size of the group view holding the tab, in logical units; null when not materialized.</summary>
    private Size? DonorGroupSize(string id)
    {
        var space = new DropZoneSpace(_workspace, Windowed);
        foreach (var root in space.Roots)
        {
            foreach (var view in root.GetVisualDescendants().OfType<TabGroupView>())
            {
                if (view.Tabs.Contains(id) && view.Bounds.Width > 0 && view.Bounds.Height > 0)
                {
                    return view.Bounds.Size;
                }
            }
        }

        return null;
    }

    /// <summary>Esc cancels the drag (spec TW-5.17); the capture holds until the release, which then does nothing.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _phase == Phase.Dragging)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    /// <summary>A capture lost to the outside world ends the gesture with no trace (spec TW-5.17).</summary>
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_phase is Phase.Dragging or Phase.CancelledAwaitingRelease)
        {
            EndGesture();
        }
    }

    private void CancelDrag()
    {
        _visual?.HideAll();
        _targets = null;
        _current = null;
        _phase = Phase.CancelledAwaitingRelease;
    }

    private void EndGesture()
    {
        _visual?.HideAll();
        _visual = null;
        _captureTarget?.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        _captureTarget = null;
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel = null;
        _targets = null;
        _current = null;
        _phase = Phase.Idle;
    }
}
