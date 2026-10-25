using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Buoy.Platform;

public sealed record ChatGptQuotaWindow(double? UsedPercent, long? WindowSeconds, long? ResetAtUnixSeconds);

public sealed record ChatGptQuotaSnapshot(
    string Message,
    string? PlanName,
    bool LoggedIn,
    ChatGptQuotaWindow? Primary,
    ChatGptQuotaWindow? Secondary);

/// <summary>
/// 通过 Codex 的官方 ChatGPT 登录读取额度：凭据在 ~/.codex/auth.json，
/// 额度接口是 chatgpt.com/backend-api/wham/usage（5 小时窗口 + 周窗口）。
/// 自定义 NewAPI 或纯 API Key 登录拿不到这些数据。
/// </summary>
public sealed class ChatGptQuotaClient
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<ChatGptQuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (ReadAuthTokens() is not { } credentials)
        {
            return new ChatGptQuotaSnapshot("未找到官方登录。请在 Codex 里用 ChatGPT 账号登录（codex login）。", null, false, null, null);
        }

        var (accessToken, accountId, refreshToken) = credentials;
        var usage = await TryUsageAsync(accessToken, accountId, cancellationToken);
        if (usage is { Snapshot: not null })
        {
            return usage.Snapshot;
        }

        if (usage is not { AuthRejected: true } || refreshToken is null)
        {
            return new ChatGptQuotaSnapshot(
                usage is { AuthRejected: true } ? "登录已过期，请在 Codex 里重新登录 ChatGPT 账号。" : "ChatGPT 额度接口暂时不可用",
                null, false, null, null);
        }

        var refreshed = await TryRefreshAsync(refreshToken, cancellationToken);
        if (refreshed is null)
        {
            return new ChatGptQuotaSnapshot("登录已过期，请在 Codex 里重新登录 ChatGPT 账号。", null, false, null, null);
        }

        PersistTokens(refreshed.Value);
        usage = await TryUsageAsync(refreshed.Value.accessToken, accountId, cancellationToken);
        return usage is { Snapshot: not null } ? usage.Snapshot
            : new ChatGptQuotaSnapshot("ChatGPT 额度接口没有返回可用数据", null, true, null, null);
    }

    private async Task<(ChatGptQuotaSnapshot? Snapshot, bool AuthRejected)> TryUsageAsync(string accessToken, string? accountId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (null, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return (Parse(document.RootElement), false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return (null, false);
        }
    }

    private async Task<(string accessToken, string? refreshToken, string? idToken)?> TryRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = OAuthClientId,
                    ["refresh_token"] = refreshToken
                })
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var access = Text(document.RootElement, "access_token");
            if (access is null)
            {
                return null;
            }

            return (access, Text(document.RootElement, "refresh_token") ?? refreshToken, Text(document.RootElement, "id_token"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// 刷新成功后把新 token 写回 auth.json，保持 Codex 自己的文件可用（老 refresh token 可能被轮换作废）。
    /// </summary>
    private static void PersistTokens((string accessToken, string? refreshToken, string? idToken) refreshed)
    {
        var path = AuthFilePath;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            var tokens = node?["tokens"];
            if (node is null || tokens is null)
            {
                return;
            }

            tokens["access_token"] = refreshed.accessToken;
            if (refreshed.refreshToken is not null)
            {
                tokens["refresh_token"] = refreshed.refreshToken;
            }

            if (refreshed.idToken is not null)
            {
                tokens["id_token"] = refreshed.idToken;
            }

            node["last_refresh"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var temp = path + ".tmp";
            File.WriteAllText(temp, node.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // 写回失败只影响下次刷新，本次读取不受影响。
        }
    }

    internal static (string AccessToken, string? AccountId, string? RefreshToken)? ReadAuthTokens()
    {
        try
        {
            var path = AuthFilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (Property(root, "OPENAI_API_KEY") is { ValueKind: JsonValueKind.String } && tokens(root) is null)
            {
                // 纯 API Key 登录不走 ChatGPT 订阅额度。
                return null;
            }

            if (tokens(root) is not { } tokenNode)
            {
                return null;
            }

            var access = Text(tokenNode, "access_token");
            if (string.IsNullOrWhiteSpace(access))
            {
                return null;
            }

            var accountId = Text(tokenNode, "account_id") ?? AccountIdFromJwt(Text(tokenNode, "id_token"));
            return (access, accountId, Text(tokenNode, "refresh_token"));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        static JsonElement? tokens(JsonElement root) =>
            root.TryGetProperty("tokens", out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    }

    /// <summary>
    /// account_id 缺失时从 id_token 的 https://api.openai.com/auth 声明里取。
    /// </summary>
    internal static string? AccountIdFromJwt(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '='));
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            if (document.RootElement.TryGetProperty("https://api.openai.com/auth", out var claim)
                && claim.ValueKind == JsonValueKind.Object)
            {
                return Text(claim, "chatgpt_account_id");
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }

        return null;
    }

    internal static ChatGptQuotaSnapshot Parse(JsonElement root)
    {
        var plan = Text(root, "plan_type");
        var planName = plan is null ? null : plan switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "free" => "Free",
            "team" => "Team",
            "enterprise" => "Enterprise",
            _ => plan
        };
        if (Property(root, "rate_limit") is not { } rateLimit)
        {
            return new ChatGptQuotaSnapshot("ChatGPT 返回未包含额度信息", planName, true, null, null);
        }

        var primary = Window(Property(rateLimit, "primary_window"));
        var secondary = Window(Property(rateLimit, "secondary_window"));
        if (primary is null && secondary is null)
        {
            return new ChatGptQuotaSnapshot("ChatGPT 返回未包含额度窗口", planName, true, null, null);
        }

        return new ChatGptQuotaSnapshot("配额已读取", planName, true, primary, secondary);
    }

    private static ChatGptQuotaWindow? Window(JsonElement? parent)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } window)
        {
            return null;
        }

        var used = Decimal(window, "used_percent");
        double? usedPercent = used is null ? null : (double)Math.Clamp(used.Value, 0m, 100m);
        return new ChatGptQuotaWindow(
            usedPercent,
            Long(window, "limit_window_seconds"),
            Long(window, "reset_at"));
    }

    private static string AuthFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    private static JsonElement? Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var property) ? property : null;

    private static string? Text(JsonElement parent, string name)
    {
        if (Property(parent, name) is not { } property)
        {
            return null;
        }

        var text = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? Decimal(JsonElement parent, string name)
    {
        var text = Text(parent, name);
        return text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? Long(JsonElement parent, string name)
    {
        var text = Text(parent, name);
        return text is not null && long.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
