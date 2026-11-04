using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace PathDock;

public sealed record TargetWindow(IntPtr Handle, string Title, string ProcessName, int Pid)
{
    public bool IsTerminal => ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase) || ProcessName.Equals("mintty", StringComparison.OrdinalIgnoreCase);
}

internal static partial class Native
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    public static void KeepAbove(IntPtr window) { if (window != IntPtr.Zero) SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13); }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    public static double WindowScale(IntPtr window) => Math.Max(96, GetDpiForWindow(window)) / 96d;
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    public static double ScaleAtPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) >= 0 ? Math.Max(96, dpi) / 96d : 1;
    }
    public static (int Left, int Top, int Right, int Bottom) WindowBounds(IntPtr window)
    {
        GetWindowRect(window, out var rect); return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
    public static void PositionDockWindow(IntPtr window, int left, int top, int width, int height)
        => SetWindowPos(window, new IntPtr(-1), left, top, width, height, 0x10);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    public static bool HasDescendant(int root, string executable)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new IntPtr(-1)) return false;
        try
        {
            var entries = new System.Collections.Generic.Dictionary<uint, (uint Parent, string Name)>();
            var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>(), Executable = "" };
            if (Process32First(snapshot, ref entry)) do { entries[entry.Id] = (entry.Parent, entry.Executable); } while (Process32Next(snapshot, ref entry));
            foreach (var pair in entries)
            {
                if (!pair.Value.Name.Equals(executable, StringComparison.OrdinalIgnoreCase)) continue;
                var parent = pair.Value.Parent;
                for (var i = 0; i < 32 && entries.TryGetValue(parent, out var ancestor); i++)
                {
                    if (parent == root) return true;
                    if (ancestor.Parent == parent) break; parent = ancestor.Parent;
                }
            }
            return false;
        }
        finally { CloseHandle(snapshot); }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void ConfigureDockFrame(IntPtr window)
    {
        // The visible dock region is smaller than its retained HWND. A DWM frame
        // would still outline that full HWND even when the client area is clipped.
        var disabled = 1; DwmSetWindowAttribute(window, 2, ref disabled, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(window, 34, ref noBorder, sizeof(int));
        var square = 1; DwmSetWindowAttribute(window, 33, ref square, sizeof(int));
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);


    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    public static void RoundEdgeWindow(IntPtr window, int width, int height, int radius, bool leftSide)
    {
        var round = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        var flush = leftSide ? CreateRectRgn(0, 0, width / 2, height + 1) : CreateRectRgn(width / 2, 0, width + 1, height + 1);
        try
        {
            CombineRgn(round, round, flush, 2);
            if (SetWindowRgn(window, round, true) == 0) DeleteObject(round);
        }
        finally { DeleteObject(flush); }
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    public static void SetVisibleRegion(IntPtr window, int x, int y, int width, int height, int radius)
    {
        var region = CreateRoundRectRgn(x, y, x + Math.Max(1, width) + 1, y + Math.Max(1, height) + 1, radius, radius);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(window, region, false) == 0) DeleteObject(region);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SHELLFILEINFO { public IntPtr Icon; public int IconIndex; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHELLFILEINFO info, uint size, uint flags);
    static System.Windows.Media.Imaging.BitmapSource? folderIcon;
    public static System.Windows.Media.Imaging.BitmapSource FolderIcon()
    {
        if (folderIcon != null) return folderIcon;
        SHGetFileInfo("folder", 0x10, out var info, (uint)Marshal.SizeOf<SHELLFILEINFO>(), 0x100 | 0x1 | 0x10);
        if (info.Icon == IntPtr.Zero) throw new InvalidOperationException("无法加载系统文件夹图标。");
        try { folderIcon = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); folderIcon.Freeze(); return folderIcon; }
        finally { DestroyIcon(info.Icon); }
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public UIntPtr extra; }

    public static TargetWindow? ReadTarget()
    {
        var hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var id); if (id == Environment.ProcessId) return null;
        try { using var process = Process.GetProcessById((int)id); var sb = new StringBuilder(1024); GetWindowText(hwnd, sb, sb.Capacity); return new(hwnd, sb.ToString(), process.ProcessName, (int)id); }
        catch { return null; }
    }
    public static void NoActivate(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, -20).ToInt64();
        SetWindowLong(hwnd, -20, new IntPtr(enabled ? style | 0x08000000L : style & ~0x08000000L));
    }
    public static void EnsurePasteAllowed(TargetWindow target)
    {
        if (!IsWindow(target.Handle)) throw new InvalidOperationException("原窗口已关闭，请重新选择目标。");
        using var process = Process.GetProcessById(target.Pid);
        if (!process.ProcessName.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标程序已变化，请重新选择。");
        EnsureTitleUnchanged(target);
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new InvalidOperationException("无法确认目标权限，请使用复制路径。");
        try { if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) throw new InvalidOperationException("无法确认目标权限。"); if (elevated != 0) throw new InvalidOperationException("目标以管理员身份运行，请使用复制路径。"); }
        finally { CloseHandle(token); }
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("请松开 Ctrl、Alt、Shift 等按键后再试。");
    }
    public static void Paste(TargetWindow target)
    {
        if (GetForegroundWindow() != target.Handle) throw new InvalidOperationException("目标窗口未获得焦点，路径已复制，可手动粘贴。");
        EnsureTitleUnchanged(target);
        var events = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        if (SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>()) != events.Length) throw new InvalidOperationException("系统未接受粘贴操作；路径已复制。");
    }
    static void EnsureTitleUnchanged(TargetWindow target)
    {
        var text = new StringBuilder(1024); GetWindowText(target.Handle, text, text.Capacity);
        if (text.ToString() != target.Title) throw new InvalidOperationException("窗口或标签标题已变化，请重新选择目标后再插入。");
    }
    static INPUT Key(ushort key, bool up) => new() { type = 1, data = new InputUnion { keyboard = new KEYBDINPUT { key = key, flags = up ? 2u : 0u } } };
}
