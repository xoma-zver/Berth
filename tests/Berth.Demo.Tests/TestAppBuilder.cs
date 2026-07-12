using Avalonia;
using Avalonia.Headless;
using Berth.Demo.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Berth.Demo.Tests;

public static class TestAppBuilder
{
    // The tests run over the real demo App: its XAML brings the FluentTheme, the ViewLocator
    // data templates and the icon resources — the actual mini-IDE composition, not a stand-in.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Demo.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
