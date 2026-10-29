using Avalonia;

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
            // macOS 上只做菜单栏常驻：不占 Dock，也不出现在 Cmd+Tab 列表里。
            builder = builder.With(new MacOSPlatformOptions { ShowInDock = false });
        }

        return builder;
    }
}
