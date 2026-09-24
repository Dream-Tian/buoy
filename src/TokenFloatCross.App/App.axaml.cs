using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Buoy.Settings;

namespace Buoy;

public partial class App : Application
{
    public static CrossSettingsStore SettingsStore { get; } = new();

    public static CrossAppSettings Settings { get; set; } = new();

    public static bool IsExiting { get; set; }

    public static TimeSpan? LastRefreshDuration { get; set; }

    public override void Initialize()
    {
        Settings = SettingsStore.Load();
        AvaloniaXamlLoader.Load(this);
        ApplyTheme(Settings.UseDarkTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (OperatingSystem.IsWindows())
            {
                WindowsTrayIcon.Install();
                WindowsTrayIcon.UpdateTip("今日 0 · 0次 · 消耗 $0.00");
                desktop.Exit += (_, _) => WindowsTrayIcon.Remove();
            }
            else
            {
                var tray = new TrayIcon
                {
                    Icon = CreateTrayIcon(),
                    ToolTipText = "今日 0 · 0次 · 消耗 $0.00",
                    IsVisible = true
                };
                var menu = new NativeMenu();
                var show = new NativeMenuItem("显示");
                show.Click += (_, _) => ShowMain();
                var exit = new NativeMenuItem("退出");
                exit.Click += (_, _) =>
                {
                    IsExiting = true;
                    desktop.Shutdown();
                };
                menu.Add(show);
                menu.Add(exit);
                tray.Menu = menu;
                tray.Clicked += (_, _) => ShowMain();
                TrayIcon.SetIcons(this, new TrayIcons { tray });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void UpdateTrayTip(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsTrayIcon.UpdateTip(text);
        }
    }

    public static void ShowMain()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var window = desktop.MainWindow ?? new MainWindow();
        desktop.MainWindow = window;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static TrayMenuWindow? _trayMenu;

    public static void ShowTrayMenu()
    {
        _trayMenu?.Close();
        _trayMenu = new TrayMenuWindow();
        _trayMenu.Closed += (_, _) => _trayMenu = null;
        _trayMenu.OpenAtCursor();
    }

    private static WindowIcon CreateTrayIcon()
    {
        var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[32 * 32 * 4];
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var index = (y * 32 + x) * 4;
                    var inside = x is > 3 and < 28 && y is > 6 and < 26;
                    var bar = inside && y is > 16 and < 21 && x is > 7 and < 24;
                    pixels[index] = bar ? (byte)69 : (byte)255;
                    pixels[index + 1] = bar ? (byte)122 : (byte)255;
                    pixels[index + 2] = bar ? (byte)31 : (byte)255;
                    pixels[index + 3] = inside ? (byte)255 : (byte)0;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }

        return new WindowIcon(bitmap);
    }

    private static void SetBrush(string key, string hex)
    {
        Current!.Resources[key] = new SolidColorBrush(Color.Parse(hex));
    }

    public static void ApplyTheme(bool dark)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        SetBrush("AppBackgroundBrush", dark ? "#000000" : "#FFFFFF");
        SetBrush("AppPanelBrush", dark ? "#000000" : "#FFFFFF");
        SetBrush("AppTextBrush", dark ? "#FFFFFF" : "#111111");
        SetBrush("AppMutedBrush", dark ? "#A3A3A3" : "#666666");
        SetBrush("AppBorderBrush", dark ? "#2E2E2E" : "#E5E5E5");
        SetBrush("AppAccentBrush", dark ? "#7DCEA0" : "#1F7A45");
        SetBrush("AppAccentSoftBrush", dark ? "#16301F" : "#E8F5EE");
        SetBrush("AppOrangeBrush", dark ? "#F0B45A" : "#C56A00");
        SetBrush("AppBlueBrush", dark ? "#7EB6E0" : "#1D6FB8");
        SetBrush("AppVioletBrush", dark ? "#C4B0F0" : "#6B4FB5");
        SetBrush("AppSubtleBrush", dark ? "#737373" : "#999999");
        SetBrush("AppSegmentBrush", dark ? "#141414" : "#F3F3F3");
        SetBrush("AppFrameBrush", dark ? "#FFFFFF" : "#111111");
    }
}
