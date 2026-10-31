using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Buoy;

/// <summary>
/// 让窗口跟着当前 Space 显示，并在唤起时把应用激活。
/// </summary>
internal static class MacWindowLevel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSWindowCollectionBehaviorMoveToActiveSpace：窗口显示时跟到当前 Space，
    // 而不是把用户切回它上次所在的那个 Space。
    private const ulong MoveToActiveSpace = 2;

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(IntPtr receiver, IntPtr selector, long value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendUnsignedInteger(IntPtr receiver, IntPtr selector, ulong value);

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
    /// 让窗口显示时跟到当前 Space。不设的话窗口会留在上次所在的 Space，
    /// 从别处点「显示」会先把用户切回那个 Space。
    /// </summary>
    public static void SetFollowsActiveSpace(Window window)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var target = NativeWindow(window);
            if (target != IntPtr.Zero)
            {
                SendUnsignedInteger(target, sel_registerName("setCollectionBehavior:"), MoveToActiveSpace);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static IntPtr NativeWindow(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var target = handle.Handle;
        if (RespondsTo(target, sel_registerName("setLevel:")))
        {
            return target;
        }

        // Avalonia 可能给的是 NSView，往上取它所在的 NSWindow。
        var windowSelector = sel_registerName("window");
        return RespondsTo(target, windowSelector) ? SendPointer(target, windowSelector) : IntPtr.Zero;
    }

    private static bool RespondsTo(IntPtr target, IntPtr selector) =>
        SendBoolWithArg(target, sel_registerName("respondsToSelector:"), selector);
}
