using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;

namespace Buoy.Platform;

public sealed record AntigravityModelQuota(string Model, string? Label, decimal? RemainingFraction);

public sealed record AntigravityQuotaSnapshot(
    string Message,
    string? PlanName,
    decimal? AvailableCredits,
    decimal? MonthlyCredits,
    DateTimeOffset? ResetsAt,
    IReadOnlyList<AntigravityModelQuota> Models,
    decimal? AvailableFlowCredits = null,
    decimal? MonthlyFlowCredits = null);

/// <summary>
/// 通过本机 Language Server 读取 Antigravity 配额。Windows 用 netstat，macOS 用 lsof，请求本身相同。
/// </summary>
public sealed class AntigravityQuotaClient
{
    private const string EndpointPrefix = "/exa.language_server_pb.LanguageServerService/";
    private readonly HttpClient _httpClient = new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<AntigravityQuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var processes = ParseProcesses(await ReadCommandLinesAsync(cancellationToken));
        if (processes.Count == 0)
        {
            return new AntigravityQuotaSnapshot("Antigravity 未运行。请先打开并登录 IDE，再点刷新。", null, null, null, null, []);
        }

        var listeners = ParseListeners(await ReadListenersAsync(cancellationToken));
        string? failure = null;
        foreach (var process in processes)
        {
            foreach (var listener in listeners.Where(item => item.ProcessId == process.ProcessId))
            {
                foreach (var scheme in new[] { "https", "http" })
                {
                    var uri = new UriBuilder(scheme, listener.Address, listener.Port).Uri;
                    var result = await TryStatusAsync(uri, process.CsrfToken, cancellationToken);
                    if (result is not null)
                    {
                        return result;
                    }

                    failure ??= "已找到本地服务，但配额接口没有返回可用数据";
                }
            }
        }

        return new AntigravityQuotaSnapshot(failure ?? "已找到 Antigravity 进程，尚未发现本机监听端口", null, null, null, null, []);
    }

    private async Task<AntigravityQuotaSnapshot?> TryStatusAsync(Uri baseUri, string csrf, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, EndpointPrefix + "GetUserStatus"))
            {
                Content = JsonContent.Create(new { metadata = new { ideName = "antigravity", extensionName = "antigravity", locale = "en" } })
            };
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return Parse(document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    internal static AntigravityQuotaSnapshot Parse(JsonElement root)
    {
        var userStatus = Object(root, "userStatus") ?? root;
        var planStatus = Object(userStatus, "planStatus");
        var planInfo = Object(planStatus, "planInfo") ?? Object(userStatus, "planInfo") ?? Object(root, "planInfo");
        var cascade = Object(userStatus, "cascadeModelConfigData") ?? Object(root, "cascadeModelConfigData");
        var models = new List<AntigravityModelQuota>();
        DateTimeOffset? resetsAt = null;
        if (Property(cascade, "clientModelConfigs") is { ValueKind: JsonValueKind.Array } configs)
        {
            foreach (var config in configs.EnumerateArray())
            {
                var alias = Object(config, "modelOrAlias");
                var model = Text(config, "modelId") ?? Text(alias, "model") ?? Text(alias, "alias");
                var quota = Object(config, "quotaInfo");
                if (model is null || quota is null)
                {
                    continue;
                }

                // 禁用模型（如 gemini-4-argon-disabled）的 remainingFraction 恒为 0，
                // 会把小组件里模型组的最低值拉到 0%，这里直接剔除。
                if (Property(config, "disabled") is { ValueKind: JsonValueKind.True }
                    || model.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fraction = Property(quota, "remainingFraction") is null ? 0 : Decimal(quota, "remainingFraction");
                if (fraction is < 0 or > 1)
                {
                    fraction = null;
                }

                if (Text(quota, "resetTime") is { } resetText
                    && DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset)
                    && (resetsAt is null || reset < resetsAt))
                {
                    resetsAt = reset;
                }

                models.Add(new AntigravityModelQuota(model, Text(config, "label"), fraction));
            }
        }

        var planName = Text(planInfo, "planName") ?? Text(planStatus, "planName");
        var monthly = Decimal(planInfo, "monthlyPromptCredits");
        var available = Decimal(planStatus, "availablePromptCredits") ?? Decimal(planInfo, "availablePromptCredits");
        var message = planName is null && models.Count == 0 ? "Antigravity 返回未包含配额信息" : "配额已读取";
        return new AntigravityQuotaSnapshot(
            message,
            planName,
            available,
            monthly,
            resetsAt,
            models,
            Decimal(planStatus, "availableFlowCredits"),
            Decimal(planInfo, "monthlyFlowCredits"));
    }

    private static async Task<string> ReadCommandLinesAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await RunAsync(
                "powershell",
                ["-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name LIKE 'language_server%'\" | ForEach-Object { $_.ProcessId.ToString() + \"`t\" + $_.CommandLine }"],
                cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await RunAsync("ps", ["-ax", "-o", "pid=,command="], cancellationToken);
        }

        return string.Empty;
    }

    private static async Task<string> ReadListenersAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await RunAsync("netstat", ["-ano"], cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await RunAsync("lsof", ["-nP", "-iTCP", "-sTCP:LISTEN"], cancellationToken);
        }

        return string.Empty;
    }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return string.Empty;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }

    internal static IReadOnlyList<(int ProcessId, string CsrfToken)> ParseProcesses(string output)
    {
        var result = new List<(int, string)>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('\t');
            string command;
            int processId;
            if (separator > 0 && int.TryParse(line[..separator].Trim(), out processId))
            {
                command = line[(separator + 1)..];
            }
            else
            {
                var trimmed = line.TrimStart();
                var space = trimmed.IndexOf(' ');
                if (space <= 0 || !int.TryParse(trimmed[..space], out processId))
                {
                    continue;
                }

                command = trimmed[space..];
            }

            if (!command.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
                !command.Contains("csrf_token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var csrf = ReadArgument(command, "csrf_token");
            if (processId > 0 && !string.IsNullOrWhiteSpace(csrf))
            {
                result.Add((processId, csrf));
            }
        }

        return result;
    }

    internal static IReadOnlyList<(int ProcessId, string Address, int Port)> ParseListeners(string output)
    {
        var listeners = new List<(int, string, int)>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 5 &&
                    fields[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(fields[4], out var processId) &&
                    IPEndPoint.TryParse(fields[1], out var endpoint) &&
                    IPAddress.IsLoopback(endpoint.Address))
                {
                    listeners.Add((processId, endpoint.Address.ToString(), endpoint.Port));
                }

                continue;
            }

            var listen = line.IndexOf("(LISTEN)", StringComparison.OrdinalIgnoreCase);
            if (listen < 0)
            {
                continue;
            }

            var fieldsMac = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fieldsMac.Length < 2 || !int.TryParse(fieldsMac[1], out var macPid))
            {
                continue;
            }

            var addressField = fieldsMac.LastOrDefault(item => item.Contains(':') && item.Contains('.')) ?? string.Empty;
            var colon = addressField.LastIndexOf(':');
            if (colon > 0 && int.TryParse(addressField[(colon + 1)..], out var port) &&
                (addressField.Contains("127.0.0.1", StringComparison.Ordinal) || addressField.Contains("[::1]", StringComparison.Ordinal)))
            {
                listeners.Add((macPid, addressField[..colon].Trim('[', ']'), port));
            }
        }

        return listeners;
    }

    private static string? ReadArgument(string command, string name)
    {
        var marker = "--" + name;
        var index = command.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var valueStart = index + marker.Length;
        if (valueStart < command.Length && command[valueStart] == '=')
        {
            valueStart++;
        }
        else if (valueStart < command.Length && command[valueStart] == ' ')
        {
            valueStart++;
        }
        else
        {
            return null;
        }

        if (valueStart < command.Length && command[valueStart] == '"')
        {
            var end = command.IndexOf('"', valueStart + 1);
            return end > valueStart ? command[(valueStart + 1)..end] : null;
        }

        var space = command.IndexOf(' ', valueStart);
        return space < 0 ? command[valueStart..] : command[valueStart..space];
    }

    private static JsonElement? Property(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static JsonElement? Object(JsonElement? parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    private static string? Text(JsonElement? parent, string name)
    {
        var property = Property(parent, name);
        var text = property?.ValueKind switch
        {
            JsonValueKind.String => property.Value.GetString(),
            JsonValueKind.Number => property.Value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? Decimal(JsonElement? parent, string name)
    {
        var text = Text(parent, name);
        return text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static HttpClientHandler CreateHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri is { IsLoopback: true }
    };
}
