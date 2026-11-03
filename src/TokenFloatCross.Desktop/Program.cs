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
            // 窗口只有 560×392，默认那套 GPU 纹理缓存上限对这个尺寸太奢侈了。
            // 卡住以后 Skia 会自己回收旧的图集，不至于把几百 MB 显存/内存一直攥着。
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 64L * 1024 * 1024 })
            .LogToTrace();

        if (OperatingSystem.IsMacOS())
        {
            // 平时只驻留菜单栏：没有 Dock 图标，也不进 Cmd+Tab。
            // 有窗口要显示时由 App.SyncActivationPolicy 临时切回普通应用（菜单栏才会
            // 出现「Buoy」「窗口」和 ⌘ 快捷键），窗口全部收起后再回到菜单栏常驻。
            builder = builder.With(new MacOSPlatformOptions
            {
                ShowInDock = false,
                // 关掉 Avalonia 自带的 About/Hide/Quit，应用菜单由 App.BuildAppMenu 提供，
                // 否则菜单栏里会出现两个同名菜单。
                DisableDefaultApplicationMenuItems = true
            });
        }

        return builder;
    }
}
