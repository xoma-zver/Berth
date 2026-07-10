using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Chrome of one materialized tool window: a header with the title and the menu and hide
/// buttons, and the body area below. The buttons are passive visuals — their gestures reduce
/// to core commands in a later task (ADR-0004); the body is a placeholder until the factory
/// bridge of the walking skeleton arrives (spec TW-9.3: content creation is the
/// materialization layer's duty). The title is not otherwise regulated by the spec (TW-6.4).
/// </summary>
public sealed class ToolWindowDecorator : Decorator
{
    internal ToolWindowDecorator(string toolWindowId, string title)
    {
        ToolWindowId = toolWindowId;
        Title = title;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        buttons.Children.Add(ChromeButton("⋮", "PART_MenuButton"));
        buttons.Children.Add(ChromeButton("—", "PART_HideButton"));
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
            Child = header,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
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
    internal static ToolWindowDecorator For(ToolWindowState window, ToolWindowRegistry registry) =>
        new(window.Id, registry.TryGet(window.Id, out var descriptor) ? descriptor.Title : window.Id);

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
