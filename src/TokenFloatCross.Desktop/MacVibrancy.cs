using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Buoy;

/// <summary>
/// 给无边框窗口垫一层系统毛玻璃（NSVisualEffectView）。
/// Avalonia 自带的 TransparencyLevelHint=Blur 在没有标题栏的窗口上会退回 Transparent，
/// 所以这里直接插原生视图，材质、混合方式和圆角都能自己定。
/// </summary>
internal static class MacVibrancy
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSVisualEffectMaterialPopover：浮层用的材质，跟随浅色 / 深色自动变。
    private const long MaterialPopover = 6;

    // NSVisualEffectBlendingModeBehindWindow：模糊窗口背后的内容。
    private const long BlendingBehindWindow = 0;

    // NSVisualEffectStateActive：窗口失焦也保持材质，不会褪成死灰色。
    private const long StateActive = 1;

    // NSViewWidthSizable | NSViewHeightSizable
    private const long ResizeWithWindow = 2 | 16;

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(IntPtr receiver, IntPtr selector, long value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(IntPtr receiver, IntPtr selector, double value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendTwoDoubles(IntPtr receiver, IntPtr selector, double first, double second);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSubview(IntPtr receiver, IntPtr selector, IntPtr view, long positioned, IntPtr relativeTo);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    private static readonly Dictionary<IntPtr, IntPtr> EffectViews = [];

    /// <summary>
    /// 按窗口尺寸铺一层毛玻璃，圆角跟外壳保持一致。重复调用不会叠加视图。
    /// 返回 false 表示没插进去（拿不到原生窗口），调用方应该退回纯色。
    /// </summary>
    public static bool Apply(Window window, double cornerRadius)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var nsWindow = WindowOf(window);
            if (nsWindow == IntPtr.Zero)
            {
                return false;
            }

            var content = SendPointer(nsWindow, sel_registerName("contentView"));
            var host = SendPointer(content, sel_registerName("superview"));
            if (host == IntPtr.Zero)
            {
                return false;
            }

            if (!EffectViews.TryGetValue(nsWindow, out var effect) || effect == IntPtr.Zero)
            {
                var effectClass = objc_getClass("NSVisualEffectView");
                effect = SendPointer(SendPointer(effectClass, sel_registerName("alloc")), sel_registerName("init"));
                if (effect == IntPtr.Zero)
                {
                    return false;
                }

                SendInteger(effect, sel_registerName("setMaterial:"), MaterialPopover);
                SendInteger(effect, sel_registerName("setBlendingMode:"), BlendingBehindWindow);
                SendInteger(effect, sel_registerName("setState:"), StateActive);
                SendInteger(effect, sel_registerName("setAutoresizingMask:"), ResizeWithWindow);
                // NSWindowBelow：垫在 Avalonia 的渲染视图下面。
                SendSubview(host, sel_registerName("addSubview:positioned:relativeTo:"), effect, -1, content);
                EffectViews[nsWindow] = effect;
            }

            SendTwoDoubles(effect, sel_registerName("setFrameOrigin:"), 0, 0);
            SendTwoDoubles(effect, sel_registerName("setFrameSize:"), window.Bounds.Width, window.Bounds.Height);

            // 圆角靠 layer 裁掉，否则方角会从外壳的圆角外面露出来。
            SendInteger(effect, sel_registerName("setWantsLayer:"), 1);
            var layer = SendPointer(effect, sel_registerName("layer"));
            if (layer != IntPtr.Zero)
            {
                SendDouble(layer, sel_registerName("setCornerRadius:"), cornerRadius);
                SendInteger(layer, sel_registerName("setMasksToBounds:"), 1);
            }

            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 原生调用不可用时保持纯色窗口，不影响主流程。
            return false;
        }
    }

    /// <summary>关掉材质时把原生视图摘掉，窗口退回纯色。</summary>
    public static void Remove(Window window)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var nsWindow = WindowOf(window);
            if (nsWindow == IntPtr.Zero || !EffectViews.Remove(nsWindow, out var effect) || effect == IntPtr.Zero)
            {
                return;
            }

            SendVoid(effect, sel_registerName("removeFromSuperview"));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static IntPtr WindowOf(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var target = handle.Handle;
        if (RespondsTo(target, sel_registerName("contentView")))
        {
            return target;
        }

        // Avalonia 给的可能是个 NSView，往上取它所在的 NSWindow。
        var windowSelector = sel_registerName("window");
        return RespondsTo(target, windowSelector) ? SendPointer(target, windowSelector) : IntPtr.Zero;
    }

    private static bool RespondsTo(IntPtr target, IntPtr selector) =>
        SendBoolWithArg(target, sel_registerName("respondsToSelector:"), selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolWithArg(IntPtr receiver, IntPtr selector, IntPtr argument);
}
