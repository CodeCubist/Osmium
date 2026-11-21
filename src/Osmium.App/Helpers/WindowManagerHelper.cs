using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Osmium.App.Helpers;

public static class WindowManagerHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static void ResizeWithDpi(AppWindow appWindow, Window window, int logicalWidth, int logicalHeight)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96u;
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int physicalWidth = (int)Math.Round(logicalWidth * scale);
            int physicalHeight = (int)Math.Round(logicalHeight * scale);
            appWindow.Resize(new SizeInt32(physicalWidth, physicalHeight));
        }
        catch
        {
        }
    }

    public static void CenterWindowOnScreen(AppWindow appWindow, int fallbackWidth = 1360, int fallbackHeight = 768)
    {
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea == null) return;

            var workArea = displayArea.WorkArea;
            var size = appWindow.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                size = new SizeInt32(fallbackWidth, fallbackHeight);
            }

            int x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
            appWindow.Move(new PointInt32(x, y));
        }
        catch
        {
        }
    }
}