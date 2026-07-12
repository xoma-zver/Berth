using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Berth.Controls.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Berth.Controls.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // The headless platform ignores Window.Position in PointToScreen/PointToClient
        // (probe, task 6.2): the gesture-space fallback composes positions manually, so
        // multi-window drag tests keep meaningful screen geometry.
        Berth.Controls.GestureSpace.UseWindowPositionFallback = true;
        return AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
