using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Buoy;

public partial class TrayMenuWindow : Window
{
    public TrayMenuWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Close();
    }

    public void OpenAtCursor()
    {
        var cursor = TopLevel.GetTopLevel(this) is null
            ? default
            : default(PixelPoint);
        if (OperatingSystem.IsWindows() && TryGetCursor(out var point))
        {
            cursor = new PixelPoint(point.X, point.Y);
        }
        else if (Screens.Primary is { } screen)
        {
            cursor = new PixelPoint(screen.WorkingArea.Right - 160, screen.WorkingArea.Bottom - 80);
        }

        Position = new PixelPoint(cursor.X - 4, cursor.Y - 52);
        Show();
        Activate();
    }

    private void ShowButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
        App.ShowMain();
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
        App.IsExiting = true;
        if (Application.Current?.ApplicationLifetime is ClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static bool TryGetCursor(out CursorPoint point)
    {
        point = default;
        return GetCursorPos(out point);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private struct CursorPoint
    {
        public int X;
        public int Y;
    }
}
