using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Buoy;

/// <summary>
/// 把窗口压到桌面层，让小组件像 macOS 桌面组件一样待在其他窗口后面。
/// </summary>
internal static class MacWindowLevel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // 比 kCGDesktopIconWindowLevel(-2147483603) 高一级：压在 Finder 的桌面窗口之上（否则鼠标事件
    // 会被桌面窗口吃掉，窗口看得见却点不动），同时仍远低于普通窗口(0)。
    private const long DesktopIconLevel = -2147483602;
    private const long NormalLevel = 0;
    private const long FloatingLevel = 3;

    // canJoinAllSpaces | stationary | ignoresCycle
    private const ulong DesktopBehavior = 1 | 16 | 64;

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(IntPtr receiver, IntPtr selector, long value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendUnsignedInteger(IntPtr receiver, IntPtr selector, ulong value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolWithArg(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    /// <summary>
    /// 没有 Dock 图标时，窗口显示出来不会自动把应用抬到最前；显式激活一次，
    /// 否则从菜单栏点开的主窗口可能还压在别的应用后面。
    /// </summary>
    public static void ActivateApplication()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var nsApplication = SendPointer(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (nsApplication == IntPtr.Zero)
            {
                return;
            }

            // BOOL 参数只有最低位有效，传 1 就够了。
            SendInteger(nsApplication, sel_registerName("activateIgnoringOtherApps:"), 1);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 原生调用失败时退回普通窗口，不影响主流程。
        }
    }

    /// <summary>
    /// enabled 为 true 时把窗口压到桌面层（桌面卡片用）：层级压到桌面图标之上，
    /// 并带上 canJoinAllSpaces|stationary 让它跨 Space 常驻。
    /// 交互用的主窗口不要走这条路——切 Space 时窗口会被重新合成而闪动。
    /// </summary>
    public static void SetDesktopLevel(Window window, bool enabled, bool topmost = false)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero)
            {
                return;
            }

            var target = handle.Handle;
            var setLevel = sel_registerName("setLevel:");
            if (!RespondsTo(target, setLevel))
            {
                // Avalonia 可能给的是 NSView，往上取它所在的 NSWindow。
                var windowSelector = sel_registerName("window");
                if (!RespondsTo(target, windowSelector))
                {
                    return;
                }

                target = SendPointer(target, windowSelector);
                if (target == IntPtr.Zero)
                {
                    return;
                }
            }

            SendInteger(target, setLevel, enabled ? DesktopIconLevel : (topmost ? FloatingLevel : NormalLevel));
            SendUnsignedInteger(target, sel_registerName("setCollectionBehavior:"), enabled ? DesktopBehavior : 0);
            if (enabled)
            {
                SendVoid(target, sel_registerName("orderFrontRegardless"));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 拿不到原生窗口时保持普通窗口，不影响主流程。
        }
    }

    private static bool RespondsTo(IntPtr target, IntPtr selector) =>
        SendBoolWithArg(target, sel_registerName("respondsToSelector:"), selector);
}
