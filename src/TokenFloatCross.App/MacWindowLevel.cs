using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Buoy;

/// <summary>
/// 把窗口压到桌面层，让小组件像 macOS 桌面组件一样待在其他窗口后面。
/// </summary>
internal static class MacWindowLevel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // kCGDesktopIconWindowLevel + 1，压在桌面图标之上、普通窗口之下。
    private const long DesktopIconLevel = -2147483608;
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
