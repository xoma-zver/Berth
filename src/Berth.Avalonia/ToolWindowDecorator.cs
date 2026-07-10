using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Chrome of one materialized tool window: a header with the title and the menu and hide
/// buttons, and the body area below. The «—» button closes the window (spec TW-5.3); the «⋮»
/// button and the title-bar context menu open the full menu of TW-5.16 — every gesture reduces
/// to a core command (ADR-0004). The body materializes through the workspace's
/// <see cref="BerthWorkspace.Lifecycle"/> — the factory bridge of TW-9.5 (spec TW-9.3: content
/// creation is the materialization layer's duty); without a coordinator, for a sleeping window
/// (DA-9.4), for a window without a body factory, and for a body tab living outside the
/// panel's own tree it stays a placeholder. The title is not otherwise regulated by the spec
/// (TW-6.4).
/// </summary>
public sealed class ToolWindowDecorator : Decorator
{
    internal ToolWindowDecorator(ToolWindowState window, ToolWindowDescriptor? descriptor, BerthWorkspace workspace)
    {
        ToolWindowId = window.Id;
        Title = descriptor?.Title ?? window.Id;
        var id = window.Id;

        var menu = ToolWindowMenus.BuildWindowMenu(window, workspace);
        var menuButton = ChromeButton("⋮", "PART_MenuButton");
        menuButton.Flyout = menu;
        var hideButton = ChromeButton("—", "PART_HideButton");
        hideButton.Click += (_, _) => workspace.Execute(s => s.Close(id));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        buttons.Children.Add(menuButton);
        buttons.Children.Add(hideButton);
        DockPanel.SetDock(buttons, Dock.Right);

        var header = new DockPanel { Height = BerthMetrics.HeaderHeight };
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var headerBorder = new Border
        {
            Name = "PART_Header",
            Child = header,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            ContextFlyout = menu,
        };
        DockPanel.SetDock(headerBorder, Dock.Top);

        var root = new DockPanel();
        root.Children.Add(headerBorder);
        root.Children.Add(BuildBody(window, isRegistered: descriptor is not null, workspace));

        Child = new Border
        {
            Child = root,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>Id of the materialized tool window.</summary>
    public string ToolWindowId { get; }

    /// <summary>Displayed title: the registered <see cref="ToolWindowDescriptor.Title"/>, or the id for a sleeping window.</summary>
    public string Title { get; }

    /// <summary>Creates the decorator for a window state, taking the title from the registration when there is one.</summary>
    internal static ToolWindowDecorator For(ToolWindowState window, ToolWindowRegistry registry, BerthWorkspace workspace) =>
        new(window, registry.TryGet(window.Id, out var descriptor) ? descriptor : null, workspace);

    /// <summary>
    /// The body area. The body materializes only for a registered window whose body tab —
    /// id equal to the window's id (TW-9.5) — lives in the window's own tree: a body moved
    /// into a dock host is shown there, not here (DA-8.1), and a tree without the body tab
    /// has no content to create («content without a tab» does not exist, TW-9.2). Other tabs
    /// of the tree and the tab strip arrive with phase 4.
    /// </summary>
    private static Border BuildBody(ToolWindowState window, bool isRegistered, BerthWorkspace workspace)
    {
        object? body = null;
        if (workspace.Lifecycle is { } lifecycle
            && isRegistered
            && ContainsTab(window.ContentTree, window.Id))
        {
            body = lifecycle.GetOrCreateToolWindowContent(window.Id);
        }

        return new Border
        {
            Name = "PART_Content",
            Child = body switch
            {
                null => null,
                // A live Control is hosted directly: it is one instance surviving the
                // per-command rebuilds, so it is detached from the discarded projection first.
                Control control => Reparented(control),
                // Anything else is opaque to the core (ADR-0003): a ContentControl presents
                // it and the application's data templates build the view (the MVVM path).
                _ => new ContentControl { Content = body },
            },
        };
    }

    /// <summary>
    /// Detaches a live content control from the previous projection: the rebuild discards the
    /// old subtree without clearing it, and Avalonia rejects a child that still has a parent.
    /// </summary>
    private static Control Reparented(Control content)
    {
        if (content.Parent is Border previous)
        {
            previous.Child = null;
        }

        return content;
    }

    /// <summary>Whether the tree contains the tab, over the public node types.</summary>
    private static bool ContainsTab(TabTreeNode node, string tabId) => node switch
    {
        TabGroupNode group => group.Tabs.Contains(tabId, StringComparer.Ordinal),
        SplitNode split => split.Children.Any(c => ContainsTab(c.Node, tabId)),
        _ => false,
    };

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
