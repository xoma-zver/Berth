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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
