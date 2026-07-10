using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Berth.Controls;

/// <summary>
/// One stripe — the vertical icon bar at the left or right edge (spec TW-1.1, TW-1.2). Top to
/// bottom: the side's Primary segment → a separator shown only when both segments are
/// non-empty (TW-1.3) → the Secondary segment → the quick access «⋯» button when this stripe
/// is the configured side and the list is non-empty (TW-8.1, TW-8.4) → a stretch → the bottom
/// segment (Bottom.Primary on the left stripe, Bottom.Secondary on the right), growing upwards
/// from the edge (TW-1.4). Icons are ordered by <see cref="ToolWindowState.Order"/> within a
/// segment (TW-1.4); sleeping states have no registration, hence no title or icon to show, and
/// produce no button (ADR-0003).
/// </summary>
internal sealed class ToolWindowStripe : Decorator
{
    public ToolWindowStripe(QuickAccessSide stripe, LayoutState state, ToolWindowRegistry registry)
    {
        Name = stripe == QuickAccessSide.Left ? "PART_LeftStripe" : "PART_RightStripe";
        var side = stripe == QuickAccessSide.Left ? ToolWindowSide.Left : ToolWindowSide.Right;
        var bottomGroup = stripe == QuickAccessSide.Left ? ToolWindowGroup.Primary : ToolWindowGroup.Secondary;

        var primary = Buttons(state, registry, new ToolWindowSlot(side, ToolWindowGroup.Primary));
        var secondary = Buttons(state, registry, new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        var bottom = Buttons(state, registry, new ToolWindowSlot(ToolWindowSide.Bottom, bottomGroup));

        var top = new StackPanel();
        top.Children.AddRange(primary);
        if (primary.Count > 0 && secondary.Count > 0)
        {
            top.Children.Add(new Border
            {
                Name = "PART_StripeSeparator",
                Height = 1,
                Margin = new Thickness(6, 6, 6, 2),
                Background = BerthBrushes.Separator,
            });
        }

        top.Children.AddRange(secondary);
        if (state.QuickAccessSide == stripe && !QuickAccess.List(state, registry).IsEmpty)
        {
            top.Children.Add(new QuickAccessButton());
        }

        // The bottom segment grows upward: Order 0 is nearest the bottom edge (TW-1.4),
        // i.e. the last child of a top-down stack.
        var bottomStack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        bottomStack.Children.AddRange(Enumerable.Reverse(bottom));

        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottomStack, Dock.Bottom);
        var root = new DockPanel { Width = BerthMetrics.StripeWidth, LastChildFill = false };
        root.Children.Add(top);
        root.Children.Add(bottomStack);

        Child = new Border
        {
            Child = root,
            Background = BerthBrushes.Pane,
            BorderBrush = BerthBrushes.Separator,
            BorderThickness = stripe == QuickAccessSide.Left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0),
        };
    }

    private static List<StripeButton> Buttons(LayoutState state, ToolWindowRegistry registry, ToolWindowSlot slot)
    {
        var buttons = new List<StripeButton>();
        foreach (var window in state.ToolWindows.Where(w => w.Slot == slot && w.IsIconVisible).OrderBy(w => w.Order))
        {
            if (registry.TryGet(window.Id, out var descriptor))
            {
                buttons.Add(new StripeButton(window.Id, descriptor.Title, descriptor.IconKey, window.IsOpen));
            }
        }

        return buttons;
    }
}

/// <summary>
/// The quick access «⋯» button (spec TW-8.1): sits at the end of the Secondary segment of the
/// configured stripe; not created at all while the quick access list is empty (TW-8.4).
/// Opening its flyout is a later task (ADR-0004).
/// </summary>
internal sealed class QuickAccessButton : Decorator
{
    public QuickAccessButton()
    {
        Name = "PART_QuickAccess";
        Child = new Border
        {
            Width = BerthMetrics.StripeButtonSize,
            Height = BerthMetrics.StripeButtonSize,
            Margin = new Thickness(4, 4, 4, 0),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = "⋯",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
