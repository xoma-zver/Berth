using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Chrome of one materialized tool window: a header with the title and the menu and hide
/// buttons, and the body area below. The «—» button closes the window (spec TW-5.3); the «⋮»
/// button and the title-bar context menu open the full menu of TW-5.16 — every gesture reduces
/// to a core command (ADR-0004). The body is a placeholder until the factory bridge of the
/// walking skeleton arrives (spec TW-9.3: content creation is the materialization layer's
/// duty). The title is not otherwise regulated by the spec (TW-6.4).
/// </summary>
public sealed class ToolWindowDecorator : Decorator
{
    internal ToolWindowDecorator(ToolWindowState window, string title, BerthWorkspace workspace)
    {
        ToolWindowId = window.Id;
        Title = title;
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
            Text = title,
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
        root.Children.Add(new Border { Name = "PART_Content" });

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
        new(window, registry.TryGet(window.Id, out var descriptor) ? descriptor.Title : window.Id, workspace);

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
