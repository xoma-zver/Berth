using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Berth.Controls;

/// <summary>
/// Shared view plumbing of the persistent content hosts — the tool window body and the dock
/// tab hosts (spec TW-9.13, DA-9.6): building the view once per content object, and locating
/// the focus target of activation (TW-6.6, DA-6.4).
/// </summary>
internal static class ContentViews
{
    /// <summary>
    /// Builds the view of one content object (spec TW-9.13, DA-9.6): a Control content object
    /// is its own view — detached from a previously discarded host first; anything else is
    /// opaque to the core (ADR-0003) and gets its view built by the application's data
    /// templates over the logical tree (the MVVM path) — null while the host is detached, to
    /// be retried on attachment. Deliberately not a ContentPresenter, which rebuilds its child
    /// on every reattachment and would defeat view retention. The template is selected once
    /// per content object: a live DataTemplates or theme swap does not re-select the built
    /// view — a conscious v1 limitation (spec tool-windows section 12); the view's own
    /// controls theme normally.
    /// </summary>
    public static Control? Build(Control host, object content)
    {
        if (content is Control control)
        {
            // After a full reconfiguration (Registry/Lifecycle swap) the surviving instance
            // may still sit in a discarded host — detach before adopting.
            if (control.Parent is Decorator previous)
            {
                previous.Child = null;
            }

            return control;
        }

        if (!((ILogical)host).IsAttachedToLogicalTree)
        {
            return null; // retried when the host attaches to the logical tree
        }

        var view = host.FindDataTemplate(content)?.Build(content)
            ?? new TextBlock { Text = content.ToString() };
        view.DataContext = content;
        return view;
    }

    /// <summary>First focusable element of the subtree in depth-first order — the focus target of activation (TW-6.6, DA-6.4).</summary>
    public static InputElement? FirstFocusable(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is InputElement { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } element)
            {
                return element;
            }

            if (FirstFocusable(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
