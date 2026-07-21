using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Berth.Controls;

/// <summary>
/// Code-behind of the built-in leaf-chrome theme dictionary and the delivery mechanism of
/// the no-include contract (docs/styling.md): a templated chrome control resolves its theme
/// over the standard implicit lookup — a resource keyed by the control type, reachable from
/// the control up to Application resources — and only when nothing resolves does it fall
/// back to the built-in theme from this dictionary. The dictionary is internal by design:
/// the built-in templates are minimal, and an application restyles by defining its own
/// ControlTheme resource rather than deriving from ours (owner decision, BACKLOG).
/// </summary>
internal sealed partial class BerthControlThemes : ResourceDictionary
{
    private static BerthControlThemes? CachedInstance;

    public BerthControlThemes() => AvaloniaXamlLoader.Load(this);

    /// <summary>The lazily created singleton — built-in themes are shared by every control instance.</summary>
    internal static BerthControlThemes Instance => CachedInstance ??= new BerthControlThemes();

    /// <summary>
    /// The theme fallback of one templated chrome control, called from its
    /// OnAttachedToLogicalTree: an explicit <see cref="StyledElement.Theme"/> or a reachable
    /// application theme resource wins through the platform's own implicit resolution — the
    /// fallback then does nothing; otherwise the built-in theme is assigned. A theme resource
    /// defined after the attach is picked up by the next leaf-chrome rebuild — every state
    /// change produces fresh chrome instances (TW-9.13).
    /// </summary>
    internal static void EnsureTheme(TemplatedControl control)
    {
        if (control.Theme is not null)
        {
            return;
        }

        var key = control.GetType();
        if (control.TryFindResource(key, out var resolved) && resolved is ControlTheme)
        {
            return; // the implicit resolution of the platform applies it
        }

        if (Instance.TryGetValue(key, out var builtIn) && builtIn is ControlTheme theme)
        {
            control.Theme = theme;
        }
    }
}
