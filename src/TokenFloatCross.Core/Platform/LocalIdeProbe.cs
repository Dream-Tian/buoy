using System.Diagnostics;

namespace Buoy.Platform;

public sealed record LocalIdeProbeResult(string Summary, IReadOnlyList<string> ProcessNames);

/// <summary>
/// 在 Windows 和 macOS 上查找本机 Antigravity / language server 进程。这里只报告是否在运行，不读取用量。
/// </summary>
public static class LocalIdeProbe
{
    public static async Task<LocalIdeProbeResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var names = Process.GetProcesses()
            .Select(process =>
            {
                try
                {
                    return process.ProcessName;
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .Where(name => name.Contains("language_server", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var listeners = await ReadListenersAsync(cancellationToken);
        var summary = names.Length == 0
            ? "没有发现本机 Antigravity 或 language server 进程"
            : $"发现 {names.Length} 个相关进程" + (listeners > 0 ? $"，约 {listeners} 个本机监听端口" : string.Empty);
        return new LocalIdeProbeResult(summary, names);
    }

    private static async Task<int> ReadListenersAsync(CancellationToken cancellationToken)
    {
        var fileName = OperatingSystem.IsWindows() ? "netstat" : OperatingSystem.IsMacOS() ? "lsof" : string.Empty;
        if (fileName.Length == 0)
        {
            return 0;
        }

        var arguments = OperatingSystem.IsWindows() ? "-ano -p tcp" : "-nP -iTCP -sTCP:LISTEN";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return 0;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return output.Split('\n').Count(line =>
                line.Contains("127.0.0.1", StringComparison.Ordinal) ||
                line.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[::1]", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
