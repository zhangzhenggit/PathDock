using System;
using System.Runtime.InteropServices;
namespace PathDock;
internal static partial class Native
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr region, out RECT rectangle);
    public static (int Width, int Height, bool NativeFrameDisabled) DockRegionState(IntPtr window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(window, region) == 0) return (0, 0, false);
            GetRgnBox(region, out var rect);
            var result = DwmGetWindowAttribute(window, 1, out var enabled, sizeof(int));
            return (rect.Right - rect.Left, rect.Bottom - rect.Top, result >= 0 && enabled == 0);
        }
        finally { DeleteObject(region); }
    }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
    public static bool TailContourMatchesEdge(IntPtr window, double scale)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try {
            if (GetWindowRgn(window, region) == 0) return false;
            GetRgnBox(region, out var rect);
            return !PtInRegion(region, rect.Left + (int)Math.Round(scale), rect.Top + (int)Math.Round(4 * scale));
        } finally { DeleteObject(region); }
    }
    public static bool CoveredByWindow(IntPtr window, IntPtr covering)
    {
        if (!GetWindowRect(window, out var rect)) return false;
        var x = (rect.Left + rect.Right) / 2; var y = (rect.Top + rect.Bottom) / 2;
        foreach (var point in new[] { new POINT { X=x,Y=y },new POINT { X=rect.Left+1,Y=y },new POINT { X=rect.Right-1,Y=y },new POINT { X=x,Y=rect.Top+1 },new POINT { X=x,Y=rect.Bottom-1 } })
            if (WindowFromPoint(point) != covering) return false;
        return true;
    }
    public static bool ReceivesPointerAtCenter(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var hit = WindowFromPoint(new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 });
        GetWindowThreadProcessId(hit, out var pid);
        return pid == Environment.ProcessId;
    }
}
