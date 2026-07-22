using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Berth.Themes.NewUi;

/// <summary>
/// Ready-made Berth appearance styled after the IntelliJ IDEA New UI look: light and dark
/// values for every Berth design token (<c>BerthThemeKeys</c>) plus pseudo-class styles for
/// the states tokens cannot express — the solid accent fill of the active panel's stripe
/// icon, hover feedback, and the selected tab header. Include it in application styles:
/// <code><![CDATA[
/// <Application.Styles>
///   <FluentTheme/>
///   <newui:BerthNewUiTheme/>
/// </Application.Styles>
/// ]]></code>
/// The theme only supplies resources and selector styles over the public styling contract
/// (docs/styling.md); it replaces no control templates, so application overrides keep
/// working: a resource under a Berth token key defined closer to the control wins, and
/// application pseudo-class styles declared after this theme override its state styles.
/// </summary>
public sealed class BerthNewUiTheme : Styles
{
    /// <summary>Loads the theme's styles and per-variant token resources.</summary>
    public BerthNewUiTheme() => AvaloniaXamlLoader.Load(this);
}
