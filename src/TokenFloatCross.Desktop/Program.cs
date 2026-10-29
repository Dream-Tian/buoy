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
            // 菜单栏常驻，同时保持普通应用身份：accessory 应用拿不到菜单栏，
            // 而反复在两种身份间切换会让 macOS 重新摆放窗口（切 Space、菜单栏闪）。
            builder = builder.With(new MacOSPlatformOptions
            {
                ShowInDock = true,
                // 关掉 Avalonia 自带的 About/Hide/Quit，应用菜单由 App.BuildAppMenu 提供，
                // 否则菜单栏里会出现两个同名菜单。
                DisableDefaultApplicationMenuItems = true
            });
        }

        return builder;
    }
}
