using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Berth;

namespace Berth.Controls.Tests;

/// <summary>
/// Shared plumbing of the workspace tests: showing a state in a headless window, addressing
/// internal controls by their PART names, window-relative geometry, and pointer input.
/// </summary>
internal static class WorkspaceTestSupport
{
    public static Window Show(LayoutState state, ToolWindowRegistry registry, double width = 800, double height = 600)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = new BerthWorkspace { State = state, Registry = registry },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    public static Control Part(Visual root, string name) => TryPart(root, name)
        ?? throw new InvalidOperationException($"No control named '{name}' under {root.GetType().Name}.");

    public static Control? TryPart(Visual root, string name) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Bounds of a control in the coordinates of an ancestor (typically the window).</summary>
    public static Rect BoundsIn(Control control, Visual ancestor)
    {
        var origin = control.TranslatePoint(default, ancestor)
            ?? throw new InvalidOperationException("The control is not attached under the ancestor.");
        return new Rect(origin, control.Bounds.Size);
    }

    /// <summary>Center of a control in window coordinates — the target of pointer input.</summary>
    public static Point Center(Control control, Window window) => BoundsIn(control, window).Center;

    public static void Click(Window window, Control control)
    {
        var point = Center(control, window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static void RightClick(Window window, Control control)
    {
        var point = Center(control, window);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    public static IReadOnlyList<StripeButton> Buttons(Visual root) =>
        [.. root.GetVisualDescendants().OfType<StripeButton>()];

    public static StripeButton Button(Visual root, string toolWindowId) =>
        Buttons(root).Single(b => string.Equals(b.ToolWindowId, toolWindowId, StringComparison.Ordinal));

    public static IReadOnlyList<ToolWindowDecorator> Decorators(Visual root) =>
        [.. root.GetVisualDescendants().OfType<ToolWindowDecorator>()];

    public static ToolWindowDecorator Decorator(Visual root, string toolWindowId) =>
        Decorators(root).Single(d => string.Equals(d.ToolWindowId, toolWindowId, StringComparison.Ordinal));

    public static ToolWindowState Win(string id, ToolWindowSide side, ToolWindowGroup group, int order = 0) =>
        new(id, new ToolWindowSlot(side, group), order);

    /// <summary>Registry with one descriptor per id; the title equals the id unless given as "id:Title".</summary>
    public static ToolWindowRegistry Registry(params string[] ids)
    {
        var registry = new ToolWindowRegistry();
        foreach (var entry in ids)
        {
            var parts = entry.Split(':', 2);
            registry.Register(new ToolWindowDescriptor(
                parts[0],
                parts.Length > 1 ? parts[1] : parts[0],
                new ToolWindowSlot(ToolWindowSide.Left, ToolWindowGroup.Primary)));
        }

        return registry;
    }
}
