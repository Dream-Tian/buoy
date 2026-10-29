using System.Text.Json;

namespace Buoy.Usage;

/// <summary>
/// 从 WorkBuddy 的本地会话记录里读 token 用量。每条 assistant 消息在
/// providerData.usage 里带 inputTokens / outputTokens / requests，时间戳是毫秒。
///
/// 国际版和国内版是两个独立安装，数据目录不同，分开统计：
///   国际版 ~/.workbuddy-ai/projects → 来源名「WorkBuddy AI」
///   国内版 ~/.workbuddy/projects    → 来源名「WorkBuddy」
///
/// 只读文件，不改动任何东西，也不读对话正文。
/// </summary>
public static class WorkBuddyUsageReader
{
    /// <summary>一个数据来源：显示用的名字 + 用户目录下的文件夹名。</summary>
    public sealed record Source(string Provider, string HomeFolder);

    public static readonly Source International = new("WorkBuddy AI", ".workbuddy-ai");
    public static readonly Source Domestic = new("WorkBuddy", ".workbuddy");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<TokenUsageEvent> ReadRecent(DateTime since, bool international, bool domestic)
    {
        var events = new List<TokenUsageEvent>();
        if (international)
        {
            Collect(International, events);
        }

        if (domestic)
        {
            Collect(Domestic, events);
        }

        // 同一条消息可能在 reasoning / message / function_call 几种事件里各带一份 usage，
        // 按「来源 + messageId」去重，否则 token 会被重复累加。跨文件也要去重（会话可能续写到新文件）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return events
            .Where(item => item.Timestamp.LocalDateTime >= since && seen.Add(item.Id))
            .ToArray();
    }

    private static void Collect(Source source, List<TokenUsageEvent> events)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            source.HomeFolder,
            "projects");
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in paths)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // 这里不按文件修改时间过滤：历史会话的文件可能很久没写过，
            // 但里面的用量仍然属于要统计的时段（比如本月）。时间过滤交给调用方按事件时间做。
            events.AddRange(ReadFile(path, info, source));
        }
    }

    private static IReadOnlyList<TokenUsageEvent> ReadFile(string path, FileInfo info, Source source)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.WriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Events;
            }
        }

        var events = new List<TokenUsageEvent>();
        var session = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (ParseLine(line, session, lineNumber, source) is { } item)
                {
                    events.Add(item);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return events;
        }

        lock (Gate)
        {
            Cache[path] = new CacheEntry(info.Length, info.LastWriteTimeUtc, events);
        }

        return events;
    }

    private static TokenUsageEvent? ParseLine(string line, string session, int lineNumber, Source source)
    {
        if (line.Length < 40 || !line.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("providerData", out var provider) ||
                provider.ValueKind != JsonValueKind.Object ||
                !provider.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = ReadLong(usage, "inputTokens");
            var output = ReadLong(usage, "outputTokens");
            if (input <= 0 && output <= 0)
            {
                return null;
            }

            if (!root.TryGetProperty("timestamp", out var stampElement) ||
                stampElement.ValueKind != JsonValueKind.Number ||
                !stampElement.TryGetInt64(out var stamp) ||
                stamp <= 0)
            {
                return null;
            }

            var model = provider.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = "unknown";
            }

            // 用 messageId 当身份，同一条消息在多种事件里重复出现时只算一次。
            // 前缀用来源文件夹名，两个来源之间不会互相去重。
            var messageId = provider.TryGetProperty("messageId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            var identity = string.IsNullOrWhiteSpace(messageId)
                ? $"{source.HomeFolder}:{session}:{lineNumber}"
                : $"{source.HomeFolder}:{messageId}";

            return new TokenUsageEvent(
                identity,
                source.Provider,
                DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToLocalTime(),
                input,
                output,
                model,
                Quota: 0,
                RequestCount: Math.Max(1, ReadLong(usage, "requests")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private sealed record CacheEntry(long Length, DateTime WriteTimeUtc, IReadOnlyList<TokenUsageEvent> Events);
}
