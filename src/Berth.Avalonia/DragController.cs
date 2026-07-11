using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Berth.Controls;

/// <summary>Kind of a drag source (spec TW-5.17); tab drags arrive with DA-9.7 (task 5.1).</summary>
internal enum DragSourceKind
{
    /// <summary>A stripe icon (spec TW-1.4).</summary>
    StripeIcon,

    /// <summary>The header bar of a tool window decorator (spec TW-7.8 in phase 6; slots until then).</summary>
    PanelHeader,
}

/// <summary>The dragged subject: what the gesture moves and what the ghost shows (spec TW-5.17).</summary>
internal readonly record struct DragSubject(DragSourceKind Kind, string ToolWindowId, string Title);

/// <summary>
/// The drag gesture controller (spec TW-5.17, ADR-0004): one instance per workspace, driving
/// every drag source through one state machine. Sources arm a candidate on their press; the
/// controller turns it into a drag when the pointer travels past
/// <see cref="BerthMetrics.DragStartThreshold"/>, re-capturing the pointer onto the stable
/// <see cref="DragLayer"/> — external re-projections rebuild leaf chrome but never tear the
/// capture. The gesture is pure visualization until the release: a ghost chip and a target
/// marker, no state changes, no focus moves. The drop target catalog is built from the current
/// state and layout, and rebuilt when an external state change re-projects the workspace
/// mid-gesture — the gesture continues over the updated targets and is cancelled only when the
/// dragged subject leaves the layout (TW-5.17). A drop commits exactly one core command
/// through the workspace funnel; a cancelled gesture — Esc, a release outside every target, a
/// lost capture — leaves no trace: no command, no activation, no focus transfer (DA-E22). A
/// gesture that became a drag also consumes the click: sources and the auto-hide pointer path
/// check <see cref="GestureConsumedClick"/> (TW-6.2: a DnD gesture is not a click).
/// </summary>
internal sealed class DragController
{
    private enum Phase
    {
        /// <summary>No gesture in flight.</summary>
        Idle,

        /// <summary>A source press was armed; the pointer has not travelled past the threshold.</summary>
        Armed,

        /// <summary>The drag is live: ghost shown, targets built, pointer captured by the layer.</summary>
        Dragging,

        /// <summary>Cancelled mid-drag (Esc, subject vanished): visuals are gone, but the capture holds until the release so no source misreads it as a click.</summary>
        CancelledAwaitingRelease,
    }

    private readonly BerthWorkspace _workspace;
    private Phase _phase;
    private DragSubject _subject;
    private Point _pressPoint;
    private List<DropTarget>? _targets;
    private DropTarget? _current;
    private bool _catalogDirty;
    private TopLevel? _topLevel;

    public DragController(BerthWorkspace workspace)
    {
        _workspace = workspace;
        workspace.AddHandler(
            InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        workspace.AddHandler(
            InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        workspace.AddHandler(
            InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The ghost layer of the current skeleton; swapped on rebuild, null after a reset.</summary>
    public DragLayer? Layer { get; set; }

    /// <summary>
    /// Whether the current press gesture became a drag: its release performs no click action —
    /// neither the source's (toggle, activation) nor the auto-hide pointer close (spec TW-5.17,
    /// TW-6.2). Reset when the next press arms or passes by.
    /// </summary>
    public bool GestureConsumedClick { get; private set; }

    /// <summary>
    /// Arms a drag candidate — called by a source from its press handler, before the press
    /// bubbles here (spec TW-5.17). The gesture starts only if the pointer travels past the
    /// threshold; otherwise the press stays an ordinary click.
    /// </summary>
    public void Arm(DragSubject subject, PointerPressedEventArgs e)
    {
        GestureConsumedClick = false;
        _subject = subject;
        _pressPoint = e.GetPosition(_workspace);
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

        if (state.ToolWindows.Any(w => string.Equals(w.Id, _subject.ToolWindowId, StringComparison.Ordinal)))
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

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_phase is Phase.Dragging or Phase.CancelledAwaitingRelease)
        {
            return; // a second-button press mid-gesture does not touch the gesture
        }

        // A press that armed nothing resets the leftover flag of the previous gesture; an
        // Armed phase here was set by this very press — the source's class handler runs
        // before the press bubbles to the workspace.
        if (_phase == Phase.Idle)
        {
            GestureConsumedClick = false;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_phase == Phase.Armed)
        {
            var position = e.GetPosition(_workspace);
            var dx = position.X - _pressPoint.X;
            var dy = position.Y - _pressPoint.Y;
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

        UpdateVisuals(e.GetPosition(_workspace));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Dragging:
                var target = _current;
                EndGesture();
                if (target is not null)
                {
                    // Exactly one core command per drop (ADR-0004); the factory guards
                    // against a state that changed after the last catalog rebuild and
                    // yields no command for an identity drop (TW-5.17).
                    _workspace.Execute(target.Commit);
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

    private void StartDrag(PointerEventArgs e)
    {
        if (Layer is not { } layer || _workspace.State is null)
        {
            _phase = Phase.Idle;
            return;
        }

        _phase = Phase.Dragging;
        GestureConsumedClick = true;
        // The capture moves to the stable layer: re-projections rebuild leaf chrome without
        // tearing the gesture, and the release routes here — the source never mistakes it
        // for a click (TW-5.17).
        e.Pointer.Capture(layer);
        layer.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        _topLevel = TopLevel.GetTopLevel(_workspace);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        RebuildCatalog();
        if (_phase != Phase.Dragging)
        {
            return;
        }

        layer.ShowGhost(_subject.Title);
        UpdateVisuals(e.GetPosition(_workspace));
    }

    private void RebuildCatalog()
    {
        _catalogDirty = false;
        if (_workspace.State is not { } state
            || !state.ToolWindows.Any(w => string.Equals(w.Id, _subject.ToolWindowId, StringComparison.Ordinal)))
        {
            CancelDrag();
            return;
        }

        // Zone geometry reads rendered bounds: settle the layout of the re-projection first.
        _workspace.UpdateLayout();
        _targets = StripeDropTargets.Build(_workspace, state, _subject.ToolWindowId);
        _current = null;
    }

    private void UpdateVisuals(Point position)
    {
        Layer?.MoveGhost(position);
        _current = null;
        if (_targets is not null)
        {
            foreach (var target in _targets)
            {
                if (target.HitRect.Contains(position))
                {
                    _current = target;
                    break;
                }
            }
        }

        if (_current is { } current)
        {
            Layer?.ShowMarker(current.MarkerRect);
        }
        else
        {
            Layer?.HideMarker();
        }
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
        Layer?.HideAll();
        _targets = null;
        _current = null;
        _phase = Phase.CancelledAwaitingRelease;
    }

    private void EndGesture()
    {
        Layer?.HideAll();
        if (Layer is { } layer)
        {
            layer.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        }

        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel = null;
        _targets = null;
        _current = null;
        _phase = Phase.Idle;
    }
}
