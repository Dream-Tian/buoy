using System.Runtime.InteropServices;

namespace Buoy;

/// <summary>
/// Windows 托盘：左键打开主窗口，右键弹出圆角菜单。
/// </summary>
internal static class WindowsTrayIcon
{
    private const int WmUser = 0x0400;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int Callback = WmUser + 20;
    private const uint BiRgb = 0;

    private static readonly WndProcDelegate Proc = WindowProc;
    private static IntPtr _window;
    private static IntPtr _icon;

    public static void Install()
    {
        if (!OperatingSystem.IsWindows() || _window != IntPtr.Zero)
        {
            return;
        }

        var className = "BuoyTray";
        var windowClass = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            lpszClassName = className
        };
        RegisterClass(ref windowClass);
        _window = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _icon = CreateIcon();
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = Callback,
            hIcon = _icon,
            szTip = "Buoy"
        };
        Shell_NotifyIcon(NimAdd, ref data);
    }

    public static void UpdateTip(string text)
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            uFlags = NifTip,
            szTip = text.Length > 127 ? text[..127] : text
        };
        Shell_NotifyIcon(NimModify, ref data);
    }

    public static void Remove()
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1
        };
        Shell_NotifyIcon(NimDelete, ref data);
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Callback)
        {
            var mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse == WmLButtonUp)
            {
                App.ShowMain();
            }
            else if (mouse == WmRButtonUp)
            {
                App.ShowTrayMenu();
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr CreateIcon()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = (y * size + x) * 4;
                var inside = x is > 3 and < 28 && y is > 6 and < 26;
                var bar = inside && y is > 16 and < 21 && x is > 7 and < 24;
                pixels[index] = bar ? (byte)69 : (byte)255;
                pixels[index + 1] = bar ? (byte)122 : (byte)255;
                pixels[index + 2] = bar ? (byte)31 : (byte)255;
                pixels[index + 3] = inside ? (byte)255 : (byte)0;
            }
        }

        var header = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);
}
