using Avalonia;
using Buoy.Settings;

namespace Buoy;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsMacOS())
        {
            // 小组件模式下不占 Dock，只留菜单栏图标。
            var widgetMode = new CrossSettingsStore().Load().WidgetMode;
            builder = builder.With(new MacOSPlatformOptions { ShowInDock = !widgetMode });
        }

        return builder;
    }
}
