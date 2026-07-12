using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using Berth.Demo;

internal sealed partial class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
#if DEBUG
        .WithDeveloperTools()
#endif
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            // The browser layout store: localStorage of the origin (task 7.0).
            .AfterSetup(builder =>
                ((App)builder.Instance!).LayoutStore = new Berth.Demo.Browser.LocalStorageLayoutStore());
}