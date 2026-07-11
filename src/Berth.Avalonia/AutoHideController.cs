using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Focus and click wiring of auto-hiding tool windows (spec TW-6.1, TW-6.2, TW-6.6), attached
/// to the workspace's TopLevel. Two complementary close paths: the focus path closes the
/// DockUnpinned/Undock window that keyboard focus actually left — the focus loser, so an open
/// window that never had focus survives foreign focus moves and a restored layout is not
/// reaped by the initial focus placement (TW-6.1) — and the pointer path closes open
/// auto-hide windows on a click that moves no focus, on release: closing on press would
/// rebuild the leaf chrome under the started gesture and tear the press/release pair
/// (TW-6.2, TW-9.13). The focus path also activates the window whose content gained focus
/// (TW-6.6). Popups are neutral on both paths: events inside popup roots bubble in their own
/// visual root and never reach these handlers, and the containment predicate walks the
/// logical tree, so elements of a popup owned by the window or its stripe icon count as
/// inside (TW-6.1); activation checks the visual tree only, keeping popup focus
/// activation-neutral (TW-6.6). Splitter drags are resize gestures, not clicks — excluded
/// from the pointer path (TW-6.2). Application deactivation and modal dialogs are covered
/// structurally: focus leaving the TopLevel raises no GotFocus here. The drag exception of
/// TW-6.1 becomes reachable with drag-and-drop (phase 5).
/// </summary>
internal sealed class AutoHideController
{
    private readonly BerthWorkspace _workspace;
    private readonly TopLevel _topLevel;
    private IInputElement? _lastFocused;

    public AutoHideController(BerthWorkspace workspace, TopLevel topLevel)
    {
        _workspace = workspace;
        _topLevel = topLevel;
        topLevel.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        topLevel.AddHandler(
            InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Removes the TopLevel subscriptions — called when the workspace detaches from the visual tree.</summary>
    public void Detach()
    {
        _topLevel.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        _topLevel.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
    }

    /// <summary>
    /// Whether the element belongs to the tool window: its host subtree or its stripe icon,
    /// walked over the logical tree so popups owned inside count as within (spec TW-6.1).
    /// </summary>
    public static bool IsWithinPanel(ILogical? element, string toolWindowId)
    {
        for (var node = element; node is not null; node = node.LogicalParent)
        {
            var id = node switch
            {
                ToolWindowDecorator decorator => decorator.ToolWindowId,
                StripeButton icon => icon.ToolWindowId,
                _ => null,
            };
            if (id is not null && string.Equals(id, toolWindowId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        var gained = e.Source as IInputElement;
        var previous = _lastFocused;
        _lastFocused = gained;

        // Activation by focus (TW-6.6): visual containment only — popup focus is neutral.
        if (gained is Visual visual
            && visual.FindAncestorOfType<ToolWindowDecorator>(includeSelf: true) is { } host
            && _workspace.State is { } state
            && !string.Equals(state.ActiveToolWindowId, host.ToolWindowId, StringComparison.Ordinal)
            && IsOpen(state, host.ToolWindowId))
        {
            var id = host.ToolWindowId;
            _workspace.Execute(s => s.Open(id));
        }

        // The focus loser closes (TW-6.1): the previous focus was inside, the new one is not.
        CloseAutoHidden(w =>
            IsWithinPanel(previous as ILogical, w.Id) && !IsWithinPanel(gained as ILogical, w.Id));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var target = e.Source as ILogical;
        if (HasSplitterAncestor(target))
        {
            return; // a splitter drag is a resize gesture, not a click (TW-6.2)
        }

        // A click outside closes (TW-6.2); the window's own stripe icon is the TW-5.4 toggle
        // and counts as inside for this check (IsWithinPanel).
        CloseAutoHidden(w => !IsWithinPanel(target, w.Id));
    }

    private void CloseAutoHidden(Func<ToolWindowState, bool> lost)
    {
        if (_workspace.State is not { } state)
        {
            return;
        }

        List<string>? close = null;
        foreach (var window in state.ToolWindows)
        {
            if (window.IsOpen && IsAutoHiding(window.Mode) && lost(window))
            {
                (close ??= []).Add(window.Id);
            }
        }

        if (close is not null)
        {
            foreach (var id in close)
            {
                // One close is one core command (ADR-0004); a window already closed by a
                // preceding command of this gesture makes Close a no-op.
                _workspace.Execute(s => s.Close(id));
            }
        }
    }

    /// <summary>The auto-hide set of spec TW-3.2: DockUnpinned and Undock.</summary>
    private static bool IsAutoHiding(ToolWindowMode mode) =>
        mode is ToolWindowMode.DockUnpinned or ToolWindowMode.Undock;

    private static bool IsOpen(LayoutState state, string id) =>
        state.ToolWindows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal) && w.IsOpen);

    private static bool HasSplitterAncestor(ILogical? element)
    {
        for (var node = element; node is not null; node = node.LogicalParent)
        {
            if (node is GridSplitter)
            {
                return true;
            }
        }

        return false;
    }
}
