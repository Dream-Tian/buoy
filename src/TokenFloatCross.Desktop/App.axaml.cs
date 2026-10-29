using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
            // 应用级菜单（菜单栏第一项）。窗口菜单只负责「窗口」，两者合起来才是完整菜单栏。
            NativeMenu.SetMenu(this, BuildAppMenu());
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
                        return;
                    }

                    // 点 Dock 图标 / Cmd+Tab 回到应用时，macOS 会直接把窗口摆出来，
                    // 不经过 ShowMain，页签就停在上次那个。这里补上同样的处理。
                    if (args.Kind == ActivationKind.Reopen)
                    {
                        ShowMain();
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

    /// <summary>
    /// 应用菜单（菜单栏第一项，macOS 会把它显示成应用名）。
    /// 这是应用级菜单的条目，不是二级菜单。
    /// </summary>
    public static NativeMenu BuildAppMenu()
    {
        var menu = new NativeMenu();
        menu.Add(MenuItem("设置…", new KeyGesture(Key.OemComma, KeyModifiers.Meta), ShowSettings));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MenuItem("隐藏 Buoy", new KeyGesture(Key.H, KeyModifiers.Meta), HideWindows));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MenuItem("退出 Buoy", new KeyGesture(Key.Q, KeyModifiers.Meta), ExitApp));
        return menu;
    }

    /// <summary>窗口菜单栏里除应用菜单之外的那几项。</summary>
    public static NativeMenu BuildWindowMenu()
    {
        var menu = new NativeMenu();
        var windowMenu = new NativeMenuItem("窗口") { Menu = new NativeMenu() };
        windowMenu.Menu.Add(MenuItem("显示主窗口", new KeyGesture(Key.D0, KeyModifiers.Meta), ShowMain));
        windowMenu.Menu.Add(MenuItem("关闭窗口", new KeyGesture(Key.W, KeyModifiers.Meta), HideWindows));
        menu.Add(windowMenu);
        return menu;
    }

    private static NativeMenuItem MenuItem(string header, KeyGesture gesture, Action action)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private static void HideWindows()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.MainWindow?.Hide();
        _settingsWindow?.Close();
    }

    private static void ExitApp()
    {
        IsExiting = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
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
        if (window is MainWindow main)
        {
            // 每次点「显示」都回到总览，不沿用上次停留的那个页签。
            main.ShowPage("Overview");
        }

        // 窗口在别的 Space 上时跟到当前 Space，而不是把用户切回去。
        MacWindowLevel.SetFollowsActiveSpace(window);
        window.Show();
        window.WindowState = WindowState.Normal;
        // Show 之后 Avalonia 可能重设过 collectionBehavior，再设一次，保证激活时
        // 窗口是"移动到当前 Space"而不是"切到它所在的 Space"。
        MacWindowLevel.SetFollowsActiveSpace(window);
        // 只 makeKeyAndOrderFront 的话窗口虽然到了最前，应用本身没被激活，
        // 菜单栏还挂在上一个应用名下——所以要显式激活一次。
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
        // 毛玻璃只在 macOS 上生效；关掉或插不进去时，所有画笔退回不透明色。
        var vibrancy = Settings.VibrancyEnabled && OperatingSystem.IsMacOS() && !VibrancyUnavailable;
        SetBrush("AppBackgroundBrush", dark ? "#1C1C1E" : "#FFFFFF");
        SetBrush("AppVibrancyBrush", !vibrancy
            ? (dark ? "#1C1C1E" : "#FFFFFF")
            : (dark ? "#59000000" : "#59FFFFFF"));
        SetBrush("AppPanelBrush", vibrancy
            ? (dark ? "#D91C1C1E" : "#D9FFFFFF")
            : (dark ? "#1C1C1E" : "#FFFFFF"));
        SetBrush("AppTextBrush", dark ? "#F5F5F7" : "#1D1D1F");
        SetBrush("AppMutedBrush", dark ? "#98989D" : "#86868B");
        SetBrush("AppBorderBrush", dark ? "#38383A" : "#E8E8ED");
        SetBrush("AppAccentBrush", dark ? "#5FD08A" : "#2E9E5B");
        SetBrush("AppAccentSoftBrush", dark ? "#17301F" : "#EAF7EF");
        SetBrush("AppOrangeBrush", dark ? "#F0B45A" : "#C26A00");
        SetBrush("AppBlueBrush", dark ? "#6FB3E8" : "#0A6CBF");
        SetBrush("AppVioletBrush", dark ? "#C4B0F0" : "#6B4FB5");
        SetBrush("AppSubtleBrush", dark ? "#7C7C80" : "#A1A1A6");
        SetBrush("AppSegmentBrush", vibrancy
            ? (dark ? "#2EFFFFFF" : "#1F787880")
            : (dark ? "#2C2C2E" : "#F2F2F4"));
        // 分段按钮的未选中文字：压在轨道上要够清楚，所以比普通 muted 深一档。
        SetBrush("AppSegmentTextBrush", vibrancy
            ? (dark ? "#B4B4BA" : "#5E5E66")
            : (dark ? "#98989D" : "#86868B"));
        SetBrush("AppFrameBrush", dark ? "#38383A" : "#E8E8ED");
        SetBrush("AppGroupBrush", vibrancy
            ? (dark ? "#991C1C1E" : "#99FFFFFF")
            : (dark ? "#2C2C2E" : "#F5F5F7"));
        SetBrush("AppFieldBrush", dark ? "#2C2C2E" : "#FFFFFF");
        SetBrush("AppFieldBorderBrush", dark ? "#48484A" : "#D8D8DE");
    }

    /// <summary>
    /// 按当前设置给窗口垫系统材质；关掉材质时把原生视图摘掉。
    /// 插不进去就把整套画笔退回不透明，免得窗口变成一块看不清的玻璃板。
    /// </summary>
    public static void ApplyWindowVibrancy(Window window, double cornerRadius)
    {
        if (!Settings.VibrancyEnabled || !OperatingSystem.IsMacOS())
        {
            MacVibrancy.Remove(window);
            return;
        }

        if (MacVibrancy.Apply(window, cornerRadius) || VibrancyUnavailable)
        {
            return;
        }

        VibrancyUnavailable = true;
        ApplyTheme(Settings.UseDarkTheme);
    }

    /// <summary>原生材质不可用（拿不到原生窗口等），本次运行一律用纯色。</summary>
    private static bool VibrancyUnavailable { get; set; }
}
