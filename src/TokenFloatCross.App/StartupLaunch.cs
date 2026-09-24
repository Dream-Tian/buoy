using System.Text;
using Microsoft.Win32;

namespace Buoy;

/// <summary>
/// 把当前程序登记为登录后启动。Windows 写当前用户的 Run 项，macOS 写用户的 LaunchAgent。
/// </summary>
public static class StartupLaunch
{
    private const string WindowsValueName = "Buoy";
    private const string MacLabel = "app.buoy.desktop";

    public static bool IsEnabled()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return IsWindowsEnabled();
        }

        if (OperatingSystem.IsMacOS())
        {
            var plist = MacPlistPath();
            return File.Exists(plist) && File.ReadAllText(plist).Contains(executablePath, StringComparison.Ordinal);
        }

        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            SetMac(enabled);
            return;
        }

        throw new PlatformNotSupportedException("当前系统不能登记登录启动项。");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var command = key?.GetValue(WindowsValueName) as string;
        var executablePath = Environment.ProcessPath;
        return string.Equals(command?.Trim().Trim('"'), executablePath, StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("无法打开登录启动项。");
        if (!enabled)
        {
            key.DeleteValue(WindowsValueName, false);
            return;
        }

        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        key.SetValue(WindowsValueName, $"\"{executablePath}\"");
    }

    private static void SetMac(bool enabled)
    {
        var plist = MacPlistPath();
        if (!enabled)
        {
            if (File.Exists(plist))
            {
                File.Delete(plist);
            }

            return;
        }

        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>{MacLabel}</string>
              <key>ProgramArguments</key><array><string>{EscapeXml(executablePath)}</string></array>
              <key>RunAtLoad</key><true/>
            </dict></plist>
            """;
        File.WriteAllText(plist, xml, Encoding.UTF8);
    }

    private static string MacPlistPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        $"{MacLabel}.plist");

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
