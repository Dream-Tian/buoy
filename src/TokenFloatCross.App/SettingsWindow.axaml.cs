using System.Diagnostics;
using Avalonia;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Buoy.NewApi;
using Buoy.Platform;
using Buoy.Settings;

namespace Buoy;

public partial class SettingsWindow : Window
{
    private readonly NewApiClient _client = new();
    private readonly AntigravityQuotaClient _quotaClient = new();
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.Source is Visual source &&
                source.FindAncestorOfType<Button>() is null &&
                source.FindAncestorOfType<TextBox>() is null &&
                source.FindAncestorOfType<CheckBox>() is null &&
                source.FindAncestorOfType<ComboBox>() is null &&
                eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(eventArgs);
            }
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Buoy" : $"当前版本 {version.ToString(3)}";
        var settings = App.Settings;
        BaseUrlBox.Text = settings.NewApiBaseUrl;
        TokenBox.Text = settings.NewApiAccessToken;
        UserIdBox.Text = settings.NewApiUserId > 0 ? settings.NewApiUserId.ToString() : string.Empty;
        AntigravityBox.IsChecked = settings.AntigravityUsageEnabled;
        VisibleOnlyBox.IsChecked = settings.RefreshOnlyWhenVisible;
        KeepOnTopBox.IsChecked = settings.KeepWindowOnTop;
        LaunchAtLoginBox.IsChecked = StartupLaunch.IsEnabled();
        AutoUpdateBox.IsChecked = settings.AutoUpdateCheck;
        ManifestBox.Text = settings.ManifestUrl;
        SelectInterval(settings.NormalizedRefreshSeconds);
        UpdateThemeButtons(settings.UseDarkTheme);
        Topmost = settings.KeepWindowOnTop;
        RefreshDataPage();
        _loading = false;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void TabButton_Click(object? sender, RoutedEventArgs e)
    {
        var tag = sender is Button { Tag: string value } ? value : "Source";
        SourcePage.IsVisible = tag == "Source";
        WindowPage.IsVisible = tag == "Window";
        DataPage.IsVisible = tag == "Data";
        UpdatePage.IsVisible = tag == "Update";
        SetActive(SourceTab, tag == "Source");
        SetActive(WindowTab, tag == "Window");
        SetActive(DataTab, tag == "Data");
        SetActive(UpdateTab, tag == "Update");
        if (tag == "Data")
        {
            RefreshDataPage();
        }
    }

    private void ThemeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        Save(App.Settings with { UseDarkTheme = tag == "Dark" });
        App.ApplyTheme(tag == "Dark");
        UpdateThemeButtons(tag == "Dark");
        StatusText.Text = tag == "Dark" ? "已切换为深色" : "已切换为浅色";
    }

    private async void TestButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadNewApi(out var settings))
        {
            return;
        }

        StatusText.Text = "正在测试…";
        var result = await _client.TestAsync(new NewApiSettings(settings.NewApiBaseUrl, settings.NewApiAccessToken, settings.NewApiUserId));
        StatusText.Text = result.Message;
    }

    private void SaveNewApiButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadNewApi(out var settings))
        {
            return;
        }

        Save(settings);
        StatusText.Text = "NewAPI 已保存到本机";
    }

    private async void TestAntigravityButton_Click(object? sender, RoutedEventArgs e)
    {
        TestAntigravityButton.IsEnabled = false;
        StatusText.Text = "正在读取 Antigravity 当前配额…";
        try
        {
            var quota = await _quotaClient.ReadAsync();
            StatusText.Text = quota.PlanName is null ? quota.Message : $"{quota.PlanName} · {quota.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            TestAntigravityButton.IsEnabled = true;
        }
    }

    private void SaveAntigravityButton_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { AntigravityUsageEnabled = AntigravityBox.IsChecked == true });
        StatusText.Text = AntigravityBox.IsChecked == true
            ? "Antigravity 已开启，关闭设置后会刷新"
            : "Antigravity 已关闭，关闭设置后会刷新";
    }

    private void IntervalBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || IntervalBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var seconds))
        {
            return;
        }

        Save(App.Settings with { RefreshIntervalSeconds = seconds });
        StatusText.Text = $"刷新间隔已设为 {IntervalBox.SelectedItem}";
    }

    private void VisibleOnlyBox_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { RefreshOnlyWhenVisible = VisibleOnlyBox.IsChecked == true });
        StatusText.Text = VisibleOnlyBox.IsChecked == true ? "只会在窗口可见时刷新" : "后台也会按间隔刷新";
    }

    private void KeepOnTopBox_Click(object? sender, RoutedEventArgs e)
    {
        var enabled = KeepOnTopBox.IsChecked == true;
        Save(App.Settings with { KeepWindowOnTop = enabled });
        Topmost = enabled;
        if (Owner is Window owner)
        {
            owner.Topmost = enabled;
        }

        StatusText.Text = enabled ? "窗口已置于顶层" : "已取消置于顶层";
    }

    private void LaunchAtLoginBox_Click(object? sender, RoutedEventArgs e)
    {
        var enabled = LaunchAtLoginBox.IsChecked == true;
        try
        {
            StartupLaunch.SetEnabled(enabled);
            Save(App.Settings with { LaunchAtLogin = StartupLaunch.IsEnabled() });
            StatusText.Text = enabled ? "已加入登录启动项" : "已取消开机自启";
        }
        catch (Exception exception)
        {
            LaunchAtLoginBox.IsChecked = StartupLaunch.IsEnabled();
            StatusText.Text = $"开机自启设置失败：{exception.Message}";
        }
    }

    private void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = App.SettingsStore.DataFolder,
            UseShellExecute = true
        });
    }

    private void AutoUpdateBox_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { AutoUpdateCheck = AutoUpdateBox.IsChecked == true });
        StatusText.Text = AutoUpdateBox.IsChecked == true ? "已开启自动检查" : "已关闭自动检查";
    }

    private void SaveUpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = ManifestBox.Text?.Trim() ?? string.Empty;
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusText.Text = "更新清单地址需要是完整的网址";
            return;
        }

        Save(App.Settings with { ManifestUrl = url });
        StatusText.Text = "更新清单已保存";
    }

    private async void CheckUpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = ManifestBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            StatusText.Text = "请先填写更新清单地址";
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        StatusText.Text = "正在检查更新…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await http.GetStringAsync(uri);
            using var document = JsonDocument.Parse(json);
            var remoteText = document.RootElement.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
            Save(App.Settings with { ManifestUrl = url, UpdateLastCheckedUtc = DateTimeOffset.UtcNow });
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (!Version.TryParse(remoteText, out var remote) || current is null)
            {
                StatusText.Text = "清单里没有可用的版本号";
                return;
            }

            StatusText.Text = remote > current
                ? $"发现新版本 {remote}。跨平台安装包还不能自动安装。"
                : $"已是当前清单中的最新版本 {remote}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"检查失败：{exception.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void RefreshDataPage()
    {
        var folder = App.SettingsStore.DataFolder;
        var settingsFile = Path.Combine(folder, "app-settings.json");
        SettingsSizeText.Text = File.Exists(settingsFile) ? FormatSize(new FileInfo(settingsFile).Length) : "无";
        DataFolderText.Text = folder;
        RefreshDurationText.Text = App.LastRefreshDuration is { } duration
            ? $"{duration.TotalSeconds:0.0} 秒"
            : "尚未刷新";
    }

    private bool TryReadNewApi(out CrossAppSettings settings)
    {
        var userIdText = UserIdBox.Text?.Trim() ?? string.Empty;
        var userId = 0;
        if (userIdText.Length > 0 && !int.TryParse(userIdText, out userId))
        {
            StatusText.Text = "用户 ID 需要是数字";
            settings = App.Settings;
            return false;
        }

        settings = App.Settings with
        {
            NewApiBaseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty,
            NewApiAccessToken = TokenBox.Text?.Trim() ?? string.Empty,
            NewApiUserId = Math.Max(0, userId)
        };
        return true;
    }

    private static void Save(CrossAppSettings settings)
    {
        App.Settings = settings;
        App.SettingsStore.Save(settings);
    }

    private void SelectInterval(int seconds)
    {
        foreach (var item in IntervalBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == seconds.ToString())
            {
                IntervalBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateThemeButtons(bool dark)
    {
        SetActive(LightButton, !dark);
        SetActive(DarkButton, dark);
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
        {
            button.Classes.Add("active");
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };
}
