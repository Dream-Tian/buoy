namespace Buoy.Usage;

public enum UsagePeriod
{
    Today,
    Week,
    Month
}

public sealed record TokenUsageEvent(
    string Id,
    string Provider,
    DateTimeOffset Timestamp,
    long InputTokens,
    long OutputTokens,
    string? Model,
    long Quota = 0,
    long RequestCount = 1)
{
    public string NormalizedModel => string.IsNullOrWhiteSpace(Model) ? "未标注模型" : Model;

    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record UsageRates(long RequestCount, long TotalTokens, double AverageRpm, double AverageTpm);

public sealed record UsageTrend(string Title, IReadOnlyList<UsageTrendPoint> Points);

public sealed record UsageTrendPoint(string Label, long Tokens, long Requests, long Quota);

public sealed record ModelUsageSummary(
    string Model,
    long InputTokens,
    long OutputTokens,
    long Requests,
    long Quota)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// 按本地时间边界汇总所选时段的 Token、请求和平均速率。
/// </summary>
public static class UsageMath
{
    public static UsageRates RatesFor(IEnumerable<TokenUsageEvent> events, UsagePeriod period, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var start = PeriodStart(period, current);
        long requests = 0;
        long tokens = 0;
        foreach (var item in events)
        {
            var timestamp = item.Timestamp.LocalDateTime;
            if (timestamp < start || timestamp > current)
            {
                continue;
            }

            requests += Math.Max(0, item.RequestCount);
            tokens += item.TotalTokens;
        }

        var elapsedMinutes = Math.Max(1d, (current - start).TotalMinutes);
        return new UsageRates(requests, tokens, requests / elapsedMinutes, tokens / elapsedMinutes);
    }

    public static IReadOnlyList<ModelUsageSummary> ModelsFor(
        IEnumerable<TokenUsageEvent> events,
        UsagePeriod period,
        DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var start = PeriodStart(period, current);
        return events
            .Where(item =>
            {
                var timestamp = item.Timestamp.LocalDateTime;
                return timestamp >= start && timestamp <= current;
            })
            .GroupBy(item => item.NormalizedModel)
            .Select(group => new ModelUsageSummary(
                group.Key,
                group.Sum(item => Math.Max(0, item.InputTokens)),
                group.Sum(item => Math.Max(0, item.OutputTokens)),
                group.Sum(item => Math.Max(0, item.RequestCount)),
                group.Sum(item => Math.Max(0, item.Quota))))
            .OrderByDescending(item => item.TotalTokens)
            .ToArray();
    }

    /// <summary>
    /// 今日按小时、本周按星期、本月按日期分桶，未来的格子保持为 0。
    /// </summary>
    public static UsageTrend TrendFor(IEnumerable<TokenUsageEvent> events, UsagePeriod period, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var start = PeriodStart(period, current);
        var end = period switch
        {
            UsagePeriod.Today => start.AddDays(1),
            UsagePeriod.Week => start.AddDays(7),
            _ => start.AddMonths(1)
        };
        var pointCount = period switch
        {
            UsagePeriod.Today => 24,
            UsagePeriod.Week => 7,
            _ => DateTime.DaysInMonth(current.Year, current.Month)
        };
        var tokens = new long[pointCount];
        var requests = new long[pointCount];
        var quota = new long[pointCount];
        foreach (var item in events)
        {
            var timestamp = item.Timestamp.LocalDateTime;
            if (timestamp < start || timestamp >= end)
            {
                continue;
            }

            var index = period switch
            {
                UsagePeriod.Today => timestamp.Hour,
                UsagePeriod.Week => (timestamp.Date - start).Days,
                _ => timestamp.Day - 1
            };
            if (index < 0 || index >= pointCount)
            {
                continue;
            }

            tokens[index] += item.TotalTokens;
            requests[index] += Math.Max(0, item.RequestCount);
            quota[index] += Math.Max(0, item.Quota);
        }

        var weekLabels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        var points = new UsageTrendPoint[pointCount];
        for (var index = 0; index < pointCount; index++)
        {
            var label = period switch
            {
                UsagePeriod.Today => $"{index:00} 时",
                UsagePeriod.Week => weekLabels[index],
                _ => $"{index + 1} 日"
            };
            points[index] = new UsageTrendPoint(label, tokens[index], requests[index], quota[index]);
        }

        var title = period switch
        {
            UsagePeriod.Today => "今日 · 每时",
            UsagePeriod.Week => "本周 · 每日",
            _ => "本月 · 每日"
        };
        return new UsageTrend(title, points);
    }

    private static DateTime PeriodStart(UsagePeriod period, DateTime current) => period switch
    {
        UsagePeriod.Today => current.Date,
        UsagePeriod.Week => current.Date.AddDays(-(((int)current.DayOfWeek + 6) % 7)),
        _ => new DateTime(current.Year, current.Month, 1)
    };
}
