using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace Berth.Controls;

/// <summary>
/// Host of one tab — the persistent visual node of spec DA-9.6, shared by every materialized
/// tree (the dock area and the content trees of tool windows): created once per tab id,
/// updated in place, reattached only by the semantics of a command (a move, an
/// activation switch, a structural rebuild of the addressed node) and living detached in the
/// projection cache while the tab is inactive or away in a non-materialized host. The content
/// view is built once per content object over the application's data templates — the same
/// manual ContentPresenter cut as the tool window body (TW-9.13) — and survives reattachment
/// and detached retention. Until content arrives — lazily (TW-9.3), for a sleeping tab
/// (DA-9.4) or without a content coordinator — the host shows a placeholder with the tab's
/// title. The host is focusable as the activation fallback target, and a press on a
/// non-focusable content area moves focus into the tab, thereby activating it (DA-6.4).
/// </summary>
public sealed class DockTabHost : Decorator
{
    private readonly TextBlock _placeholder;
    private object? _content;

    internal DockTabHost(string tabId)
    {
        TabId = tabId;
        Focusable = true;
        _placeholder = new TextBlock
        {
            Text = tabId,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _placeholder;
    }

    /// <summary>Id of the hosted tab.</summary>
    public string TabId { get; }

    /// <summary>Whether live content is attached; without it the host shows the placeholder.</summary>
    internal bool HasContent => _content is not null;

    /// <summary>Sleeping marker of the current pull pass (spec DA-9.4); re-resolved on every projection.</summary>
    internal bool IsSleeping { get; set; }

    /// <summary>Updates the placeholder title — the provider string or the id (spec DA-9.6).</summary>
    internal void UpdateTitle(string title) => _placeholder.Text = title;

    /// <summary>
    /// Adopts the materialized content and builds its view once per content object (spec
    /// DA-9.6). Replacing the content — a DisposeOnClose body recreated after reopening —
    /// drops the previous view first: a view must never outlive its content (TW-9.13).
    /// </summary>
    internal void SetContent(object content)
    {
        if (ReferenceEquals(_content, content))
        {
            return;
        }

        _content = content;
        Child = _placeholder;
        BuildView();
    }

    /// <summary>
    /// Forgets the content and its built view, returning to the placeholder (spec DA-9.6):
    /// called when the coordinator releases content whose id stays in the layout — the
    /// DisposeOnClose transition of a panel body out of openness (TW-9.2) or the owner's
    /// unregistration (TW-9.4). The host only drops references; releasing the content object
    /// is the coordinator's job.
    /// </summary>
    internal void ResetContent()
    {
        _content = null;
        Child = _placeholder;
    }

    /// <summary>
    /// Adopts keyboard focus for activation (spec TW-6.6, DA-6.4): the first focusable element
    /// of the built view — the search starts at the host, so a view that is itself focusable
    /// (a bare TextBox) counts — with the host as the fallback for content without one.
    /// </summary>
    internal void FocusContent()
    {
        if (ContentViews.FirstFocusable(this) is { } target && target.Focus())
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
        BuildView();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A press on a non-focusable content area moves focus into the tab and thereby
        // activates it (DA-6.4); interactive children handle their presses and focus
        // themselves before this bubble handler.
        if (!e.Handled && !IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    private void BuildView()
    {
        if (_content is null || !ReferenceEquals(Child, _placeholder))
        {
            return;
        }

        if (ContentViews.Build(this, _content) is { } view)
        {
            Child = view;
            // The focus upgrade of lazy materialization (TW-6.6, DA-6.4): activation focused
            // the placeholder host as the fallback before the content arrived — the just
            // built view completes the transfer. Focus anywhere else is never stolen.
            if (IsFocused)
            {
                FocusContent();
            }
        }
    }
}
