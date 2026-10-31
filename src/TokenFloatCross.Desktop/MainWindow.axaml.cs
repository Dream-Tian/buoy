using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Buoy.NewApi;
using Buoy.Platform;
using Buoy.Usage;

namespace Buoy;

public partial class MainWindow : Window
{
    private readonly NewApiClient _client = new();
    private readonly AntigravityQuotaClient _quotaClient = new();
    private readonly ChatGptQuotaClient _chatGptClient = new();

    private WidgetQuotaSnapshot? _widgetQuota;
    private WidgetChatGptQuotaSnapshot? _widgetChatGpt;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _widgetSaveTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private ShellMode? _appliedMode;
    private DateTimeOffset _lastPressUtc;
    private Point _lastPressPoint;
    private const int DoubleClickMilliseconds = 420;
    private const double DoubleClickSlop = 6;
    private const double FullWidth = 560;
    private const double FullHeight = 392;
    private const double WidgetWidth = 300;
    private const double WidgetHeight = 156;
    private const double MiniWidth = 180;
    private const double MiniHeight = 68;
    private bool _lastRefreshHadError;
    private DateTimeOffset? _lastSuccessfulRefreshUtc;
    private const double TrendPlotLeft = 40;
    private const double TrendPlotTop = 10;
    private const double TrendPlotRight = 28;
    private const double TrendPlotBottom = 28;

    private UsagePeriod _period = UsagePeriod.Today;
    private IReadOnlyList<TokenUsageEvent> _allEvents = [];
    private IReadOnlyList<TokenUsageEvent> _events = [];
    private IReadOnlyList<UsageTrendPoint> _trendPoints = [];
    private double _trendMaximum;
    private TrendMetric _trendMetric = TrendMetric.Tokens;
    private TrendChartStyle _trendChartStyle = TrendChartStyle.Bars;

    public MainWindow()
    {
        InitializeComponent();
        // 窗口激活时菜单栏显示这套菜单，否则 macOS 那边左边是空的。
        NativeMenu.SetMenu(this, App.BuildWindowMenu());
        OverviewSparkCanvas.SizeChanged += (_, _) => DrawOverviewSpark();
        PointerPressed += (_, eventArgs) =>
        {
            if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (IsInteractiveSource(eventArgs.Source))
            {
                return;
            }

            var position = eventArgs.GetPosition(this);
            var now = DateTimeOffset.UtcNow;
            // 双击自己算：按下就进了系统的窗口拖动循环，DoubleTapped 等不到第二次。
            var isDoubleClick = now - _lastPressUtc <= TimeSpan.FromMilliseconds(DoubleClickMilliseconds)
                                && Math.Abs(position.X - _lastPressPoint.X) <= DoubleClickSlop
                                && Math.Abs(position.Y - _lastPressPoint.Y) <= DoubleClickSlop;

            if (isDoubleClick)
            {
                _lastPressUtc = default;
                ToggleMiniMode();
                return;
            }

            _lastPressUtc = now;
            _lastPressPoint = position;
            BeginMoveDrag(eventArgs);
        };
        Closing += (_, eventArgs) =>
        {
            if (!App.IsExiting)
            {
                eventArgs.Cancel = true;
                Hide();
            }
        };
        UsageTrendCanvas.SizeChanged += (_, _) => DrawTrend();
        _refreshTimer.Tick += async (_, _) =>
        {
            if (App.Settings.RefreshOnlyWhenVisible && !IsVisible)
            {
                return;
            }

            await RefreshAsync();
        };
        ApplyWindowSettings();
        ApplyWindowMode();
        Opened += async (_, _) =>
        {
            ApplyWidgetLevel();
            ApplyWidgetPosition();
            RefreshVibrancy();
            await RefreshAsync();
        };
        PositionChanged += (_, _) =>
        {
            if (!App.Settings.WidgetMode)
            {
                return;
            }

            _widgetSaveTimer.Stop();
            _widgetSaveTimer.Start();
        };
        _widgetSaveTimer.Tick += (_, _) =>
        {
            _widgetSaveTimer.Stop();
            SaveWidgetPosition();
        };
    }

    /// <summary>
    /// 标准窗口、桌面小组件、迷你卡片共用同一份数据，只在布局和窗口尺寸上切换。
    /// </summary>
    public void ApplyWindowMode()
    {
        var mode = CurrentMode();
        var changed = mode != _appliedMode;
        _appliedMode = mode;

        FullShell.IsVisible = mode == ShellMode.Full;
        WidgetShell.IsVisible = mode == ShellMode.Widget;
        MiniShell.IsVisible = mode == ShellMode.Mini;
        Topmost = App.Settings.KeepWindowOnTop;
        RefreshVibrancy();

        if (!changed)
        {
            return;
        }

        switch (mode)
        {
            case ShellMode.Widget:
                Width = WidgetWidth;
                Height = WidgetHeight;
                WindowStartupLocation = WindowStartupLocation.Manual;
                ApplyWidgetPosition();
                break;

            case ShellMode.Mini:
                WindowStartupLocation = WindowStartupLocation.Manual;
                Width = MiniWidth;
                Height = MiniHeight;
                // 首次启动就是迷你模式时窗口还没显示过，直接摆到工作区中间。
                if (!IsVisible)
                {
                    CenterOnWorkingArea();
                }

                RenderMini();
                break;

            default:
                Width = FullWidth;
                Height = FullHeight;
                CenterOnWorkingArea();
                break;
        }
    }

    private static ShellMode CurrentMode() =>
        App.Settings.MiniMode ? ShellMode.Mini
        : App.Settings.WidgetMode ? ShellMode.Widget
        : ShellMode.Full;

    /// <summary>
    /// 指针落在按钮、输入框这类控件上时不当作窗口拖动/双击，交给控件自己处理。
    /// </summary>
    private static bool IsInteractiveSource(object? source)
    {
        for (var node = source as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is Button or TextBox or ScrollBar or ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    public void RefreshWindowMode()
    {
        ApplyWindowMode();
        ApplyWidgetLevel();
    }

    /// <summary>
    /// 窗口外壳的圆角三种模式各不相同，毛玻璃要跟着改，否则方角会露出来。
    /// </summary>
    public void RefreshVibrancy() => App.ApplyWindowVibrancy(this, CurrentMode() switch
    {
        ShellMode.Mini => 12,
        ShellMode.Widget => 20,
        _ => 18
    });

    private void ApplyWidgetLevel()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacWindowLevel.SetDesktopLevel(this, App.Settings.WidgetMode && !App.Settings.KeepWindowOnTop, App.Settings.KeepWindowOnTop);
        }
    }

    private void ApplyWidgetPosition()
    {
        if (!App.Settings.WidgetMode)
        {
            return;
        }

        if (App.Settings.WidgetX is int x && App.Settings.WidgetY is int y)
        {
            Position = new PixelPoint(x, y);
        }
        else if (Screens.Primary is { } screen)
        {
            // 默认放在桌面左下角，避开系统自带的小组件区。
            var area = screen.WorkingArea;
            Position = new PixelPoint(area.X + 28, Math.Max(area.Y + 28, area.Bottom - (int)Height - 40));
        }
    }

    private void CenterOnWorkingArea()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)Width) / 2),
            area.Y + Math.Max(0, (area.Height - (int)Height) / 2));
    }

    private void SaveWidgetPosition()
    {
        if (!App.Settings.WidgetMode)
        {
            return;
        }

        App.Settings = App.Settings with { WidgetX = Position.X, WidgetY = Position.Y };
        App.SettingsStore.Save(App.Settings);
    }

    private void EnterWidget_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings = App.Settings with { WidgetMode = true, MiniMode = false };
        App.SettingsStore.Save(App.Settings);
        RefreshWindowMode();
        SetStatus("已切成桌面小组件，拖到桌面任意位置即可固定。");
    }

    private void ExitWidget_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings = App.Settings with { WidgetMode = false };
        App.SettingsStore.Save(App.Settings);
        RefreshWindowMode();
    }

    /// <summary>标题栏上的迷你按钮。</summary>
    private void EnterMini_Click(object? sender, RoutedEventArgs e) => SetMiniMode(true);

    /// <summary>双击窗口在标准窗口和迷你卡片之间来回切。</summary>
    private void ToggleMiniMode() => SetMiniMode(!App.Settings.MiniMode);

    private void SetMiniMode(bool enabled)
    {
        if (enabled && App.Settings.MiniMode)
        {
            return;
        }

        App.Settings = App.Settings with
        {
            MiniMode = enabled,
            // 迷你卡片和桌面小组件都是缩起来的形态，同时只留一个。
            WidgetMode = enabled ? false : App.Settings.WidgetMode
        };
        App.SettingsStore.Save(App.Settings);
        RefreshWindowMode();
        if (!enabled)
        {
            SetStatus("已切回标准窗口，双击可再缩成迷你卡片。");
        }
    }

    public void ApplyWindowSettings()
    {
        Topmost = App.Settings.KeepWindowOnTop;
        _refreshTimer.Interval = TimeSpan.FromSeconds(App.Settings.NormalizedRefreshSeconds);
        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) => App.ShowSettings();

    public async Task RefreshAfterSettingsAsync()
    {
        ApplyWindowSettings();
        RefreshWindowMode();
        if (App.Settings.IsNewApiConfigured || App.Settings.AntigravityUsageEnabled || App.Settings.ChatGptUsageEnabled)
        {
            await RefreshAsync();
        }
        else
        {
            await ReadQuotaAsync();
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void TabButton_Click(object? sender, RoutedEventArgs e)
    {
        var tag = sender is Button { Tag: string value } ? value : "Overview";
        ShowPage(tag);
    }

    /// <summary>
    /// 切到指定页签，供 buoy://quota 这类外部唤起复用。
    /// </summary>
    public void ShowPage(string tag)
    {
        OverviewPage.IsVisible = tag == "Overview";
        TrendPage.IsVisible = tag == "Trend";
        ModelPage.IsVisible = tag == "Model";
        QuotaPage.IsVisible = tag == "Quota";
        SetActive(OverviewTab, tag == "Overview");
        SetActive(TrendTab, tag == "Trend");
        SetActive(ModelTab, tag == "Model");
        SetActive(QuotaTab, tag == "Quota");
        if (tag == "Trend")
        {
            DrawTrend();
        }
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void PeriodButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out UsagePeriod period))
        {
            return;
        }

        _period = period;
        SetActive(TodayButton, period == UsagePeriod.Today);
        SetActive(WeekButton, period == UsagePeriod.Week);
        SetActive(MonthButton, period == UsagePeriod.Month);
        Render();
    }

    private async Task RefreshAsync()
    {
        var settings = App.Settings;
        SetStatus("正在读取…");
        var started = Stopwatch.StartNew();
        try
        {
            var events = new List<TokenUsageEvent>();
            string? error = null;

            if (settings.IsNewApiConfigured)
            {
                try
                {
                    events.AddRange(await _client.ReadRecentAsync(new NewApiSettings(
                        settings.NewApiBaseUrl,
                        settings.NewApiAccessToken,
                        settings.NewApiUserId)));
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
            }

            if (settings.WorkBuddyUsageEnabled || settings.WorkBuddyCnUsageEnabled)
            {
                // 会话记录可能有好几 MB，放到后台解析，别卡住界面。
                // 窗口给到一年：历史会话的文件很久没写过，但里面的用量仍属于「本月」等时段。
                var since = DateTime.Now.AddDays(-400);
                events.AddRange(await Task.Run(() => WorkBuddyUsageReader.ReadRecent(
                    since,
                    settings.WorkBuddyUsageEnabled,
                    settings.WorkBuddyCnUsageEnabled)));
            }

            _allEvents = events;
            ApplyModelFilter();
            _lastRefreshHadError = error is not null;
            if (error is null)
            {
                _lastSuccessfulRefreshUtc = DateTimeOffset.UtcNow;
            }

            Render();
            SetStatus(error is not null
                ? error.Contains("invalid access token", StringComparison.OrdinalIgnoreCase)
                    ? "NewAPI 拒绝了当前 Token。请在设置里重新粘贴访问令牌并保存。"
                    : error
                : events.Count == 0 && !settings.IsNewApiConfigured && !settings.WorkBuddyUsageEnabled && !settings.WorkBuddyCnUsageEnabled
                    ? "未配置数据来源，点 ⚙ 填写 NewAPI 地址和令牌。"
                    : DescribeEventCount());
        }
        catch (Exception exception)
        {
            _lastRefreshHadError = true;
            SetStatus(exception.Message.Contains("invalid access token", StringComparison.OrdinalIgnoreCase)
                ? "NewAPI 拒绝了当前 Token。请在设置里重新粘贴访问令牌并保存。"
                : exception.Message);
        }
        finally
        {
            started.Stop();
            App.LastRefreshDuration = started.Elapsed;
            RenderWidget();
            RenderMini();
        }

        await ReadQuotaAsync();
    }

    /// <summary>设置页里列表展示的时间段标题。</summary>
    public string SelectedPeriodLabel => PeriodLabel(_period);

    /// <summary>
    /// 当前时段用过的模型，供设置页的筛选列表使用。这里读的是筛选前的全量数据，
    /// 否则勾选一个模型后其他模型就从列表里消失了。
    /// </summary>
    public IReadOnlyList<ModelUsageSummary> ModelsForSelectedPeriod() => UsageMath.ModelsFor(_allEvents, _period);

    /// <summary>设置页改动模型筛选后立即重算，不用等下一次刷新。</summary>
    public void ApplyModelFilterAndRender()
    {
        ApplyModelFilter();
        Render();
        RenderWidget();
        RenderMini();
        if (_allEvents.Count > 0)
        {
            SetStatus(DescribeEventCount());
        }
    }

    /// <summary>
    /// 统计只走筛选后的 _events；全量数据留在 _allEvents 里给筛选列表和重新筛选用。
    /// </summary>
    private void ApplyModelFilter()
    {
        var selected = App.Settings.SelectedModels;
        if (selected.Count == 0)
        {
            _events = _allEvents;
            return;
        }

        var allowed = new HashSet<string>(selected, StringComparer.Ordinal);
        _events = _allEvents
            .Where(item => allowed.Contains(item.QualifiedModel) || allowed.Contains(item.NormalizedModel))
            .ToArray();
    }

    private string DescribeEventCount()
    {
        // 两个来源混在一起：NewAPI 是小时汇总，WorkBuddy 是每条模型调用，所以只说「记录」。
        var text = $"已读取 {_events.Count} 条记录";
        return App.Settings.HasModelFilter
            ? $"{text} · 已筛选 {App.Settings.SelectedModels.Count} 个模型"
            : text;
    }

    private async Task ReadQuotaAsync()
    {
        var antigravity = await ReadAntigravityQuotaAsync();
        var chatGpt = await ReadChatGptQuotaAsync();

        QuotaList.Children.Clear();
        if (antigravity is { } quota)
        {
            for (var index = 0; index < quota.Models.Count; index++)
            {
                QuotaList.Children.Add(CreateQuotaRow(index + 1, quota.Models[index]));
            }

            if (quota.Models.Count == 0)
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = "还没有读到模型配额",
                    FontSize = 11,
                    Foreground = Brush("AppMutedBrush"),
                    Margin = new Thickness(0, 12, 0, 5)
                });
            }

            // 模型环之外的两个月度额度池（接口只有月度口径，没有周限）。
            var creditParts = new List<string>();
            if (quota.AvailableCredits is { } prompt && quota.MonthlyCredits is { } monthlyPrompt)
            {
                creditParts.Add($"Prompt 额度 {prompt:0}/{monthlyPrompt:0}");
            }

            if (quota.AvailableFlowCredits is { } flow && quota.MonthlyFlowCredits is { } monthlyFlow)
            {
                creditParts.Add($"Flow 额度 {flow:0}/{monthlyFlow:0}");
            }

            if (creditParts.Count > 0)
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", creditParts),
                    FontSize = 10,
                    Foreground = Brush("AppMutedBrush"),
                    Margin = new Thickness(0, 8, 0, 2)
                });
            }
        }

        if (chatGpt is { } chatQuota)
        {
            QuotaList.Children.Add(new TextBlock
            {
                Text = "ChatGPT",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("AppTextBrush"),
                Margin = new Thickness(0, antigravity is null ? 0 : 14, 0, 2)
            });
            if (chatQuota.Primary is null && chatQuota.Secondary is null)
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = chatQuota.Message,
                    FontSize = 11,
                    Foreground = Brush("AppMutedBrush"),
                    Margin = new Thickness(0, 4, 0, 5),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                foreach (var window in new[] { chatQuota.Primary, chatQuota.Secondary })
                {
                    if (window is not null)
                    {
                        AddChatGptWindowRow(WindowLabel(window.WindowSeconds), window);
                    }
                }
            }
        }

        if (antigravity is null && chatGpt is null)
        {
            QuotaList.Children.Add(new TextBlock
            {
                Text = "在设置里启用本地 Antigravity 或 ChatGPT(Codex 官方登录),即可读取当前配额。",
                FontSize = 11,
                Foreground = Brush("AppMutedBrush"),
                Margin = new Thickness(0, 12, 0, 5),
                TextWrapping = TextWrapping.Wrap
            });
        }

        QuotaPageSummary.Text = JoinSummary(antigravity, chatGpt);
        QuotaStatus.Text = antigravity?.Message ?? chatGpt?.Message ?? "剩余比例";
        if (_events.Count == 0)
        {
            SetStatus(QuotaStatus.Text);
        }

        PublishWidgetSnapshot();
    }

    private static string JoinSummary(AntigravityQuotaSnapshot? antigravity, ChatGptQuotaSnapshot? chatGpt)
    {
        var parts = new List<string>();
        if (antigravity is { } quota)
        {
            var resetsAt = quota.ResetsAt is { } reset ? $"重置 {reset.LocalDateTime:HH:mm}" : null;
            if (quota.Models.Count > 0)
            {
                parts.Add($"{quota.Models.Count} 个模型");
            }

            if (quota.PlanName is { } plan)
            {
                parts.Add(plan);
            }

            if (resetsAt is { } value)
            {
                parts.Add(value);
            }
        }

        if (chatGpt is { } chat && (chat.Primary is not null || chat.Secondary is not null))
        {
            if (chat.PlanName is { } plan)
            {
                parts.Add(plan);
            }

            foreach (var window in new[] { chat.Primary, chat.Secondary })
            {
                if (window is not { } value)
                {
                    continue;
                }

                var reset = FormatChatGptReset(value);
                parts.Add($"{WindowLabel(value.WindowSeconds)}剩余 {RemainingPercent(value.UsedPercent):0}%");
                if (!string.IsNullOrEmpty(reset))
                {
                    parts.Add(reset);
                }
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>返回 null 表示没有启用 Antigravity 配额读取。</summary>
    private async Task<AntigravityQuotaSnapshot?> ReadAntigravityQuotaAsync()
    {
        if (!App.Settings.AntigravityUsageEnabled)
        {
            _widgetQuota = null;
            return null;
        }

        var quota = await _quotaClient.ReadAsync();
        _widgetQuota = new WidgetQuotaSnapshot(
            quota.Message,
            quota.PlanName,
            (double?)quota.AvailableCredits,
            (double?)quota.MonthlyCredits,
            quota.ResetsAt?.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            quota.Models
                .Select(item => new WidgetQuotaModel(item.Model, item.Label, (double?)item.RemainingFraction))
                .ToArray());
        return quota;
    }

    /// <summary>返回 null 表示没有启用 ChatGPT 配额读取。</summary>
    private async Task<ChatGptQuotaSnapshot?> ReadChatGptQuotaAsync()
    {
        if (!App.Settings.ChatGptUsageEnabled)
        {
            _widgetChatGpt = null;
            return null;
        }

        var chatQuota = await _chatGptClient.ReadAsync();
        _widgetChatGpt = new WidgetChatGptQuotaSnapshot(
            chatQuota.Message,
            chatQuota.PlanName,
            ToWidgetWindow(chatQuota.Primary),
            ToWidgetWindow(chatQuota.Secondary),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return chatQuota;
    }

    private void AddChatGptWindowRow(string title, ChatGptQuotaWindow window)
    {
        var remaining = RemainingPercent(window.UsedPercent);
        var reset = FormatChatGptReset(window);
        var text = $"剩余 {remaining:0}%{(string.IsNullOrEmpty(reset) ? "" : $" · {reset}")}";
        var grid = new Grid { Margin = new Thickness(0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("AppTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var value = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = Brush(remaining <= 20 ? "AppOrangeBrush" : "AppAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        QuotaList.Children.Add(new Border
        {
            Padding = new Thickness(4, 3),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = grid
        });
    }

    private static double RemainingPercent(double? usedPercent) =>
        usedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : 0;

    private static string WindowLabel(long? windowSeconds) => windowSeconds switch
    {
        >= 2_160_000 => "30 天窗口",
        >= 172_800 => "每周窗口",
        >= 3_600 => "5 小时窗口",
        _ => "当前窗口"
    };

    private static string FormatChatGptReset(ChatGptQuotaWindow window)
    {
        if (window.ResetAtUnixSeconds is not { } seconds || seconds <= 0)
        {
            return string.Empty;
        }

        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
        var format = (window.WindowSeconds ?? 0) >= 172800 ? "MM/dd HH:mm" : "HH:mm";
        return $"重置 {reset.ToString(format)}";
    }

    private static WidgetChatGptWindow? ToWidgetWindow(ChatGptQuotaWindow? window) => window is null
        ? null
        : new WidgetChatGptWindow(window.UsedPercent, window.WindowSeconds, window.ResetAtUnixSeconds);

    /// <summary>
    /// 配额行与模型用量同一套排法：左侧序号和名称，右侧剩余比例。
    /// </summary>
    private Control CreateQuotaRow(int rank, AntigravityModelQuota model)
    {
        var label = string.IsNullOrWhiteSpace(model.Label) ? model.Model : model.Label;
        var percent = model.RemainingFraction is { } fraction ? $"{Math.Clamp(fraction, 0m, 1m):P0}" : "未知";
        var low = model.RemainingFraction is <= 0.2m;
        var grid = new Grid { Margin = new Thickness(0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(26)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(new TextBlock
        {
            Text = rank.ToString(),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(rank <= 3 ? "AppOrangeBrush" : "AppSubtleBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var modelText = new StackPanel
        {
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        modelText.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("AppTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320
        });
        if (!string.IsNullOrWhiteSpace(model.Label) && !string.Equals(model.Label, model.Model, StringComparison.Ordinal))
        {
            modelText.Children.Add(new TextBlock
            {
                Text = model.Model,
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 10,
                Foreground = Brush("AppMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            });
        }

        Grid.SetColumn(modelText, 1);
        grid.Children.Add(modelText);
        var value = new TextBlock
        {
            Text = percent,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = Brush(low ? "AppOrangeBrush" : "AppAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var row = new Border
        {
            Padding = new Thickness(4, 3),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = grid
        };
        var hover = Brush("AppAccentSoftBrush");
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private void Render()
    {
        var rates = UsageMath.RatesFor(_events, _period);
        TokensText.Text = FormatCount(rates.TotalTokens);
        RequestsText.Text = rates.RequestCount.ToString("N0");
        RpmText.Text = FormatRate(rates.AverageRpm);
        TpmText.Text = FormatRate(rates.AverageTpm);
        OverviewScopeText.Text = App.Settings.HasModelFilter
            ? $"所选时段 · 筛选 {App.Settings.SelectedModels.Count} 个模型"
            : "所选时段";
        // 消耗直接复用模型页的口径：把所选时段各模型的 quota 加起来，有没定价的就标 ≥。
        var periodModels = UsageMath.ModelsFor(_events, _period);
        CostText.Text = FormatPeriodCost(
            periodModels.Sum(item => item.Quota),
            periodModels.Any(item => item.Quota <= 0 && (item.Requests > 0 || item.TotalTokens > 0)));
        var today = UsageMath.RatesFor(_events, UsagePeriod.Today);
        var todayQuota = _events.Where(item => item.Timestamp.LocalDateTime >= DateTime.Today).Sum(item => item.Quota);
        App.UpdateTrayTip($"今日 {FormatCount(today.TotalTokens)} · {today.RequestCount}次 · 消耗 {FormatUsd(todayQuota / QuotaPerUnit)}");
        RenderModels();
        DrawTrend();
        // 此刻布局可能还没跑完，Bounds 还是 0，等一帧再画。
        Dispatcher.UIThread.Post(DrawOverviewSpark, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 总览页的迷你走势：把当前时段的趋势点画成一条折线（和趋势页一样往 Canvas 里加图形）。
    /// </summary>
    private void DrawOverviewSpark()
    {
        var width = OverviewSparkCanvas.Bounds.Width;
        var height = OverviewSparkCanvas.Bounds.Height;
        if (width < 2 || height < 2)
        {
            return;
        }

        OverviewSparkCanvas.Children.Clear();
        var series = UsageMath.SeriesFor(_events, _period);
        if (series.Count == 0 || series[0].Points.Count < 2)
        {
            return;
        }

        // 只画到"现在"为止，未来的格子是 0，画出来会像掉零。
        var total = series[0].Points.Count;
        var count = Math.Clamp(
            _period switch
            {
                UsagePeriod.Today => DateTime.Now.Hour + 1,
                UsagePeriod.Week => ((int)DateTime.Now.DayOfWeek + 6) % 7 + 1,
                _ => DateTime.Now.Day
            },
            2,
            total);

        var totals = new double[count];
        for (var index = 0; index < count; index++)
        {
            for (var source = 0; source < series.Count; source++)
            {
                totals[index] += series[source].Points[index].Tokens;
            }
        }

        var maximum = totals.Max();
        var colors = SourceColors(series);
        FillLegend(OverviewLegend, series, colors, 5, 9);
        var plotHeight = height - 2;

        // 按来源堆叠，堆起来的高度就是总量。
        var cumulative = new double[series.Count, count];
        var running = new double[count];
        for (var source = 0; source < series.Count; source++)
        {
            for (var index = 0; index < count; index++)
            {
                if (maximum > 0)
                {
                    running[index] += plotHeight * series[source].Points[index].Tokens / maximum;
                }

                cumulative[source, index] = running[index];
            }
        }

        double X(int index) => count <= 1 ? 0 : width * index / (count - 1d);

        for (var source = 0; source < series.Count; source++)
        {
            var band = new Points();
            for (var index = 0; index < count; index++)
            {
                band.Add(new Point(X(index), height - 1 - cumulative[source, index]));
            }

            for (var index = count - 1; index >= 0; index--)
            {
                var below = source == 0 ? 0 : cumulative[source - 1, index];
                band.Add(new Point(X(index), height - 1 - below));
            }

            var color = colors[source];
            OverviewSparkCanvas.Children.Add(new Polygon
            {
                Points = band,
                Fill = new SolidColorBrush(Color.FromArgb(130, color.R, color.G, color.B))
            });
        }

        var outline = new Points();
        for (var index = 0; index < count; index++)
        {
            outline.Add(new Point(X(index), height - 1 - cumulative[series.Count - 1, index]));
        }

        OverviewSparkCanvas.Children.Add(new Polyline
        {
            Points = outline,
            Stroke = Brush("AppAccentBrush"),
            StrokeThickness = 1.6,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round
        });
    }

    /// <summary>
    /// 迷你卡片只留今日 Token 和已知消耗，口径与总览页、托盘提示保持一致。
    /// </summary>
    private void RenderMini()
    {
        if (!App.Settings.IsNewApiConfigured
            && !App.Settings.WorkBuddyUsageEnabled
            && !App.Settings.WorkBuddyCnUsageEnabled)
        {
            MiniTokensText.Text = "--";
            MiniCostText.Text = "--";
            return;
        }

        var today = UsageMath.RatesFor(_events, UsagePeriod.Today);
        var todayQuota = _events
            .Where(item => item.Timestamp.LocalDateTime >= DateTime.Today)
            .Sum(item => item.Quota);
        MiniTokensText.Text = FormatCount(today.TotalTokens);
        MiniCostText.Text = FormatUsd(todayQuota / QuotaPerUnit);
    }

    /// <summary>
    /// 桌面卡片固定显示今日总量和速率，跟主窗口共用同一批事件。
    /// </summary>
    private void RenderWidget()
    {
        var today = UsageMath.RatesFor(_events, UsagePeriod.Today);
        WidgetTokensText.Text = FormatCount(today.TotalTokens);
        WidgetRequestsText.Text = today.RequestCount.ToString("N0");
        WidgetRpmText.Text = FormatRate(today.AverageRpm);
        WidgetTpmText.Text = FormatRate(today.AverageTpm);

        if (!App.Settings.IsNewApiConfigured)
        {
            WidgetCaptionText.Text = "在 Buoy 里配置 NewAPI";
            WidgetCaptionText.Foreground = Brush("AppMutedBrush");
        }
        else if (_lastRefreshHadError)
        {
            WidgetCaptionText.Text = "刷新失败";
            WidgetCaptionText.Foreground = Brush("AppOrangeBrush");
        }
        else
        {
            WidgetCaptionText.Text = "今日 TOKEN";
            WidgetCaptionText.Foreground = Brush("AppMutedBrush");
        }

        PublishWidgetSnapshot();
    }

    /// <summary>
    /// 设置页切换系统小组件时间范围后立即重算并发布。
    /// </summary>
    public void RefreshWidgetRange() => PublishWidgetSnapshot();

    /// <summary>
    /// 系统小组件按设置页选择的时间范围发布数据。
    /// </summary>
    private void PublishWidgetSnapshot()
    {
        var (start, label) = ResolveWidgetRange(App.Settings.WidgetRange);
        var rates = UsageMath.RatesFor(_events, start);
        var models = UsageMath.ModelsFor(_events, start);
        var topModels = models
            .Take(3)
            .Select(item => new WidgetModelUsage(
                item.Model,
                item.TotalTokens,
                item.Requests,
                rates.TotalTokens > 0 ? item.TotalTokens / (double)rates.TotalTokens : 0,
                (double)(item.Quota / QuotaPerUnit)))
            .ToArray();
        var quota = _events
            .Where(item => item.Timestamp.LocalDateTime >= start)
            .Sum(item => item.Quota);

        // 今日每小时 Token,给系统小组件的 medium 尺寸画走势。
        var todayTrend = UsageMath.TrendFor(_events, UsagePeriod.Today)
            .Points
            .Select(point => (double)point.Tokens)
            .ToArray();

        MacWidgetSnapshotPublisher.Publish(new WidgetUsageSnapshot(
            rates.TotalTokens,
            rates.RequestCount,
            rates.AverageRpm,
            rates.AverageTpm,
            _lastSuccessfulRefreshUtc?.ToUnixTimeSeconds() ?? 0,
            App.Settings.IsNewApiConfigured,
            _lastRefreshHadError,
            topModels,
            (double)(quota / QuotaPerUnit),
            label,
            _widgetQuota,
            _widgetChatGpt,
            todayTrend));
    }

    private static (DateTime Start, string Label) ResolveWidgetRange(string key)
    {
        var now = DateTime.Now;
        return key switch
        {
            "week" => (now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)), "本周"),
            "month" => (new DateTime(now.Year, now.Month, 1), "本月"),
            "24h" => (now.AddHours(-24), "最近 24 小时"),
            "7d" => (now.AddDays(-7), "最近 7 天"),
            "30d" => (now.AddDays(-30), "最近 30 天"),
            _ => (now.Date, "今日")
        };
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void TrendMetricButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out TrendMetric metric))
        {
            return;
        }

        _trendMetric = metric;
        SetActive(TrendTokensButton, metric == TrendMetric.Tokens);
        SetActive(TrendRequestsButton, metric == TrendMetric.Requests);
        SetActive(TrendCostButton, metric == TrendMetric.Cost);
        DrawTrend();
    }

    private void TrendStyleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out TrendChartStyle style))
        {
            return;
        }

        _trendChartStyle = style;
        SetActive(TrendBarsButton, style == TrendChartStyle.Bars);
        SetActive(TrendAreaButton, style == TrendChartStyle.Area);
        DrawTrend();
    }

    /// <summary>
    /// 在画布上画坐标轴，再按当前指标画柱状图或面积图。
    /// </summary>
    private void DrawTrend()
    {
        if (UsageTrendCanvas.Bounds.Width <= 1 || UsageTrendCanvas.Bounds.Height <= 1)
        {
            return;
        }

        var trend = UsageMath.TrendFor(_events, _period);
        _trendPoints = trend.Points;
        _trendMaximum = trend.Points.Count == 0 ? 0 : trend.Points.Max(TrendValue);
        TrendTitleText.Text = trend.Title;
        TrendTotalText.Text = _trendMetric switch
        {
            TrendMetric.Tokens => $"总计 {FormatCount(trend.Points.Sum(point => point.Tokens))}",
            TrendMetric.Requests => $"总计 {trend.Points.Sum(point => point.Requests):N0}",
            _ => $"总计 {FormatPeriodCost(trend.Points.Sum(point => point.Quota), trend.Points.Any(point => point.Quota <= 0 && point.Requests > 0))}"
        };
        RestoreTrendPeakText();
        UsageTrendCanvas.Children.Clear();

        var width = UsageTrendCanvas.Bounds.Width;
        var height = UsageTrendCanvas.Bounds.Height;
        var plotWidth = Math.Max(1, width - TrendPlotLeft - TrendPlotRight);
        var plotHeight = Math.Max(1, height - TrendPlotTop - TrendPlotBottom);
        var plotBottom = TrendPlotTop + plotHeight;
        var border = Brush("AppBorderBrush");
        var muted = Brush("AppMutedBrush");

        for (var tick = 0; tick <= 4; tick++)
        {
            var y = TrendPlotTop + tick / 4d * plotHeight;
            UsageTrendCanvas.Children.Add(new Line
            {
                StartPoint = new Point(TrendPlotLeft, y),
                EndPoint = new Point(TrendPlotLeft + plotWidth, y),
                Stroke = border,
                StrokeThickness = 1
            });
            var axisLabel = new TextBlock
            {
                Text = FormatTrendAxis(_trendMaximum * (1 - tick / 4d)),
                Width = 36,
                FontSize = 10,
                TextAlignment = TextAlignment.Right,
                Foreground = muted
            };
            Canvas.SetLeft(axisLabel, 0);
            Canvas.SetTop(axisLabel, y - 7);
            UsageTrendCanvas.Children.Add(axisLabel);
        }

        if (trend.Points.Count == 0 || _trendMaximum <= 0)
        {
            return;
        }

        var labelStride = Math.Max(1, (int)Math.Ceiling(trend.Points.Count / 6d));
        double lastLabelRight = double.NegativeInfinity;
        for (var index = 0; index < trend.Points.Count; index++)
        {
            if (index % labelStride != 0 && index != trend.Points.Count - 1)
            {
                continue;
            }

            var x = TrendPointX(index, trend.Points.Count, plotWidth);
            var left = Math.Clamp(x - 20, TrendPlotLeft - 4, width - 40);
            if (left < lastLabelRight + 4)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = trend.Points[index].Label,
                Width = 40,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                Foreground = muted
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, plotBottom + 6);
            UsageTrendCanvas.Children.Add(label);
            lastLabelRight = left + 40;
        }

        var series = UsageMath.SeriesFor(_events, _period);
        var colors = SourceColors(series);
        RenderTrendLegend(series, colors);

        if (_trendChartStyle == TrendChartStyle.Bars)
        {
            var slot = plotWidth / trend.Points.Count;
            var barWidth = Math.Clamp(slot * 0.64, 3, 54);
            for (var index = 0; index < trend.Points.Count; index++)
            {
                var x = TrendPointX(index, trend.Points.Count, plotWidth);
                var heights = new double[series.Count];
                var lastNonZero = -1;
                for (var source = 0; source < series.Count; source++)
                {
                    heights[source] = SeriesValue(series[source], index) / _trendMaximum * plotHeight;
                    if (heights[source] > 0.5)
                    {
                        lastNonZero = source;
                    }
                }

                // 每个来源一段，从底往上堆；堆起来的高度就是这一格的总量。
                var bottom = plotBottom;
                for (var source = 0; source < series.Count; source++)
                {
                    if (heights[source] <= 0)
                    {
                        continue;
                    }

                    var barHeight = Math.Max(0.8, heights[source]);
                    var bar = new Rectangle
                    {
                        Width = barWidth,
                        Height = barHeight,
                        RadiusX = source == lastNonZero ? 2 : 0,
                        RadiusY = source == lastNonZero ? 2 : 0,
                        Fill = new SolidColorBrush(colors[source])
                    };
                    Canvas.SetLeft(bar, x - barWidth / 2);
                    Canvas.SetTop(bar, bottom - barHeight);
                    UsageTrendCanvas.Children.Add(bar);
                    bottom -= barHeight;
                }
            }

            return;
        }

        // 堆叠面积：每个来源画成一条带，最后给总量描一条轮廓线。
        var cumulative = new double[series.Count, trend.Points.Count];
        var running = new double[trend.Points.Count];
        for (var source = 0; source < series.Count; source++)
        {
            for (var index = 0; index < trend.Points.Count; index++)
            {
                running[index] += SeriesValue(series[source], index) / _trendMaximum * plotHeight;
                cumulative[source, index] = running[index];
            }
        }

        for (var source = 0; source < series.Count; source++)
        {
            var band = new Points();
            for (var index = 0; index < trend.Points.Count; index++)
            {
                band.Add(new Point(
                    TrendPointX(index, trend.Points.Count, plotWidth),
                    plotBottom - cumulative[source, index]));
            }

            for (var index = trend.Points.Count - 1; index >= 0; index--)
            {
                var below = source == 0 ? 0 : cumulative[source - 1, index];
                band.Add(new Point(
                    TrendPointX(index, trend.Points.Count, plotWidth),
                    plotBottom - below));
            }

            var color = colors[source];
            UsageTrendCanvas.Children.Add(new Polygon
            {
                Points = band,
                Fill = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B))
            });
        }

        if (series.Count > 0)
        {
            var outline = new Points();
            for (var index = 0; index < trend.Points.Count; index++)
            {
                outline.Add(new Point(
                    TrendPointX(index, trend.Points.Count, plotWidth),
                    plotBottom - cumulative[series.Count - 1, index]));
            }

            // 总量单独一条主色轮廓，和三个来源色区分开。
            UsageTrendCanvas.Children.Add(new Polyline
            {
                Points = outline,
                Stroke = Brush("AppAccentBrush"),
                StrokeThickness = 2,
                StrokeJoin = PenLineJoin.Round
            });
        }
    }

    private double SeriesValue(UsageTrendSeries series, int index) =>
        index < series.Points.Count ? TrendValue(series.Points[index]) : 0;

    /// <summary>
    /// 趋势页顶部的来源图例。只有一个来源时不显示（颜色没有区分意义）。
    /// </summary>
    private void RenderTrendLegend(IReadOnlyList<UsageTrendSeries> series, IReadOnlyList<Color> colors) =>
        FillLegend(TrendLegend, series, colors, 6, 10);

    /// <summary>
    /// 把来源配色画成一排「色点 + 名称」。总览页和趋势页共用，只是尺寸不同。
    /// </summary>
    private void FillLegend(
        StackPanel target,
        IReadOnlyList<UsageTrendSeries> series,
        IReadOnlyList<Color> colors,
        double dotSize,
        double fontSize)
    {
        target.Children.Clear();
        if (series.Count <= 1)
        {
            target.IsVisible = false;
            return;
        }

        target.IsVisible = true;
        for (var index = 0; index < series.Count; index++)
        {
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            item.Children.Add(new Ellipse
            {
                Width = dotSize,
                Height = dotSize,
                Fill = new SolidColorBrush(colors[index]),
                VerticalAlignment = VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock
            {
                Text = series[index].Provider,
                FontSize = fontSize,
                Foreground = Brush("AppMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            target.Children.Add(item);
        }
    }

    private void UsageTrendCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_trendPoints.Count == 0 || UsageTrendCanvas.Bounds.Width <= 1)
        {
            return;
        }

        var position = e.GetPosition(UsageTrendCanvas);
        var plotWidth = Math.Max(1, UsageTrendCanvas.Bounds.Width - TrendPlotLeft - TrendPlotRight);
        var relative = Math.Clamp((position.X - TrendPlotLeft) / plotWidth, 0, 0.999999);
        var index = _trendChartStyle == TrendChartStyle.Bars
            ? Math.Min((int)(relative * _trendPoints.Count), _trendPoints.Count - 1)
            : Math.Clamp((int)Math.Round(relative * (_trendPoints.Count - 1)), 0, _trendPoints.Count - 1);
        var point = _trendPoints[index];
        TrendPeakText.Text = $"{point.Label} · {FormatCount(point.Tokens)} · {point.Requests}次 · {FormatUsd(point.Quota / QuotaPerUnit)}";
    }

    private void UsageTrendCanvas_PointerExited(object? sender, PointerEventArgs e) => RestoreTrendPeakText();

    private void RestoreTrendPeakText()
    {
        if (_trendMaximum <= 0 || _trendPoints.Count == 0)
        {
            TrendPeakText.Text = "暂无记录";
            return;
        }

        var peak = _trendPoints.MaxBy(TrendValue)!;
        TrendPeakText.Text = _trendMetric switch
        {
            TrendMetric.Tokens => $"峰 {FormatCount(peak.Tokens)}",
            TrendMetric.Requests => $"峰 {peak.Requests:N0}",
            _ => $"峰 {FormatUsd(peak.Quota / QuotaPerUnit)}"
        };
    }

    private double TrendValue(UsageTrendPoint point) => _trendMetric switch
    {
        TrendMetric.Tokens => point.Tokens,
        TrendMetric.Requests => point.Requests,
        _ => (double)(point.Quota / QuotaPerUnit)
    };

    private double TrendPointX(int index, int count, double plotWidth) =>
        _trendChartStyle == TrendChartStyle.Bars
            ? TrendPlotLeft + (index + 0.5) * plotWidth / count
            : TrendPlotLeft + index * plotWidth / Math.Max(1, count - 1);

    // 来源配色：已知来源固定拿一个槽位，这样关掉某个来源时其他来源的颜色不会跟着变。
    private static readonly string[] SourceColorKeys =
    [
        "AppBlueBrush", "AppVioletBrush", "AppOrangeBrush", "AppAccentBrush"
    ];

    private static readonly Dictionary<string, int> SourceColorSlots = new(StringComparer.Ordinal)
    {
        ["NewAPI"] = 0,
        ["WorkBuddy AI"] = 1,
        ["WorkBuddy"] = 2,
        ["Antigravity"] = 3
    };

    private Color PaletteColor(int slot)
    {
        var key = SourceColorKeys[Math.Abs(slot) % SourceColorKeys.Length];
        return ((SolidColorBrush)Brush(key)).Color;
    }

    /// <summary>
    /// 给每个来源分配颜色：已知来源用固定槽位，其余按顺序补空位。
    /// </summary>
    private IReadOnlyList<Color> SourceColors(IReadOnlyList<UsageTrendSeries> series)
    {
        var colors = new Color?[series.Count];
        var used = new HashSet<int>();
        for (var index = 0; index < series.Count; index++)
        {
            if (SourceColorSlots.TryGetValue(series[index].Provider, out var slot))
            {
                colors[index] = PaletteColor(slot);
                used.Add(slot);
            }
        }

        var next = 0;
        for (var index = 0; index < series.Count; index++)
        {
            if (colors[index].HasValue)
            {
                continue;
            }

            while (used.Contains(next))
            {
                next++;
            }

            colors[index] = PaletteColor(next);
            used.Add(next);
        }

        return colors.Select(color => color ?? PaletteColor(0)).ToArray();
    }

    private string FormatTrendAxis(double value) => _trendMetric switch
    {
        TrendMetric.Tokens => FormatCount((long)Math.Round(value)),
        TrendMetric.Requests => ((long)Math.Round(value)).ToString("N0"),
        _ => value >= 100 ? $"${value:0}" : $"${value:0.##}"
    };

    /// <summary>
    /// 按 Token 从高到低画出模型行：左侧序号和输入输出占比，右侧次数与消耗占比。
    /// </summary>
    private void RenderModels()
    {
        var models = UsageMath.ModelsFor(_events, _period);
        var quotaSum = models.Sum(item => item.Quota);
        var hasUnpriced = models.Any(item => item.Quota <= 0 && (item.Requests > 0 || item.TotalTokens > 0));
        ModelPageTitle.Text = $"模型用量 · {PeriodLabel(_period)}";
        ModelPageSummary.Text = App.Settings.HasModelFilter
            ? $"筛选 {App.Settings.SelectedModels.Count} 个 · {FormatPeriodCost(quotaSum, hasUnpriced)}"
            : $"{models.Count} 个模型 · {FormatPeriodCost(quotaSum, hasUnpriced)}";
        ModelList.Children.Clear();
        for (var index = 0; index < models.Count; index++)
        {
            ModelList.Children.Add(CreateModelRow(index + 1, models[index], quotaSum));
        }

        if (models.Count == 0)
        {
            ModelList.Children.Add(new TextBlock
            {
                Text = "当前范围暂无模型记录",
                FontSize = 11,
                Foreground = Brush("AppMutedBrush"),
                Margin = new Thickness(0, 12, 0, 5)
            });
        }
    }

    private Control CreateModelRow(int rank, ModelUsageSummary model, long quotaSum)
    {
        var totalTokens = Math.Max(1, model.TotalTokens);
        var inputPercent = model.InputTokens * 100d / totalTokens;
        var outputPercent = model.OutputTokens * 100d / totalTokens;
        var costShare = model.Quota > 0 && quotaSum > 0
            ? $"{model.Quota * 100m / quotaSum:0.#}%"
            : "未计价";

        var grid = new Grid { Margin = new Thickness(0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(26)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(new TextBlock
        {
            Text = rank.ToString(),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(rank <= 3 ? "AppOrangeBrush" : "AppSubtleBrush"),
            VerticalAlignment = VerticalAlignment.Top
        });

        var modelText = new StackPanel
        {
            Margin = new Thickness(4, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        modelText.Children.Add(new TextBlock
        {
            Text = model.Model,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("AppTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 280
        });
        modelText.Children.Add(new TextBlock
        {
            Text = $"输入 {inputPercent:0.#}% · 输出 {outputPercent:0.#}%",
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 10,
            Foreground = Brush("AppMutedBrush")
        });
        Grid.SetColumn(modelText, 1);
        grid.Children.Add(modelText);

        var values = new TextBlock
        {
            Text = $"{FormatCount(model.TotalTokens)}\n{model.Requests}次 · 消耗 {costShare}",
            FontSize = 11,
            Foreground = Brush("AppAccentBrush"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(values, 2);
        grid.Children.Add(values);

        var row = new Border
        {
            Padding = new Thickness(4, 1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        var hover = Brush("AppAccentSoftBrush");
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private IBrush Brush(string key) => (IBrush)this.FindResource(key)!;

    private async Task ScanLocalIdeAsync()
    {
        await ReadQuotaAsync();
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

    private const decimal QuotaPerUnit = 500_000m;

    private enum TrendMetric
    {
        Tokens,
        Requests,
        Cost
    }

    /// <summary>窗口的三种形态：标准窗口、桌面小组件、迷你卡片。</summary>
    private enum ShellMode
    {
        Full,
        Widget,
        Mini
    }

    private enum TrendChartStyle
    {
        Bars,
        Area
    }

    private static string PeriodLabel(UsagePeriod period) => period switch
    {
        UsagePeriod.Today => "今日",
        UsagePeriod.Week => "本周",
        _ => "本月"
    };

    private static string FormatPeriodCost(long quota, bool hasUnpriced)
    {
        if (quota <= 0)
        {
            return hasUnpriced ? "未计价" : "$0.00";
        }

        var text = FormatUsd(quota / QuotaPerUnit);
        return hasUnpriced ? $"≥{text}" : text;
    }

    private static string FormatUsd(decimal value) => value switch
    {
        >= 1_000m => $"${value:N0}",
        >= 1m => $"${value:0.00}",
        >= 0.01m => $"${value:0.000}",
        > 0m => $"${value:0.0000}",
        _ => "$0.00"
    };

    private static string FormatCount(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
        >= 10_000 => $"{value / 10_000d:0.##}万",
        _ => value.ToString("N0")
    };

    private static string FormatRate(double value) => value switch
    {
        >= 10_000 => $"{value / 10_000d:0.##}万",
        >= 100 => $"{value:0}",
        _ => $"{value:0.##}"
    };
}
