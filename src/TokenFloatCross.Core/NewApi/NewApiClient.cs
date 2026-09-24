using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Buoy.Usage;

namespace Buoy.NewApi;

public sealed record NewApiSettings(string BaseUrl, string AccessToken, int UserId)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed record NewApiConnectionResult(bool IsSuccess, string Message, TimeSpan Duration);

/// <summary>
/// 读取 NewAPI 的状态和个人消费汇总。请求本身不依赖 Windows 或 macOS。
/// </summary>
public sealed class NewApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public NewApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<NewApiConnectionResult> TestAsync(NewApiSettings settings, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings, "api/status"));
            ApplyAuth(request, settings);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var elapsed = DateTimeOffset.UtcNow - started;
            return response.IsSuccessStatusCode
                ? new NewApiConnectionResult(true, $"连接成功，HTTP {(int)response.StatusCode}", elapsed)
                : new NewApiConnectionResult(false, $"连接失败，HTTP {(int)response.StatusCode}", elapsed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new NewApiConnectionResult(false, exception.Message, DateTimeOffset.UtcNow - started);
        }
    }

    /// <summary>
    /// 读取覆盖今日、本周、本月的小时汇总。NewAPI 单次跨度不能超过一个月，所以按 30 天切段再合并。
    /// </summary>
    public async Task<IReadOnlyList<TokenUsageEvent>> ReadRecentAsync(
        NewApiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var start = weekStart < monthStart ? weekStart : monthStart;
        var rows = new List<NewApiQuotaData>();
        for (var cursor = start; cursor < now;)
        {
            var chunkEnd = cursor.AddDays(30);
            if (chunkEnd > now)
            {
                chunkEnd = now;
            }

            rows.AddRange(await ReadChunkAsync(settings, cursor, chunkEnd, cancellationToken));
            cursor = chunkEnd;
        }

        return rows
            .GroupBy(row => (row.CreatedAt, row.ModelName))
            .Select(group => group.MaxBy(row => row.TokenUsed + row.Count + row.Quota)!)
            .Select(ToEvent)
            .ToArray();
    }

    private async Task<IReadOnlyList<NewApiQuotaData>> ReadChunkAsync(
        NewApiSettings settings,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(
            settings,
            "api/data/self",
            ("start_timestamp", new DateTimeOffset(start).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("end_timestamp", new DateTimeOffset(end).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyAuth(request, settings);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope<List<NewApiQuotaData>>>(stream, JsonOptions, cancellationToken);
        if (envelope is not { IsSuccess: true })
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope?.Message) ? "NewAPI 返回失败" : envelope.Message);
        }

        return envelope.Data ?? [];
    }

    private static TokenUsageEvent ToEvent(NewApiQuotaData row)
    {
        var model = string.IsNullOrWhiteSpace(row.ModelName) ? "未标注模型" : row.ModelName;
        return new TokenUsageEvent(
            $"newapi-data:{row.CreatedAt}:{model}",
            "NewAPI",
            DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, row.CreatedAt)).ToLocalTime(),
            Math.Max(0, row.TokenUsed),
            0,
            model,
            Quota: Math.Max(0, row.Quota),
            RequestCount: Math.Max(0, row.Count));
    }

    private static void ApplyAuth(HttpRequestMessage request, NewApiSettings settings)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken.Trim());
        if (settings.UserId > 0)
        {
            request.Headers.TryAddWithoutValidation("New-Api-User", settings.UserId.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static Uri BuildUri(NewApiSettings settings, string path, params (string Name, string Value)[] query)
    {
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/') + "/";
        var relative = path.TrimStart('/');
        if (query.Length > 0)
        {
            relative += "?" + string.Join("&", query.Select(item =>
                $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}"));
        }

        return new Uri(new Uri(baseUrl, UriKind.Absolute), relative);
    }

    private sealed class ApiEnvelope<T>
    {
        public bool Success { get; set; }

        public JsonElement Code { get; set; }

        public string Message { get; set; } = string.Empty;

        public T? Data { get; set; }

        public bool IsSuccess => Success ||
            Code.ValueKind == JsonValueKind.True ||
            (Code.ValueKind == JsonValueKind.Number && Code.GetInt32() != 0);
    }

    private sealed class NewApiQuotaData
    {
        [JsonPropertyName("model_name")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; }

        [JsonPropertyName("token_used")]
        public long TokenUsed { get; set; }

        [JsonPropertyName("count")]
        public long Count { get; set; }

        [JsonPropertyName("quota")]
        public long Quota { get; set; }
    }
}
