using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// One stripe — the vertical icon bar at the left or right edge (TW-1.1, TW-1.2). Top to
/// bottom: the side's Primary segment → a separator shown only when both segments are
/// non-empty (TW-1.3) → the Secondary segment → the quick access «⋯» button when this stripe
/// is the configured side and the list is non-empty (TW-8.1, TW-8.4) → a stretch → the bottom
/// segment (Bottom.Primary on the left stripe, Bottom.Secondary on the right), growing upwards
/// from the edge (TW-1.4). Icons are ordered by <see cref="ToolWindowState.Order"/> within a
/// segment (TW-1.4); sleeping states have no registration, hence no title or icon to show, and
/// produce no button (ADR-0003). The stripe container persists; its buttons are leaf chrome,
/// rebuilt on every update (TW-9.13).
/// </summary>
internal sealed class ToolWindowStripe : Decorator
{
    private readonly QuickAccessSide _stripe;
    private readonly StackPanel _top = new();
    private readonly StackPanel _bottom = new() { Margin = new Thickness(0, 0, 0, 4) };

    public ToolWindowStripe(QuickAccessSide stripe)
    {
        _stripe = stripe;
        Name = stripe == QuickAccessSide.Left ? "PART_LeftStripe" : "PART_RightStripe";
        DockPanel.SetDock(_top, Dock.Top);
        DockPanel.SetDock(_bottom, Dock.Bottom);
        var root = new DockPanel { LastChildFill = false };
        ThemeTokens.BindSize(root, Layoutable.WidthProperty, BerthThemeKeys.StripeWidth, BerthMetrics.StripeWidth);
        root.Children.Add(_top);
        root.Children.Add(_bottom);
        var border = new Border
        {
            Child = root,
            BorderThickness = stripe == QuickAccessSide.Left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0),
        };
        ThemeTokens.BindBrush(border, Border.BackgroundProperty, BerthThemeKeys.Pane, BerthBrushes.Pane);
        ThemeTokens.BindBrush(border, Border.BorderBrushProperty, BerthThemeKeys.Separator, BerthBrushes.Separator);
        Child = border;
    }

    /// <summary>Refills the segments from the state and registrations.</summary>
    public void Update(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        var side = _stripe == QuickAccessSide.Left ? ToolWindowSide.Left : ToolWindowSide.Right;
        var bottomGroup = _stripe == QuickAccessSide.Left ? ToolWindowGroup.Primary : ToolWindowGroup.Secondary;

        var primary = Buttons(state, registry, workspace, new ToolWindowSlot(side, ToolWindowGroup.Primary));
        var secondary = Buttons(state, registry, workspace, new ToolWindowSlot(side, ToolWindowGroup.Secondary));
        var bottom = Buttons(state, registry, workspace, new ToolWindowSlot(ToolWindowSide.Bottom, bottomGroup));

        _top.Children.Clear();
        _top.Children.AddRange(primary);
        if (primary.Count > 0 && secondary.Count > 0)
        {
            var separator = new Border
            {
                Name = "PART_StripeSeparator",
                Height = 1,
                Margin = new Thickness(6, 6, 6, 2),
            };
            ThemeTokens.BindBrush(
                separator, Border.BackgroundProperty, BerthThemeKeys.Separator, BerthBrushes.Separator);
            _top.Children.Add(separator);
        }

        _top.Children.AddRange(secondary);
        if (state.QuickAccessSide == _stripe && !QuickAccess.List(state, registry).IsEmpty)
        {
            _top.Children.Add(new QuickAccessButton(state, registry, workspace));
        }

        // The bottom segment grows upward: Order 0 is nearest the bottom edge (TW-1.4),
        // i.e. the last child of a top-down stack.
        _bottom.Children.Clear();
        _bottom.Children.AddRange(Enumerable.Reverse(bottom));
    }

    private static List<StripeButton> Buttons(
        LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace, ToolWindowSlot slot)
    {
        var buttons = new List<StripeButton>();
        foreach (var window in state.ToolWindows.Where(w => w.Slot == slot && w.IsIconVisible).OrderBy(w => w.Order))
        {
            if (registry.TryGet(window.Id, out var descriptor))
            {
                buttons.Add(new StripeButton(window, descriptor, workspace));
            }
        }

        return buttons;
    }
}

/// <summary>
/// The quick access «⋯» button (TW-8.1): sits at the end of the Secondary segment of the
/// configured stripe; not created at all while the quick access list is empty (TW-8.4). A left
/// click opens the list of hidden windows (TW-8.2) — selecting one returns its icon and opens
/// it (TW-8.3); the context menu moves the button between the stripes (TW-5.15, TW-5.16). The
/// list is an attached flyout, so it stays reachable without opening the popup.
/// </summary>
internal sealed class QuickAccessButton : Decorator
{
    private bool _pressed;

    public QuickAccessButton(LayoutState state, ToolWindowRegistry registry, BerthWorkspace workspace)
    {
        Name = "PART_QuickAccess";
        var face = new Border
        {
            Margin = new Thickness(4, 4, 4, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent, // a null background would defeat hit testing
            Child = new TextBlock
            {
                Text = "⋯",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ThemeTokens.BindSize(face, Layoutable.WidthProperty, BerthThemeKeys.StripeButtonSize, BerthMetrics.StripeButtonSize);
        ThemeTokens.BindSize(face, Layoutable.HeightProperty, BerthThemeKeys.StripeButtonSize, BerthMetrics.StripeButtonSize);
        Child = face;
        FlyoutBase.SetAttachedFlyout(this, ToolWindowMenus.BuildQuickAccessList(state, registry, workspace));
        ContextFlyout = ToolWindowMenus.BuildQuickAccessSideMenu(state.QuickAccessSide, workspace);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = true;
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed && e.InitialPressMouseButton == MouseButton.Left)
        {
            FlyoutBase.ShowAttachedFlyout(this);
            e.Handled = true;
        }

        _pressed = false;
    }
}
