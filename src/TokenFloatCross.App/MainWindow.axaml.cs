using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Buoy.NewApi;
using Buoy.Platform;
using Buoy.Usage;

namespace Buoy;

public partial class MainWindow : Window
{
    private readonly NewApiClient _client = new();
    private readonly AntigravityQuotaClient _quotaClient = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _widgetSaveTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _widgetApplied;
    private bool _lastRefreshHadError;
    private DateTimeOffset? _lastSuccessfulRefreshUtc;
    private const double TrendPlotLeft = 40;
    private const double TrendPlotTop = 10;
    private const double TrendPlotRight = 28;
    private const double TrendPlotBottom = 28;

    private UsagePeriod _period = UsagePeriod.Today;
    private IReadOnlyList<TokenUsageEvent> _events = [];
    private IReadOnlyList<UsageTrendPoint> _trendPoints = [];
    private double _trendMaximum;
    private TrendMetric _trendMetric = TrendMetric.Tokens;
    private TrendChartStyle _trendChartStyle = TrendChartStyle.Bars;

    public MainWindow()
    {
        InitializeComponent();
        PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(eventArgs);
            }
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
        ApplyWidgetMode();
        Opened += async (_, _) =>
        {
            ApplyWidgetLevel();
            ApplyWidgetPosition();
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
    /// 标准窗口和桌面小组件共用同一份数据，只在布局和窗口层级上切换。
    /// </summary>
    public void ApplyWidgetMode()
    {
        var widget = App.Settings.WidgetMode;
        var changed = widget != _widgetApplied;
        _widgetApplied = widget;

        FullShell.IsVisible = !widget;
        WidgetShell.IsVisible = widget;
        Topmost = App.Settings.KeepWindowOnTop;

        if (!changed)
        {
            return;
        }

        if (widget)
        {
            Width = 300;
            Height = 156;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ApplyWidgetPosition();
        }
        else
        {
            Width = 560;
            Height = 392;
            CenterOnWorkingArea();
        }
    }

    public void RefreshWindowMode()
    {
        ApplyWidgetMode();
        ApplyWidgetLevel();
    }

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
        App.Settings = App.Settings with { WidgetMode = true };
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
        if (App.Settings.IsNewApiConfigured || App.Settings.AntigravityUsageEnabled)
        {
            await RefreshAsync();
        }
        else
        {
            await ReadQuotaAsync();
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Hide();

    private void TabButton_Click(object? sender, RoutedEventArgs e)
    {
        var tag = sender is Button { Tag: string value } ? value : "Overview";
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
            if (settings.IsNewApiConfigured)
            {
                _events = await _client.ReadRecentAsync(new NewApiSettings(
                    settings.NewApiBaseUrl,
                    settings.NewApiAccessToken,
                    settings.NewApiUserId));
                _lastSuccessfulRefreshUtc = DateTimeOffset.UtcNow;
                _lastRefreshHadError = false;
                SetStatus($"已读取 {_events.Count} 条小时汇总");
                Render();
            }
            else
            {
                _lastRefreshHadError = false;
                SetStatus("未配置 NewAPI，点 ⚙ 填写地址和令牌。");
            }
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
        }

        await ReadQuotaAsync();
    }

    private async Task ReadQuotaAsync()
    {
        if (!App.Settings.AntigravityUsageEnabled)
        {
            QuotaPageSummary.Text = string.Empty;
            QuotaStatus.Text = "在设置中启用本地 Antigravity，即可读取当前配额。";
            QuotaList.Children.Clear();
            return;
        }

        var quota = await _quotaClient.ReadAsync();
        var credits = quota.AvailableCredits is null
            ? null
            : quota.MonthlyCredits is { } monthly
                ? $"积分 {quota.AvailableCredits:0} / {monthly:0}"
                : $"积分 {quota.AvailableCredits:0}";
        QuotaPageSummary.Text = quota.Models.Count == 0
            ? quota.PlanName ?? string.Empty
            : string.Join(" · ", new[] { $"{quota.Models.Count} 个模型", quota.PlanName, credits }.Where(item => !string.IsNullOrWhiteSpace(item)));
        QuotaStatus.Text = quota.Message;
        QuotaList.Children.Clear();
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

        if (_events.Count == 0)
        {
            SetStatus(quota.Message);
        }
    }

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
        var today = UsageMath.RatesFor(_events, UsagePeriod.Today);
        var todayQuota = _events.Where(item => item.Timestamp.LocalDateTime >= DateTime.Today).Sum(item => item.Quota);
        App.UpdateTrayTip($"今日 {FormatCount(today.TotalTokens)} · {today.RequestCount}次 · 消耗 {FormatUsd(todayQuota / QuotaPerUnit)}");
        RenderModels();
        DrawTrend();
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
            label));
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

        var accent = TrendAccent();
        if (_trendChartStyle == TrendChartStyle.Bars)
        {
            var slot = plotWidth / trend.Points.Count;
            var barWidth = Math.Clamp(slot * 0.64, 3, 54);
            for (var index = 0; index < trend.Points.Count; index++)
            {
                var barHeight = Math.Max(1, TrendValue(trend.Points[index]) / _trendMaximum * plotHeight);
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(bar, TrendPointX(index, trend.Points.Count, plotWidth) - barWidth / 2);
                Canvas.SetTop(bar, plotBottom - barHeight);
                UsageTrendCanvas.Children.Add(bar);
            }

            return;
        }

        var line = new Points();
        for (var index = 0; index < trend.Points.Count; index++)
        {
            var x = TrendPointX(index, trend.Points.Count, plotWidth);
            var y = plotBottom - TrendValue(trend.Points[index]) / _trendMaximum * plotHeight;
            line.Add(new Point(x, y));
        }

        var area = new Points { new Point(line[0].X, plotBottom) };
        foreach (var point in line)
        {
            area.Add(point);
        }

        area.Add(new Point(line[^1].X, plotBottom));
        UsageTrendCanvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B))
        });
        UsageTrendCanvas.Children.Add(new Polyline
        {
            Points = line,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            StrokeJoin = PenLineJoin.Round
        });
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

    private Color TrendAccent()
    {
        var key = _trendMetric switch
        {
            TrendMetric.Tokens => "AppBlueBrush",
            TrendMetric.Requests => "AppVioletBrush",
            _ => "AppOrangeBrush"
        };
        return ((SolidColorBrush)Brush(key)).Color;
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
        ModelPageSummary.Text = $"{models.Count} 个模型 · {FormatPeriodCost(quotaSum, hasUnpriced)}";
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
