using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// One tab group of a materialized tree (spec DA-2.1): the tab strip on top — leaf chrome,
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

    /// <summary>Tabs currently projected — the reconciliation key (spec DA-1.3).</summary>
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
                _content.Child = null;
                BerthWorkspace.DetachFromParent(host);
                _content.Child = host;
            }
        }
        else
        {
            _content.Child = null;
        }
    }

    /// <summary>
    /// Fills a strip panel with the group's tab headers — leaf chrome (spec DA-9.6), shared by
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

    /// <summary>Returns the hosted tab to the projection cache — called before the view is discarded (DA-9.6).</summary>
    public void DetachHost() => _content.Child = null;
}

/// <summary>
/// Header of one tab in a strip — leaf chrome (spec DA-9.6), rebuilt on every update so it
/// always reflects the state it was built from. A left click activates the tab and moves
/// keyboard focus into its content (DA-5.3, DA-6.4) — committed on release, like every chrome
/// gesture (TW-6.2); a middle click and the «×» button close it (DA-5.2); the context menu
/// carries the tab commands (ADR-0004, TW-5.16). The group's active tab carries the
/// <c>:active</c> pseudo-class; the active document — the current tab of the effective active
/// host while no tool window is active (DA-6.2) — additionally carries <c>:current</c>.
/// Headers share the PART name and are discriminated by <c>Tag</c> holding the tab id.
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

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled)
        {
            return; // the «×» button handled its own press
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            _leftPressed = true;
            e.Handled = true;
        }
        else if (properties.IsMiddleButtonPressed)
        {
            _middlePressed = true;
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
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
