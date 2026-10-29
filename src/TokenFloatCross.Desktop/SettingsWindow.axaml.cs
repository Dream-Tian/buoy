using System.Diagnostics;
using Avalonia;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Buoy.NewApi;
using Buoy.Platform;
using Buoy.Settings;
using Buoy.Usage;

namespace Buoy;

public partial class SettingsWindow : Window
{
    private readonly NewApiClient _client = new();
    private readonly AntigravityQuotaClient _quotaClient = new();
    private readonly ChatGptQuotaClient _chatGptClient = new();
    private readonly MainWindow? _mainWindow;
    private bool _loading = true;

    public SettingsWindow()
        : this(null)
    {
    }

    public SettingsWindow(MainWindow? mainWindow)
    {
        _mainWindow = mainWindow;
        InitializeComponent();
        NativeMenu.SetMenu(this, App.BuildWindowMenu());
        Opened += (_, _) => App.ApplyWindowVibrancy(this, 18);

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
        ChatGptBox.IsChecked = settings.ChatGptUsageEnabled;
        WorkBuddyBox.IsChecked = settings.WorkBuddyUsageEnabled;
        WorkBuddyCnBox.IsChecked = settings.WorkBuddyCnUsageEnabled;
        VisibleOnlyBox.IsChecked = settings.RefreshOnlyWhenVisible;
        KeepOnTopBox.IsChecked = settings.KeepWindowOnTop;
        WidgetModeBox.IsChecked = settings.WidgetMode;
        LaunchAtLoginBox.IsChecked = StartupLaunch.IsEnabled();
        AutoUpdateBox.IsChecked = settings.AutoUpdateCheck;
        VibrancyBox.IsChecked = settings.VibrancyEnabled;
        ManifestBox.Text = settings.ManifestUrl;
        SelectInterval(settings.NormalizedRefreshSeconds);
        SelectFont(settings.FontStyle);
        SelectWidgetRange(CrossSettingsStore.NormalizeRange(settings.WidgetRange));
        UpdateFilterSummary();
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
        FilterPage.IsVisible = tag == "Filter";
        DataPage.IsVisible = tag == "Data";
        UpdatePage.IsVisible = tag == "Update";
        SetActive(SourceTab, tag == "Source");
        SetActive(WindowTab, tag == "Window");
        SetActive(FilterTab, tag == "Filter");
        SetActive(DataTab, tag == "Data");
        SetActive(UpdateTab, tag == "Update");
        if (tag == "Filter")
        {
            // 每次进页面都按主窗口当前时段重建，避免时间段切了列表还是旧的。
            BuildFilterList();
        }

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

    private void WorkBuddyBox_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { WorkBuddyUsageEnabled = WorkBuddyBox.IsChecked == true });
        StatusText.Text = WorkBuddyBox.IsChecked == true
            ? "WorkBuddy AI（国际版）用量已开启，关闭设置后会刷新"
            : "WorkBuddy AI（国际版）用量已关闭，关闭设置后会刷新";
    }

    private void WorkBuddyCnBox_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { WorkBuddyCnUsageEnabled = WorkBuddyCnBox.IsChecked == true });
        StatusText.Text = WorkBuddyCnBox.IsChecked == true
            ? "WorkBuddy（国内版）用量已开启，关闭设置后会刷新"
            : "WorkBuddy（国内版）用量已关闭，关闭设置后会刷新";
    }

    private void SaveAntigravityButton_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { AntigravityUsageEnabled = AntigravityBox.IsChecked == true });
        StatusText.Text = AntigravityBox.IsChecked == true
            ? "Antigravity 已开启，关闭设置后会刷新"
            : "Antigravity 已关闭，关闭设置后会刷新";
    }

    private async void TestChatGptButton_Click(object? sender, RoutedEventArgs e)
    {
        TestChatGptButton.IsEnabled = false;
        StatusText.Text = "正在读取 ChatGPT 当前额度…";
        try
        {
            var quota = await _chatGptClient.ReadAsync();
            StatusText.Text = quota.PlanName is null ? quota.Message : $"{quota.PlanName} · {quota.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            TestChatGptButton.IsEnabled = true;
        }
    }

    private void SaveChatGptButton_Click(object? sender, RoutedEventArgs e)
    {
        Save(App.Settings with { ChatGptUsageEnabled = ChatGptBox.IsChecked == true });
        StatusText.Text = ChatGptBox.IsChecked == true
            ? "ChatGPT 已开启，关闭设置后会刷新"
            : "ChatGPT 已关闭，关闭设置后会刷新";
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
        _mainWindow?.RefreshWindowMode();
        StatusText.Text = enabled ? "窗口已置于顶层" : "已取消置于顶层";
    }

    private void FontBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontBox.SelectedItem is not ComboBoxItem { Tag: string key })
        {
            return;
        }

        Save(App.Settings with { FontStyle = key });
        App.ApplyFont(key);
        StatusText.Text = $"字体已切换为 {FontBox.SelectedItem}";
    }

    private void WidgetRangeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || WidgetRangeBox.SelectedItem is not ComboBoxItem { Tag: string key })
        {
            return;
        }

        Save(App.Settings with { WidgetRange = key });
        _mainWindow?.RefreshWidgetRange();
        StatusText.Text = $"系统小组件范围：{WidgetRangeBox.SelectedItem}";
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

    private void WidgetModeBox_Click(object? sender, RoutedEventArgs e)
    {
        var enabled = WidgetModeBox.IsChecked == true;
        Save(App.Settings with { WidgetMode = enabled });
        _mainWindow?.RefreshWindowMode();

        StatusText.Text = enabled
            ? "已进入桌面小组件。拖动会记住位置。"
            : "已切回标准窗口。";
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

    private void VibrancyBox_Click(object? sender, RoutedEventArgs e)
    {
        var enabled = VibrancyBox.IsChecked == true;
        Save(App.Settings with { VibrancyEnabled = enabled });
        // 画笔和原生材质都要重来一遍：材质关掉时窗口退回纯色。
        App.ApplyTheme(App.Settings.UseDarkTheme);
        _mainWindow?.RefreshVibrancy();
        App.ApplyWindowVibrancy(this, 18);
        StatusText.Text = enabled ? "已启用系统毛玻璃材质" : "已切回纯色窗口";
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

    /// <summary>
    /// 列表只列主窗口当前时段用过的模型；已勾选但当前时段没用过的也列出来（标注一下），
    /// 否则换个时段就取消不掉了。
    /// </summary>
    private void BuildFilterList()
    {
        FilterModelList.Children.Clear();
        var summaries = _mainWindow?.ModelsForSelectedPeriod() ?? Array.Empty<ModelUsageSummary>();
        var period = _mainWindow?.SelectedPeriodLabel ?? "今日";
        var selected = App.Settings.SelectedModels;
        var selectedSet = new HashSet<string>(selected, StringComparer.Ordinal);

        FilterScopeText.Text = summaries.Count == 0
            ? $"主窗口当前时段（{period}）还没有模型记录，先在主窗口刷新一次。未勾选表示统计全部模型。"
            : $"列表来自主窗口当前时段（{period}）用过的模型，可单选也可多选。未勾选表示统计全部模型。";

        foreach (var item in summaries)
        {
            FilterModelList.Children.Add(CreateFilterRow(
                item.Model,
                $"{FormatTokens(item.TotalTokens)} token",
                selectedSet.Contains(item.Model)));
        }

        foreach (var model in selected.Where(model => !summaries.Any(item => string.Equals(item.Model, model, StringComparison.Ordinal))))
        {
            FilterModelList.Children.Add(CreateFilterRow(model, "当前时段无记录", true));
        }

        if (FilterModelList.Children.Count == 0)
        {
            FilterModelList.Children.Add(new TextBlock
            {
                Text = "没有可筛选的模型",
                FontSize = 11,
                Foreground = Brush("AppMutedBrush"),
                Margin = new Thickness(0, 10, 0, 8)
            });
        }

        UpdateFilterSummary();
    }

    private Control CreateFilterRow(string model, string detail, bool isChecked)
    {
        // CheckBox 的内容区不会拉伸，所以右侧的数量单独放一列，才能对齐到行尾。
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
        var box = new CheckBox
        {
            Tag = model,
            IsChecked = isChecked,
            Content = new TextBlock
            {
                Text = model,
                FontSize = 12,
                MaxWidth = 246,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("AppTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            },
            FontSize = 12,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        box.Click += FilterRow_Click;
        grid.Children.Add(box);

        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 10,
            Foreground = Brush("AppMutedBrush"),
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(detailText, 1);
        grid.Children.Add(detailText);
        return grid;
    }

    private void FilterRow_Click(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveFilterSelection();
    }

    private void FilterSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var box in FilterBoxes())
        {
            box.IsChecked = true;
        }

        SaveFilterSelection();
    }

    private void FilterClear_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var box in FilterBoxes())
        {
            box.IsChecked = false;
        }

        SaveFilterSelection();
    }

    private void FilterRefresh_Click(object? sender, RoutedEventArgs e)
    {
        BuildFilterList();
        StatusText.Text = "筛选列表已按主窗口当前时段更新";
    }

    /// <summary>
    /// 勾选状态就是筛选结果：空集合表示不筛选。改完立刻让主窗口重算，不用等下一次刷新。
    /// </summary>
    private void SaveFilterSelection()
    {
        var models = FilterBoxes()
            .Where(box => box.IsChecked == true && box.Tag is string)
            .Select(box => (string)box.Tag!)
            .ToArray();
        Save(App.Settings with { ModelFilter = models });
        _mainWindow?.ApplyModelFilterAndRender();
        UpdateFilterSummary();
        StatusText.Text = models.Length == 0
            ? "已取消筛选，统计全部模型"
            : $"已筛选 {models.Length} 个模型，其余不计入统计";
    }

    private IEnumerable<CheckBox> FilterBoxes() => FilterModelList.Children
        .OfType<Grid>()
        .Select(grid => grid.Children.OfType<CheckBox>().FirstOrDefault())
        .OfType<CheckBox>();

    private void UpdateFilterSummary()
    {
        var selected = App.Settings.SelectedModels.Count;
        FilterSummaryText.Text = selected == 0
            ? "未勾选 · 统计全部模型"
            : $"已选 {selected} 个模型 · 其余不计入统计";
    }

    private IBrush Brush(string key) => (IBrush)this.FindResource(key)!;

    private static string FormatTokens(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
        >= 10_000 => $"{value / 10_000d:0.##}万",
        _ => value.ToString("N0")
    };

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

    private void SelectWidgetRange(string key)
    {
        foreach (var item in WidgetRangeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == key)
            {
                WidgetRangeBox.SelectedItem = item;
                return;
            }
        }

        WidgetRangeBox.SelectedIndex = 0;
    }

    private void SelectFont(string key)
    {
        foreach (var item in FontBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == key)
            {
                FontBox.SelectedItem = item;
                return;
            }
        }

        FontBox.SelectedIndex = 0;
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
