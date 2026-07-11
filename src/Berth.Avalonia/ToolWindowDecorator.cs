using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>
/// Chrome of one tool window — the persistent host of spec TW-9.13: created once per id,
/// updated in place on every state change, reattached only when the window actually moves to
/// another slot or layer, and living detached in the workspace cache while the window is
/// closed. A header with the title and the menu and hide buttons, the body area below. The «—»
/// button closes the window (spec TW-5.3); the «⋮» button and the title-bar context menu open
/// the full menu of TW-5.16 — every gesture reduces to a core command (ADR-0004); menus are
/// leaf chrome, rebuilt per update so their checkmarks reflect the state. The body materializes
/// through the workspace's <see cref="BerthWorkspace.Lifecycle"/> — the factory bridge of
/// TW-9.5 (spec TW-9.3: content creation is the materialization layer's duty) — and its view is
/// built once per content object and hosted directly: a Control content object is its own view,
/// anything else gets the view built by the application's data templates (the MVVM path) —
/// deliberately not through a ContentPresenter, which rebuilds its child on every reattachment
/// and would defeat the view retention of TW-9.13. The built view survives updates,
/// reattachment and, under KeepWhileRegistered, closing and reopening. Without a coordinator,
/// for a sleeping window (DA-9.4), for a window without a body factory, and for a body tab
/// living outside the panel's own tree the body stays a placeholder. The title is not otherwise
/// regulated by the spec (TW-6.4).
/// </summary>
public sealed class ToolWindowDecorator : Decorator
{
    private readonly BerthWorkspace _workspace;
    private readonly TextBlock _titleText;
    private readonly Button _menuButton;
    private readonly Border _headerBorder;
    private readonly Border _content;
    private object? _bodyContent;

    internal ToolWindowDecorator(string id, BerthWorkspace workspace)
    {
        ToolWindowId = id;
        Title = id;
        _workspace = workspace;
        Focusable = true; // the activation fallback focus target (TW-6.6): content may offer no focusable element

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
        var header = new DockPanel { Height = BerthMetrics.HeaderHeight };
        header.Children.Add(buttons);
        header.Children.Add(_titleText);

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

    /// <summary>Projects one window state into the persistent chrome (spec TW-9.13): the title, the active accent, the menus and the body.</summary>
    internal void Update(ToolWindowState window, ToolWindowDescriptor? descriptor, bool isActive)
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
        UpdateBody(window, descriptor);
    }

    /// <summary>
    /// Adopts keyboard focus for activation (spec TW-6.6): the first focusable element of the
    /// built body view, with the decorator itself as the fallback that keeps the focus-loss
    /// semantics of TW-6.1 reachable for content without focusable elements.
    /// </summary>
    internal void FocusContent()
    {
        if (ContentViews.FirstFocusable(_content) is { } target && target.Focus())
        {
            return;
        }

        Focus();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // A view whose building was deferred — template resolution needs the tree.
        BuildBodyView();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A press on the header or a non-focusable body area moves focus into the window and
        // thereby activates it (TW-6.6); focus already inside stays where it is — interactive
        // children handle their presses and focus themselves before this bubble handler.
        if (!e.Handled && !IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    /// <summary>
    /// The body area. The body materializes only for a hosted, registered window whose body
    /// tab — id equal to the window's id (TW-9.5) — lives in the window's own tree: a body
    /// moved into a dock host is shown there, not here (DA-8.1), and a tree without the body
    /// tab has no content to create («content without a tab» does not exist, TW-9.2). Other
    /// tabs of the tree and the tab strip arrive with phase 4.
    /// </summary>
    private void UpdateBody(ToolWindowState window, ToolWindowDescriptor? descriptor)
    {
        if (window.IsOpen && window.Mode.GetLayer() != ToolWindowLayer.Floating)
        {
            object? body = null;
            if (_workspace.Lifecycle is { } lifecycle
                && descriptor is not null
                && DockTrees.ContainsTab(window.ContentTree, window.Id))
            {
                body = lifecycle.GetOrCreateToolWindowContent(window.Id);
            }

            SetBody(body);
            return;
        }

        // A detached host: under KeepWhileRegistered the built view is retained until
        // reopening or unregistration (TW-9.13); a released body — the DisposeOnClose
        // transition out of openness, or a gone registration — must not be kept alive
        // by the retained view.
        if (!window.IsOpen
            && (descriptor is null || descriptor.RetentionPolicy == ContentRetentionPolicy.DisposeOnClose))
        {
            SetBody(null);
        }
    }

    private void SetBody(object? content)
    {
        if (!ReferenceEquals(_bodyContent, content))
        {
            _bodyContent = content;
            _content.Child = null;
        }

        BuildBodyView();
    }

    /// <summary>
    /// Builds the body view once per content object and keeps it (TW-9.13) — the shared
    /// manual ContentPresenter cut of <see cref="ContentViews.Build"/>; the template path is
    /// deferred until attachment when the logical tree is not there yet.
    /// </summary>
    private void BuildBodyView()
    {
        if (_content.Child is null && _bodyContent is not null)
        {
            _content.Child = ContentViews.Build(this, _bodyContent);
        }
    }

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
