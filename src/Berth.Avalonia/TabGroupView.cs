using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// One tab group of a materialized tree (DA-2.1): the tab strip on top — leaf chrome,
/// refilled on every update (DA-9.6) — and the content area below, hosting the active tab's
/// <see cref="DockTabHost"/> from the workspace-wide cache. Only the active tab's content is
/// shown, so only it materializes (TW-9.3, DA-9.3). The group has no identity beyond its tabs
/// (DA-1.3): reconciliation matches views to state groups by tab overlap, and a view may be
/// discarded and rebuilt freely — the retained state lives in the tab hosts, not here. The
/// root group of a panel tree shows no strip of its own: its strip lives in the decorator's
/// header row (TW-9.5, task 4.1) — or nowhere, for the degenerate solitary body (DA-8.4).
/// </summary>
internal sealed class TabGroupView : DockPanel
{
    private readonly TabTreeContext _context;
    private readonly StackPanel _strip;
    private readonly Border _stripBar;
    private readonly Decorator _content;

    public TabGroupView(TabTreeContext context)
    {
        _context = context;
        _strip = new StackPanel { Orientation = Orientation.Horizontal };
        _stripBar = new Border
        {
            Name = "PART_TabStrip",
            Height = BerthMetrics.TabStripHeight,
            Child = _strip,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            // Overflowing headers stay inside the group (document-area, section 11): they
            // must not paint over or steal clicks from the neighbouring split cell.
            ClipToBounds = true,
        };
        SetDock(_stripBar, Dock.Top);
        _content = new Decorator { Name = "PART_GroupContent" };
        Children.Add(_stripBar);
        Children.Add(_content);
    }

    /// <summary>Context of the tree the group belongs to — the canHost key of the drop catalog (DA-9.7).</summary>
    public TabTreeContext Context => _context;

    /// <summary>Tabs currently projected — the reconciliation key (DA-1.3).</summary>
    public HashSet<string> Tabs { get; } = new(StringComparer.Ordinal);

    /// <summary>Refills the strip and points the content area at the active tab's host.</summary>
    public void Update(TabGroupNode group, LayoutState state, ToolWindowRegistry registry, ImmutableArray<int> path)
    {
        Tabs.Clear();
        Tabs.UnionWith(group.Tabs);

        // The root group of a panel keeps no strip of its own — the decorator header hosts
        // it (TW-9.5), and filling both would duplicate the headers; the empty strip exists
        // only at the empty root group (DA-2.3) — nothing to show.
        var stripInHeader = _context.PanelId is not null && path.IsEmpty;
        if (stripInHeader)
        {
            _strip.Children.Clear();
        }
        else
        {
            FillStrip(_strip, group, state, registry, _context, path);
        }

        _stripBar.IsVisible = !group.Tabs.IsEmpty && !stripInHeader;

        if (group.ActiveTabId is { } active)
        {
            var host = _context.Workspace.TabHosts.GetHost(active);
            if (!ReferenceEquals(_content.Child, host))
            {
                DetachHost();
                BerthWorkspace.DetachFromParent(host);
                _content.Child = host;
            }
        }
        else
        {
            DetachHost();
        }
    }

    /// <summary>
    /// Fills a strip panel with the group's tab headers — leaf chrome (DA-9.6), shared by
    /// the group's own bar and the decorator header row hosting a panel root group's strip.
    /// </summary>
    public static void FillStrip(
        Panel strip,
        TabGroupNode group,
        LayoutState state,
        ToolWindowRegistry registry,
        TabTreeContext context,
        ImmutableArray<int> path)
    {
        strip.Children.Clear();
        foreach (var tab in group.Tabs)
        {
            strip.Children.Add(new DockTabHeader(tab, group, state, registry, context, path));
        }
    }

    /// <summary>
    /// Returns the hosted tab to the projection cache (DA-9.6). Through the draining detach:
    /// the displaced host may join another window of the workspace in the same pass, and the
    /// old root's layout queue must not keep naming it (see
    /// <see cref="BerthWorkspace.DetachFromParent"/>).
    /// </summary>
    public void DetachHost()
    {
        if (_content.Child is { } previous)
        {
            BerthWorkspace.DetachFromParent(previous);
        }
    }
}

/// <summary>
/// Header of one tab in a strip — leaf chrome (DA-9.6), rebuilt on every update so it
/// always reflects the state it was built from. A left click activates the tab and moves
/// keyboard focus into its content (DA-5.3, DA-6.4) — committed on release, like every chrome
/// gesture (TW-6.2); a middle click and the «×» button close it (DA-5.2); the context menu
/// carries the tab commands (ADR-0004, TW-5.16). The header is also a drag source (DA-9.7): its press arms the workspace drag controller, whose workspace-level tunnel
/// handler already marked the press handled — deferring the platform press-focus — so the
/// header's own press handling runs on the tunnel with handledEventsToo, and the click
/// semantics complete on the release only when the gesture never became a drag. The group's
/// active tab carries the <c>:active</c> pseudo-class; the active document — the current tab
/// of the effective active host while no tool window is active (DA-6.2) — additionally
/// carries <c>:current</c>. Headers share the PART name and are discriminated by <c>Tag</c>
/// holding the tab id.
/// </summary>
internal sealed class DockTabHeader : Border
{
    private readonly TabTreeContext _context;
    private readonly string _tabId;
    private bool _leftPressed;
    private bool _middlePressed;

    public DockTabHeader(
        string tabId,
        TabGroupNode group,
        LayoutState state,
        ToolWindowRegistry registry,
        TabTreeContext context,
        ImmutableArray<int> path)
    {
        _tabId = tabId;
        _context = context;
        Name = "PART_TabHeader";
        Tag = tabId;
        Padding = new Thickness(8, 0, 2, 0);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        var workspace = context.Workspace;
        var isActive = string.Equals(group.ActiveTabId, tabId, StringComparison.Ordinal);
        var isCurrentDocument = state.ActiveToolWindowId is null
            && string.Equals(state.DockArea.CurrentTabId, tabId, StringComparison.Ordinal);
        PseudoClasses.Set(":active", isActive);
        PseudoClasses.Set(":current", isCurrentDocument);
        Background = isCurrentDocument
            ? BerthBrushes.ActiveHeader
            : isActive ? BerthBrushes.OpenIcon : Brushes.Transparent;

        var close = new Button
        {
            Name = "PART_TabClose",
            Content = "×",
            Focusable = false,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => workspace.Execute(s => s.CloseTab(tabId));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = TabHostCache.TitleOf(workspace, tabId),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(close);
        Child = row;
        ContextFlyout = DockTabMenus.BuildTabMenu(
            state, tabId, registry, workspace, canRotate: !path.IsEmpty, context.PanelId);
    }

    /// <summary>Id of the tab the header represents.</summary>
    internal string TabId => _tabId;

    /// <summary>Context of the tree the header belongs to — the canHost key of the drop catalog (DA-9.7).</summary>
    internal TabTreeContext Context => _context;

    /// <summary>Nearest tab header above the press target, or null.</summary>
    internal static DockTabHeader? FindHeader(object? source) =>
        (source as Visual)?.FindAncestorOfType<DockTabHeader>(includeSelf: true);

    /// <summary>
    /// Whether the press target is an interactive child of this header — the «×» button keeps
    /// its own press and the platform press-focus path (DA-9.6, DA-9.7).
    /// </summary>
    internal bool IsPressOnInteractiveChild(object? source)
    {
        for (var node = source as Visual;
            node is not null && !ReferenceEquals(node, this);
            node = node.GetVisualParent())
        {
            if (node is Button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Press handling on the tunnel with handledEventsToo: the workspace drag controller marks
    /// bare header presses handled earlier on the same tunnel (the press-focus deferral of
    /// DA-9.7), so the bubble path never fires for them. Flags are re-armed on every press —
    /// a drag's release routes to the capture owner and never resets them here (TW-5.17).
    /// </summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _leftPressed = false;
        _middlePressed = false;
        if (IsPressOnInteractiveChild(e.Source))
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            _leftPressed = true;
            // The press is also a drag candidate (DA-9.7): past the threshold the gesture
            // becomes a drag and this header never sees the release.
            _context.Workspace.Drag?.Arm(
                new DragSubject(DragSourceKind.TreeTab, _tabId, TabHostCache.TitleOf(_context.Workspace, _tabId)),
                e);
        }
        else if (properties.IsMiddleButtonPressed)
        {
            _middlePressed = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_context.Workspace.Drag?.GestureConsumedClick == true)
        {
            // A gesture that became a drag is not a click (TW-5.17); its release normally
            // routes to the capture owner and never arrives here — this is the safety net.
            _leftPressed = false;
            _middlePressed = false;
            return;
        }

        if (_leftPressed && e.InitialPressMouseButton == MouseButton.Left)
        {
            // DA-5.3 + DA-6.4: the gesture activates the tab and transfers focus into its
            // content; focus already inside is left alone (TryFocusTab guards).
            var id = _tabId;
            _context.Workspace.Execute(s => s.ActivateTab(id));
            _context.Workspace.FocusTab(id);
            e.Handled = true;
        }
        else if (_middlePressed && e.InitialPressMouseButton == MouseButton.Middle)
        {
            var id = _tabId;
            _context.Workspace.Execute(s => s.CloseTab(id));
            e.Handled = true;
        }

        _leftPressed = false;
        _middlePressed = false;
    }
}
