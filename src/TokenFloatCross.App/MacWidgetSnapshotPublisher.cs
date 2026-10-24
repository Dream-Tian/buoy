using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Buoy;

/// <summary>
/// 把最新用量同时发布给原生 WidgetKit 扩展：本机 HTTP 接口提供数据，
/// 原生桥只负责让 WidgetKit 立刻刷新时间线。
/// 只打包了 dylib 的 macOS 应用才会启用，直接 dotnet run 不受影响。
/// </summary>
internal static class MacWidgetSnapshotPublisher
{
    public const int Port = 47821;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string SnapshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Buoy",
        "widget-snapshot.json");

    private static readonly object Gate = new();
    private static string _latestJson = "{}";
    private static bool _bridgeUnavailable;
    private static bool _serverStarted;

    static MacWidgetSnapshotPublisher()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(MacWidgetSnapshotPublisher).Assembly, ResolveBridge);
    }

    public static void StartServer()
    {
        if (!OperatingSystem.IsMacOS() || _serverStarted)
        {
            return;
        }

        _serverStarted = true;
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(listener));
        }
        catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException)
        {
            // 端口被占用时小组件会显示“未连接”，不影响主程序。
            _serverStarted = false;
        }
    }

    public static void Publish(WidgetUsageSnapshot snapshot)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(snapshot, JsonOptions);
        }
        catch (Exception)
        {
            return;
        }

        lock (Gate)
        {
            _latestJson = json;
        }

        // 再落一份文件，主程序没开时小组件可以读到最后一次快照。
        try
        {
            var folder = Path.GetDirectoryName(SnapshotPath)!;
            Directory.CreateDirectory(folder);
            var temp = SnapshotPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SnapshotPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写缓存失败不影响界面。
        }

        if (_bridgeUnavailable)
        {
            return;
        }

        try
        {
            PublishJson(json);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _bridgeUnavailable = true;
        }
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private static async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            string json;
            lock (Gate)
            {
                json = _latestJson;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            // 客户端提前断开，忽略。
        }
    }

    private static IntPtr ResolveBridge(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "BuoyWidgetBridge")
        {
            return IntPtr.Zero;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "libBuoyWidgetBridge.dylib");
        return NativeLibrary.Load(path);
    }

    [DllImport("BuoyWidgetBridge", EntryPoint = "BuoyWidgetPublish", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PublishJson([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
}

internal sealed record WidgetModelUsage(
    string Model,
    long Tokens,
    long Requests,
    double Share,
    double CostUsd);

internal sealed record WidgetUsageSnapshot(
    long TotalTokens,
    long RequestCount,
    double AverageRpm,
    double AverageTpm,
    long UpdatedAtUnixSeconds,
    bool IsConfigured,
    bool HasError,
    IReadOnlyList<WidgetModelUsage> TopModels,
    double CostUsd,
    string RangeLabel);
