using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
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
        // 系统弹窗（比如重启时的「中断了重启」）用的是这个名字，默认值是 "Avalonia Application"。
        Name = "Buoy";
        Settings = SettingsStore.Load();
        AvaloniaXamlLoader.Load(this);
        ApplyTheme(Settings.UseDarkTheme);
        ApplyFont(Settings.FontStyle);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacWidgetSnapshotPublisher.StartServer();
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 菜单栏常驻用 OnExplicitShutdown，关窗口不退出；但系统注销/重启发来的退出请求要放行，
            // 否则 macOS 会认为应用拒绝退出，弹「中断了重启」并卡住重启。
            desktop.ShutdownRequested += (_, _) =>
            {
                if (IsExiting)
                {
                    return;
                }

                IsExiting = true;
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            };
            if (desktop.Args?.FirstOrDefault(IsBuoyUri) is { } startupUri)
            {
                HandleUri(startupUri);
            }

            if (ApplicationLifetime is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, args) =>
                {
                    if (args.Kind == ActivationKind.OpenUri)
                    {
                        var uri = args is ProtocolActivatedEventArgs protocol
                            ? protocol.Uri?.ToString()
                            : null;
                        HandleUri(uri);
                    }
                };
            }

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
                var settings = new NativeMenuItem("设置");
                settings.Click += (_, _) => ShowSettings();
                var exit = new NativeMenuItem("退出");
                exit.Click += (_, _) =>
                {
                    IsExiting = true;
                    desktop.Shutdown();
                };
                menu.Add(show);
                menu.Add(settings);
                menu.Add(exit);
                tray.Menu = menu;
                tray.Clicked += (_, _) => ShowMain();
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                if (OperatingSystem.IsMacOS())
                {
                    ApplyTrayTemplate(tray);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool IsBuoyUri(string argument) =>
        argument.StartsWith("buoy:", StringComparison.OrdinalIgnoreCase);

    private static void HandleUri(string? uri)
    {
        if (uri is not null && uri.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            ShowSettings();
            return;
        }

        var quota = uri is not null && uri.Contains("quota", StringComparison.OrdinalIgnoreCase);
        ShowMain();
        if (quota && Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is MainWindow window)
        {
            window.ShowPage("Quota");
        }
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

        if (OperatingSystem.IsMacOS() && !Settings.KeepWindowOnTop)
        {
            // 默认贴在桌面层：从菜单栏点「显示」不会盖住正在用的窗口，也不抢焦点。
            MacWindowLevel.SetDesktopLevel(window, true);
            return;
        }

        // 勾了「置于顶层」，或者其他平台，正常抬到最前。
        MacWindowLevel.ActivateApplication();
        window.Activate();
    }

    private static SettingsWindow? _settingsWindow;

    /// <summary>
    /// 设置窗口按独立窗口显示，不阻塞主窗口；点主窗口可以把它抬到设置上面。
    /// </summary>
    public static void ShowSettings()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (_settingsWindow is { } existing)
        {
            if (existing.IsVisible)
            {
                MacWindowLevel.ActivateApplication();
                existing.Activate();
            }
            else
            {
                existing.Show();
                MacWindowLevel.ActivateApplication();
                existing.Activate();
            }

            return;
        }

        var main = desktop.MainWindow as MainWindow;
        var window = new SettingsWindow(main);
        _settingsWindow = window;
        window.Closed += async (_, _) =>
        {
            _settingsWindow = null;
            if (main is not null)
            {
                await main.RefreshAfterSettingsAsync();
            }
        };
        window.Show();
        MacWindowLevel.ActivateApplication();
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

    /// <summary>
    /// Avalonia 11.3 没有公开 template 开关，原生实现里有；反射打开后菜单栏会按深浅色自动着色。
    /// </summary>
    private static void ApplyTrayTemplate(TrayIcon tray)
    {
        try
        {
            var impl = typeof(TrayIcon)
                .GetProperty("Impl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.GetValue(tray);
            if (impl is null)
            {
                return;
            }

            var iface = impl.GetType().GetInterfaces().FirstOrDefault(item => item.Name == "ITrayIconWithIsTemplateImpl");
            iface?.GetMethod("SetIsTemplateIcon")?.Invoke(impl, new object[] { true });
        }
        catch (Exception)
        {
            // 反射失败时保留白底黑边图标，深浅菜单栏都能看见。
        }
    }

    private static WindowIcon CreateTrayIcon()
    {
        const int size = 32;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var ink = new SolidColorBrush(Colors.White);
            var outline = new Pen(new SolidColorBrush(Colors.Black), 1.1);
            context.DrawEllipse(ink, outline, new Point(16, 5.6), 3, 3);
            context.DrawRectangle(ink, outline, new Rect(14.6, 8, 2.8, 5.6), 1.4, 1.4);

            var body = StreamGeometry.Parse("M11.5,12.5 C11.5,10.6 20.5,10.6 20.5,12.5 L24.2,24 C25.2,27.6 21,29.6 16,29.6 C11,29.6 6.8,27.6 7.8,24 Z");
            var band = new RectangleGeometry(new Rect(6, 18, 20, 3));
            context.DrawGeometry(ink, outline, new CombinedGeometry(GeometryCombineMode.Exclude, body, band));
        }

        return new WindowIcon(bitmap);
    }

    private static void SetBrush(string key, string hex)
    {
        Current!.Resources[key] = new SolidColorBrush(Color.Parse(hex));
    }

    public static void ApplyFont(string key)
    {
        if (Current is null)
        {
            return;
        }

        Current.Resources["AppFontFamily"] = new FontFamily(FontFamilyFor(key));
    }

    private static string FontFamilyFor(string key) => key switch
    {
        "rounded" => "SF Pro Rounded, Yuanti SC, YouYuan, PingFang SC, Microsoft YaHei UI",
        "song" => "Songti SC, STSong, SimSun, serif",
        "kai" => "Kaiti SC, KaiTi, STKaiti, serif",
        "mono" => "SF Mono, Menlo, Consolas, monospace",
        _ => "SF Pro Text, SF Pro Display, PingFang SC, Helvetica Neue, Microsoft YaHei UI"
    };

    public static void ApplyTheme(bool dark)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        SetBrush("AppBackgroundBrush", dark ? "#1C1C1E" : "#FFFFFF");
        SetBrush("AppPanelBrush", dark ? "#1C1C1E" : "#FFFFFF");
        SetBrush("AppTextBrush", dark ? "#F5F5F7" : "#1D1D1F");
        SetBrush("AppMutedBrush", dark ? "#98989D" : "#86868B");
        SetBrush("AppBorderBrush", dark ? "#38383A" : "#E8E8ED");
        SetBrush("AppAccentBrush", dark ? "#5FD08A" : "#2E9E5B");
        SetBrush("AppAccentSoftBrush", dark ? "#17301F" : "#EAF7EF");
        SetBrush("AppOrangeBrush", dark ? "#F0B45A" : "#C26A00");
        SetBrush("AppBlueBrush", dark ? "#6FB3E8" : "#0A6CBF");
        SetBrush("AppVioletBrush", dark ? "#C4B0F0" : "#6B4FB5");
        SetBrush("AppSubtleBrush", dark ? "#7C7C80" : "#A1A1A6");
        SetBrush("AppSegmentBrush", dark ? "#2C2C2E" : "#F2F2F4");
        SetBrush("AppFrameBrush", dark ? "#38383A" : "#E8E8ED");
        SetBrush("AppGroupBrush", dark ? "#2C2C2E" : "#F5F5F7");
        SetBrush("AppFieldBrush", dark ? "#2C2C2E" : "#FFFFFF");
        SetBrush("AppFieldBorderBrush", dark ? "#48484A" : "#D8D8DE");
    }
}
