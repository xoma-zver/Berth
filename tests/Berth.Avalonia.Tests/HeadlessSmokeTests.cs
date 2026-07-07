using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Berth.Controls.Tests;

/// <summary>Proves the headless test pipeline works before any real controls exist.</summary>
public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void Window_shows_on_headless_platform()
    {
        var window = new Window { Content = new TextBlock { Text = "smoke" } };
        window.Show();

        Assert.True(window.IsVisible);
    }
}
