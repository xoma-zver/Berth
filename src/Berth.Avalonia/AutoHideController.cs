using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Focus and click wiring of auto-hiding tool windows (spec TW-6.1, TW-6.2, TW-6.6), attached
/// to every TopLevel of the workspace — the main window plus each materialized floating
/// window (a Float/Window tool window, a document window): keyboard focus is global across
/// the application's windows, so a focus move between the workspace's own windows is an
/// ordinary focus loss and closes the auto-hiding loser (TW-6.1); only deactivation of the
/// whole application — focus leaving every attached TopLevel — raises no GotFocus here and
/// keeps the windows open. Two complementary close paths: the focus path closes the
/// DockUnpinned/Undock window that keyboard focus actually left — the focus loser, so an open
/// window that never had focus survives foreign focus moves and a restored layout is not
/// reaped by the initial focus placement (TW-6.1) — and the pointer path closes open
/// auto-hide windows on a click that moves no focus: the click's containment is captured at
/// the press and the close applies on the release, keeping the press/release pair whole
/// while commands of the gesture rebuild leaf chrome underneath (TW-6.2, TW-9.13, DA-9.6). The focus path also activates the window whose content gained focus
/// (TW-6.6). Popups are neutral on both paths: events inside popup roots bubble in their own
/// visual root and never reach these handlers, and the containment predicate walks the
/// logical tree, so elements of a popup owned by the window or its stripe icon count as
/// inside (TW-6.1); activation checks the visual tree only, keeping popup focus
/// activation-neutral (TW-6.6). Splitter drags are resize gestures, not clicks — excluded
/// from the pointer path (TW-6.2); DnD gestures likewise (TW-5.17) — a release that finished
/// or cancelled a drag closes nothing, and the drag exception of TW-6.1 holds structurally
/// because the drag capture never moves focus. Application deactivation and modal dialogs are
/// covered structurally: focus leaving the TopLevel raises no GotFocus here.
///
/// The same focus path carries the activity wiring (DA-6.4): a focus gain inside a
/// <see cref="DockTabHost"/> of any materialized tree reduces to ActivateTab — a dock tab
/// activates the document and clears the active tool window (TW-6.5), a panel tab activates
/// its owner panel (DA-5.3); Esc inside a tool window moves focus into the current tab of the
/// effective active dock host (TW-6.3) — the auto-hide close then follows from the ordinary
/// focus loss.
/// </summary>
internal sealed class AutoHideController
{
    private readonly BerthWorkspace _workspace;
    private readonly List<TopLevel> _attached = [];
    private IInputElement? _lastFocused;
    private HashSet<string>? _pressWithin;
    private HashSet<string>? _pressOpenAutoHide;
    private bool _pressSeen;
    private bool _pressOnSplitter;

    public AutoHideController(BerthWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Subscribes one TopLevel of the workspace — the main window on attach, each floating
    /// window when it materializes. All attached TopLevels share one state: keyboard focus and
    /// pointer interactions are global across the application's windows.
    /// </summary>
    public void Attach(TopLevel topLevel)
    {
        topLevel.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        topLevel.AddHandler(
            InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        topLevel.AddHandler(
            InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Not handledEventsToo: content that handles Esc itself — a popup, an editor — wins.
        topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        _attached.Add(topLevel);
    }

    /// <summary>Removes the subscriptions of one TopLevel — a floating window being closed.</summary>
    public void Detach(TopLevel topLevel)
    {
        topLevel.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        topLevel.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _attached.Remove(topLevel);
    }

    /// <summary>Removes every subscription — called when the workspace detaches from the visual tree.</summary>
    public void DetachAll()
    {
        foreach (var topLevel in _attached.ToArray())
        {
            Detach(topLevel);
        }
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

        // Activity by focus (DA-6.4, TW-6.6): visual containment only — popup focus is
        // neutral. A focus gain inside a tab host of any materialized tree reduces to
        // ActivateTab: for a dock tab it activates the document, clearing the active tool
        // window (TW-6.5); for a panel tab it activates the owner panel (DA-5.3), so the
        // enclosing decorator needs no command of its own. Focus in panel chrome outside any
        // tab host — the header, an empty-tree placeholder — reduces to the panel activation.
        // Both reductions are idempotent: an unchanged state deduplicates on assignment.
        var gainedVisual = gained as Visual;
        var tab = gainedVisual?.FindAncestorOfType<DockTabHost>(includeSelf: true);
        if (tab is not null
            && _workspace.State is { } layout
            && DockTrees.LayoutContainsTab(layout, tab.TabId))
        {
            var tabId = tab.TabId;
            _workspace.Execute(s => s.ActivateTab(tabId));
        }
        else if (gainedVisual?.FindAncestorOfType<ToolWindowDecorator>(includeSelf: true) is { } host
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

    /// <summary>
    /// Esc inside a tool window moves keyboard focus into the current tab of the effective
    /// active dock host (spec TW-6.3); auto-hiding windows then close by the ordinary focus
    /// loss (TW-6.1). Without a target — the empty main window — nothing happens and nothing
    /// closes: Esc without a target is a no-op.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // The FocusManager of any TopLevel reflects the global keyboard focus (headless
        // probe, 2026-07) — the sender's suffices.
        var focused = (sender as TopLevel)?.FocusManager?.GetFocusedElement();
        if (focused is not Visual visual
            || visual.FindAncestorOfType<ToolWindowDecorator>(includeSelf: true) is null)
        {
            return;
        }

        if (_workspace.FocusCurrentDockTab())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The click's containment — and the set of auto-hide windows open at that moment — is
    /// captured at the press (spec TW-6.2: the close must not tear the press/release pair):
    /// commands running during the release — a tab activation, a toggle — rebuild leaf chrome
    /// under the still-bubbling event (TW-9.13, DA-9.6), orphaning the target, so a
    /// release-time walk would misread a click inside the window as outside; and they may
    /// open a window mid-gesture (a move to a closed owner, DA-E39) that the same release
    /// must not immediately reap.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressSeen = true;
        _pressOnSplitter = HasSplitterAncestor(e.Source as ILogical);
        _pressWithin = PanelsContaining(e.Source as ILogical);
        _pressOpenAutoHide = null;
        if (_workspace.State is not { } state)
        {
            return;
        }

        foreach (var window in state.ToolWindows)
        {
            if (window.IsOpen && IsAutoHiding(window.Mode))
            {
                (_pressOpenAutoHide ??= new HashSet<string>(StringComparer.Ordinal)).Add(window.Id);
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var seen = _pressSeen;
        var within = _pressWithin;
        var openAtPress = _pressOpenAutoHide;
        var onSplitter = _pressOnSplitter;
        _pressSeen = false;
        _pressWithin = null;
        _pressOpenAutoHide = null;
        _pressOnSplitter = false;
        if (onSplitter || HasSplitterAncestor(e.Source as ILogical))
        {
            return; // a splitter drag is a resize gesture, not a click (TW-6.2)
        }

        if (_workspace.Drag?.GestureConsumedClick == true)
        {
            // A DnD gesture is not a click (TW-6.2, TW-5.17): the release that finished or
            // cancelled a drag closes nothing on the pointer path; the focus path is
            // untouched structurally — the capture never moved focus.
            return;
        }

        // A click outside closes, fixed on the release (TW-6.2); the window's own stripe icon
        // is the TW-5.4 toggle and counts as inside (IsWithinPanel). A window opened by the
        // gesture's own command between press and release was not clicked past — it survives
        // until the next interaction. A release without an observed press falls back to the
        // conservative close.
        CloseAutoHidden(w =>
            (!seen || openAtPress?.Contains(w.Id) == true)
            && within?.Contains(w.Id) != true);
    }

    /// <summary>Ids of every tool window the element belongs to, by one logical-ancestor walk (see <see cref="IsWithinPanel"/>).</summary>
    private static HashSet<string>? PanelsContaining(ILogical? element)
    {
        HashSet<string>? result = null;
        for (var node = element; node is not null; node = node.LogicalParent)
        {
            var id = node switch
            {
                ToolWindowDecorator decorator => decorator.ToolWindowId,
                StripeButton icon => icon.ToolWindowId,
                _ => null,
            };
            if (id is not null)
            {
                (result ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
            }
        }

        return result;
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
