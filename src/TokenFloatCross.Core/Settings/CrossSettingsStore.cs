using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Buoy.Settings;

public sealed record CrossAppSettings(
    string NewApiBaseUrl = "",
    string NewApiAccessToken = "",
    int NewApiUserId = 0,
    bool UseDarkTheme = false,
    int RefreshIntervalSeconds = 30,
    bool RefreshOnlyWhenVisible = false,
    bool KeepWindowOnTop = false,
    bool AntigravityUsageEnabled = false,
    bool ChatGptUsageEnabled = false,
    bool AutoUpdateCheck = true,
    string ManifestUrl = "https://github.com/Dream-Tian/TokenFloat/releases/latest/download/update-manifest.json",
    DateTimeOffset? UpdateLastCheckedUtc = null,
    bool LaunchAtLogin = false,
    bool WidgetMode = false,
    int? WidgetX = null,
    int? WidgetY = null,
    string FontStyle = "system",
    string WidgetRange = "today",
    IReadOnlyList<string>? ModelFilter = null,
    bool VibrancyEnabled = true,
    bool WorkBuddyUsageEnabled = true)
{
    public bool IsNewApiConfigured =>
        !string.IsNullOrWhiteSpace(NewApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(NewApiAccessToken);

    public int NormalizedRefreshSeconds =>
        RefreshIntervalSeconds is 10 or 30 or 60 or 300 ? RefreshIntervalSeconds : 30;

    /// <summary>
    /// 只统计这些模型；空列表表示统计全部模型。
    /// </summary>
    public IReadOnlyList<string> SelectedModels => ModelFilter ?? [];

    public bool HasModelFilter => ModelFilter is { Count: > 0 };
}

/// <summary>
/// 把配置放在各系统的本地应用数据目录。Windows 上 Token 用 DPAPI，其他系统先用当前用户文件保存，钥匙串后续再接。
/// </summary>
public sealed class CrossSettingsStore
{
    private const string ProtectedPrefix = "dpapi:v1:";
    private static readonly byte[] TokenFloatEntropy = Encoding.UTF8.GetBytes("TokenFloat.NewApiAccessToken.v1");
    private readonly string _path;

    public CrossSettingsStore()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(local, "Buoy");
        var legacy = Path.Combine(local, "TokenFloatCross", "app-settings.json");
        Directory.CreateDirectory(folder);
        var settingsPath = Path.Combine(folder, "app-settings.json");
        if (!File.Exists(settingsPath) && File.Exists(legacy))
        {
            File.Copy(legacy, settingsPath);
        }

        _path = settingsPath;
        DataFolder = folder;
    }

    public string DataFolder { get; }

    public CrossAppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ImportTokenFloatSettings() ?? new CrossAppSettings();
            }

            var json = File.ReadAllText(_path);
            var stored = JsonSerializer.Deserialize<StoredSettings>(json);
            if (stored is null)
            {
                return ImportTokenFloatSettings() ?? new CrossAppSettings();
            }

            var settings = FromStored(stored);
            if (!json.Contains("\"AntigravityUsageEnabled\"", StringComparison.Ordinal))
            {
                var imported = ImportTokenFloatSettings();
                if (imported is not null)
                {
                    settings = settings with { AntigravityUsageEnabled = imported.AntigravityUsageEnabled };
                }
            }
            if (!settings.IsNewApiConfigured)
            {
                var imported = ImportTokenFloatSettings();
                if (imported is { IsNewApiConfigured: true })
                {
                    settings = imported with { UseDarkTheme = settings.UseDarkTheme };
                    Save(settings);
                }
            }

            return settings;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new CrossAppSettings();
        }
    }

    public void Save(CrossAppSettings settings)
    {
        var stored = new StoredSettings
        {
            NewApiBaseUrl = settings.NewApiBaseUrl.Trim(),
            NewApiAccessTokenProtected = Protect(settings.NewApiAccessToken.Trim()),
            NewApiUserId = Math.Max(0, settings.NewApiUserId),
            UseDarkTheme = settings.UseDarkTheme,
            RefreshIntervalSeconds = settings.NormalizedRefreshSeconds,
            RefreshOnlyWhenVisible = settings.RefreshOnlyWhenVisible,
            KeepWindowOnTop = settings.KeepWindowOnTop,
            AntigravityUsageEnabled = settings.AntigravityUsageEnabled,
            ChatGptUsageEnabled = settings.ChatGptUsageEnabled,
            AutoUpdateCheck = settings.AutoUpdateCheck,
            ManifestUrl = settings.ManifestUrl.Trim(),
            UpdateLastCheckedUtc = settings.UpdateLastCheckedUtc,
            LaunchAtLogin = settings.LaunchAtLogin,
            WidgetMode = settings.WidgetMode,
            WidgetX = settings.WidgetX,
            WidgetY = settings.WidgetY,
            FontStyle = NormalizeFont(settings.FontStyle),
            WidgetRange = NormalizeRange(settings.WidgetRange),
            ModelFilter = NormalizeModels(settings.ModelFilter)?.ToArray(),
            VibrancyEnabled = settings.VibrancyEnabled,
            WorkBuddyUsageEnabled = settings.WorkBuddyUsageEnabled
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 读取已安装的 Windows 版 TokenFloat 配置，避免再填一遍地址和 Token。
    /// </summary>
    private static CrossAppSettings? ImportTokenFloatSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat",
            "app-settings.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path));
            if (stored is null)
            {
                return null;
            }

            return FromStored(stored);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static string Protect(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() ? ProtectMac(token) : "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        }

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), TokenFloatEntropy, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("plain:", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value["plain:".Length..]));
        }

        if (value == "keychain:v1" && OperatingSystem.IsMacOS())
        {
            return UnprotectMac();
        }

        if (!OperatingSystem.IsWindows() || !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, TokenFloatEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private static string ProtectMac(string token)
    {
        if (RunSecurity(null, "add-generic-password", "-U", "-s", "Buoy", "-a", Environment.UserName, "-w", token))
        {
            return "keychain:v1";
        }

        return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
    }

    private static string UnprotectMac()
    {
        var output = RunSecurityOutput("find-generic-password", "-s", "Buoy", "-a", Environment.UserName, "-w");
        return output?.Trim() ?? string.Empty;
    }

    private static bool RunSecurity(string? ignored, params string[] arguments) => RunSecurityOutput(arguments) is not null;

    private static string? RunSecurityOutput(params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "security";
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static CrossAppSettings FromStored(StoredSettings stored) => new(
        stored.NewApiBaseUrl ?? string.Empty,
        Unprotect(stored.NewApiAccessTokenProtected),
        Math.Max(0, stored.NewApiUserId),
        stored.UseDarkTheme,
        stored.RefreshIntervalSeconds is 10 or 30 or 60 or 300 ? stored.RefreshIntervalSeconds : 30,
        stored.RefreshOnlyWhenVisible,
        stored.KeepWindowOnTop,
        stored.AntigravityUsageEnabled,
        stored.ChatGptUsageEnabled,
        stored.AutoUpdateCheck,
        string.IsNullOrWhiteSpace(stored.ManifestUrl)
            ? new CrossAppSettings().ManifestUrl
            : stored.ManifestUrl.Trim(),
        stored.UpdateLastCheckedUtc,
        stored.LaunchAtLogin,
        stored.WidgetMode,
        stored.WidgetX,
        stored.WidgetY,
        NormalizeFont(stored.FontStyle),
        NormalizeRange(stored.WidgetRange),
        NormalizeModels(stored.ModelFilter),
        stored.VibrancyEnabled,
        stored.WorkBuddyUsageEnabled);

    private sealed class StoredSettings
    {
        public string? NewApiBaseUrl { get; set; }

        public string? NewApiAccessTokenProtected { get; set; }

        public int NewApiUserId { get; set; }

        public bool UseDarkTheme { get; set; }

        public int RefreshIntervalSeconds { get; set; } = 30;

        public bool RefreshOnlyWhenVisible { get; set; }

        public bool KeepWindowOnTop { get; set; }

        public bool AntigravityUsageEnabled { get; set; }

        public bool ChatGptUsageEnabled { get; set; }

        public bool AutoUpdateCheck { get; set; } = true;

        public string? ManifestUrl { get; set; }

        public DateTimeOffset? UpdateLastCheckedUtc { get; set; }

        public bool LaunchAtLogin { get; set; }

        public bool WidgetMode { get; set; }

        public int? WidgetX { get; set; }

        public int? WidgetY { get; set; }

        public string? FontStyle { get; set; }

        public string? WidgetRange { get; set; }

        public string[]? ModelFilter { get; set; }

        public bool VibrancyEnabled { get; set; } = true;

        public bool WorkBuddyUsageEnabled { get; set; } = true;
    }

    public static string NormalizeFont(string? key) => key is "rounded" or "song" or "kai" or "mono" ? key : "system";

    public static string NormalizeRange(string? key) =>
        key is "week" or "month" or "24h" or "7d" or "30d" ? key : "today";

    /// <summary>去空白、去重；空集合按「不筛选」处理，存成 null。</summary>
    public static IReadOnlyList<string>? NormalizeModels(IReadOnlyList<string>? models)
    {
        if (models is null)
        {
            return null;
        }

        var cleaned = models
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
