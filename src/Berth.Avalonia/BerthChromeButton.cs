using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Berth.Controls;

/// <summary>
/// Chrome button of the built-in leaf chrome: the decorator's «⋮» and «—», the «×» of a tab
/// header and of a document pseudo-window title bar. A templated control with its own
/// ControlTheme — restyle it by defining a theme resource keyed by this type, or style the
/// <c>:pointerover</c>/<c>:pressed</c> states with selectors (docs/styling.md). Never
/// focusable: chrome carries no view-state and must not park keyboard focus (TW-9.13);
/// glyphs and click commands come from the creating chrome. The style key is this type, so
/// application-wide <c>Button</c> styles and the application theme's button template do not
/// apply to it.
/// </summary>
public sealed class BerthChromeButton : Button
{
    /// <summary>Creates the button; chrome buttons are never focusable (TW-9.13).</summary>
    public BerthChromeButton() => Focusable = false;

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The no-include theme fallback (docs/styling.md): an application theme resource
        // wins through the implicit resolution; otherwise the built-in theme applies.
        BerthControlThemes.EnsureTheme(this);
    }
}
