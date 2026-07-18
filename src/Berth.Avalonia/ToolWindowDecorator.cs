using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Chrome of one tool window — the persistent host of spec TW-9.13: created once per id,
/// updated in place on every state change, reattached only when the window actually moves to
/// another slot or layer, and living detached in the workspace cache while the window is
/// closed. A header with the title, the tab strip of the tree's root group (TW-9.5: the strip
/// lives in the header row, like the reference embedding the title into the top-left cell's
/// strip; hidden for the degenerate solitary body, DA-8.4) and the menu and hide buttons; the
/// content area below materializes the window's tab tree — groups, splits and splitters — by
/// the shared projection of spec DA-9.6: tab hosts come from the workspace-wide cache, their
/// content is pulled lazily by the workspace's materialization pass while the window is open
/// (TW-9.3), and the built views survive updates, reattachment and, under KeepWhileRegistered,
/// closing and reopening. A closed DisposeOnClose window drops the body view together with the
/// released content (TW-9.2, DA-9.6) — unless the body lives in a dock host, which shields it
/// from the panel's openness (DA-8.3). The «—» button closes the window (spec TW-5.3); the «⋮»
/// button and the title-bar context menu open the full menu of TW-5.16 — every gesture reduces
/// to a core command (ADR-0004); menus and the header strip are leaf chrome, rebuilt per
/// update so they reflect the state they were built from. The title is not otherwise regulated
/// by the spec (TW-6.4).
/// </summary>
public sealed class ToolWindowDecorator : Decorator
{
    private readonly BerthWorkspace _workspace;
    private readonly TabTreeContext _context;
    private readonly TextBlock _titleText;
    private readonly StackPanel _headerTabs;
    private readonly Button _menuButton;
    private readonly Border _headerBorder;
    private readonly Border _content;
    private bool _headerPressed;

    internal ToolWindowDecorator(string id, BerthWorkspace workspace)
    {
        ToolWindowId = id;
        Title = id;
        _workspace = workspace;
        _context = new TabTreeContext(workspace, id);
        Focusable = true; // the activation fallback focus target (TW-6.6): content may offer no focusable element
        AddHandler(PointerPressedEvent, OnHeaderPreviewPressed, RoutingStrategies.Tunnel);

        _menuButton = ChromeButton("⋮", "PART_MenuButton");
        var hideButton = ChromeButton("—", "PART_HideButton");
        hideButton.Click += (_, _) => workspace.Execute(s => s.Close(id));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        buttons.Children.Add(_menuButton);
        buttons.Children.Add(hideButton);
        DockPanel.SetDock(buttons, Dock.Right);

        _titleText = new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(_titleText, Dock.Left);
        _headerTabs = new StackPanel
        {
            Name = "PART_HeaderTabs",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            // Strip overflow is unhandled by design (document-area, section 11), but it must
            // stay inside the panel: unclipped headers would paint over — and steal clicks
            // from — the neighbouring pane.
            ClipToBounds = true,
        };
        var header = new DockPanel { Height = BerthMetrics.HeaderHeight };
        header.Children.Add(buttons);
        header.Children.Add(_titleText);
        header.Children.Add(_headerTabs);

        _headerBorder = new Border
        {
            Name = "PART_Header",
            Child = header,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(_headerBorder, Dock.Top);

        _content = new Border { Name = "PART_Content" };
        var root = new DockPanel();
        root.Children.Add(_headerBorder);
        root.Children.Add(_content);

        Child = new Border
        {
            Child = root,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>Id of the hosted tool window.</summary>
    public string ToolWindowId { get; }

    /// <summary>Displayed title: the registered <see cref="ToolWindowDescriptor.Title"/>, or the id for a sleeping window.</summary>
    public string Title { get; private set; }

    /// <summary>Projects one window state into the persistent chrome (spec TW-9.13): the title, the active accent, the menus and the content tree.</summary>
    internal void Update(
        ToolWindowState window,
        ToolWindowDescriptor? descriptor,
        bool isActive,
        LayoutState state,
        ToolWindowRegistry registry)
    {
        Title = descriptor?.Title ?? window.Id;
        _titleText.Text = Title;
        // The active-window accent is the theme-discretion indication of TW-6.4; the
        // pseudo-class is the theming hook.
        PseudoClasses.Set(":active", isActive);
        _headerBorder.Background = isActive ? BerthBrushes.ActiveHeader : BerthBrushes.Pane;
        var menu = ToolWindowMenus.BuildWindowMenu(window, _workspace);
        _menuButton.Flyout = menu;
        _headerBorder.ContextFlyout = menu;
        UpdateTree(window, descriptor, state, registry);
    }

    /// <summary>
    /// Adopts keyboard focus for activation (spec TW-6.6): the first focusable element of the
    /// materialized content, with the decorator itself as the fallback that keeps the
    /// focus-loss semantics of TW-6.1 reachable for content without focusable elements.
    /// </summary>
    internal void FocusContent()
    {
        if (ContentViews.FirstFocusable(_content) is { } target)
        {
            // The active tab's host comes first in the walk: delegate into it — content
            // first, the host itself as the fallback (TW-6.6); a placeholder host completes
            // the transfer when its content materializes (DockTabHost.BuildView).
            if (target is DockTabHost host)
            {
                host.FocusContent();
                return;
            }

            if (target.Focus())
            {
                return;
            }
        }

        Focus();
    }

    /// <summary>
    /// Tunnel interception of bare header presses — the header bar is a drag source
    /// (TW-5.17), so its focus-activation is fixed on the release: a press that becomes a
    /// drag never activates, and a cancelled header gesture leaves no trace (TW-6.6; = IDEA:
    /// startDrag does not activate). The press must be handled on the tunnel, before the
    /// platform's own tunnel handler at the source focuses the decorator — the nearest
    /// focusable ancestor — right on the press. Interactive header children — the chrome
    /// buttons and the tab headers of the root group's strip — keep their own presses.
    /// Inside a pseudo-window — and inside a frameless Float window on Windows (TW-7.1) —
    /// the header is the window's move handle instead (TW-7.7): the press delegates to the
    /// window's move gesture, whose no-movement release runs the same deferred activation.
    /// </summary>
    private void OnHeaderPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        // Re-armed on every press: the release of a gesture that became a drag routes to the
        // capture owner and never resets the flag here, and a stale flag would activate the
        // window on the next chrome click depending on gesture history.
        _headerPressed = false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || !IsWithinHeader(e.Source)
            || HasInteractiveHeaderChild(e.Source))
        {
            return;
        }

        if (this.FindAncestorOfType<PseudoWindow>() is { } pseudo)
        {
            pseudo.BeginMove(e);
            e.Handled = true;
            return;
        }

        if (TopLevel.GetTopLevel(this) is FloatingWindowLayer.PanelWindow { IsFrameless: true } frameless)
        {
            frameless.BeginHeaderMove(e);
            e.Handled = true;
            return;
        }

        _headerPressed = true;
        _workspace.Drag?.Arm(new DragSubject(DragSourceKind.PanelHeader, ToolWindowId, Title), e);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A press on a non-focusable body area moves focus into the window and thereby
        // activates it (TW-6.6); focus already inside stays where it is — interactive
        // children handle their presses and focus themselves before this handler, and bare
        // header presses were intercepted on the tunnel (deferred activation, TW-5.17).
        if (!e.Handled && !IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // The deferred header activation (TW-6.6): the release of a click that never became
        // a drag — a drag's release routes to the capture owner and never arrives here.
        if (_headerPressed
            && e.InitialPressMouseButton == MouseButton.Left
            && _workspace.Drag?.GestureConsumedClick != true
            && !IsKeyboardFocusWithin)
        {
            Focus();
        }

        _headerPressed = false;
    }

    private bool IsWithinHeader(object? source) =>
        source is Visual visual
        && (ReferenceEquals(visual, _headerBorder) || _headerBorder.IsVisualAncestorOf(visual));

    /// <summary>Whether the press target belongs to an interactive header child, walked up to the header border.</summary>
    private bool HasInteractiveHeaderChild(object? source)
    {
        for (var node = source as Visual;
            node is not null && !ReferenceEquals(node, _headerBorder);
            node = node.GetVisualParent())
        {
            if (node is Button or DockTabHeader)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The content area — the window's tab tree materialized by the shared projection
    /// (TW-9.5, DA-9.6). A hosted window reconciles its tree and the header strip; a detached
    /// one keeps its views (TW-9.13) — except that a closed DisposeOnClose window forgets the
    /// body view together with the content the coordinator releases on this transition
    /// (TW-9.2): the reset runs during the assignment sync, before the release in
    /// NotifyTransition, and only when the body tab lives in the window's own tree — a body
    /// in a dock host is shielded by DA-8.3 and keeps its view there.
    /// </summary>
    private void UpdateTree(
        ToolWindowState window, ToolWindowDescriptor? descriptor, LayoutState state, ToolWindowRegistry registry)
    {
        var hosted = _workspace.IsHosted(window);
        if (hosted)
        {
            _context.ReconcileRoot(_content, window.ContentTree, state, registry);
            _headerTabs.Children.Clear();
            if (window.ContentTree is TabGroupNode rootGroup && ShowHeaderStrip(rootGroup, window.Id))
            {
                TabGroupView.FillStrip(_headerTabs, rootGroup, state, registry, _context, []);
            }

            return;
        }

        _headerTabs.Children.Clear();
        if (!window.IsOpen
            && descriptor?.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose
            && DockTrees.ContainsTab(window.ContentTree, window.Id))
        {
            _workspace.TabHosts.ResetContent(window.Id);
        }
    }

    /// <summary>
    /// The strip visibility rule of DA-8.4: hidden exactly for the degenerate solitary body —
    /// the panel looks classic; a solitary tab with its own id shows, or it would have no UI
    /// handle at all. The empty root group shows nothing either (DA-2.3).
    /// </summary>
    private static bool ShowHeaderStrip(TabGroupNode root, string windowId) =>
        !root.Tabs.IsEmpty
        && !(root.Tabs.Length == 1 && string.Equals(root.Tabs[0], windowId, StringComparison.Ordinal));

    private static Button ChromeButton(string glyph, string name) => new()
    {
        Name = name,
        Content = glyph,
        Focusable = false,
        Padding = new Thickness(6, 0),
        Background = Brushes.Transparent,
        BorderThickness = default,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
