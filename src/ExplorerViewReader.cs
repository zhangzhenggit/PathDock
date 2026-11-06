using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PathDock;

internal sealed class ExplorerReader : IDisposable
{
    readonly object shell;
    readonly object windows;
    public ExplorerReader()
    {
        shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try { windows = ((dynamic)shell).Windows(); }
        catch { Marshal.ReleaseComObject(shell); throw; }
    }
    public List<ExplorerLocation> Read(IntPtr requestedFrame = default)
    {
        var locations = new List<ExplorerLocation>();
        var foreground = DialogFollowNative.GetForegroundWindow();
        for (var i = 0; i < (int)((dynamic)windows).Count; i++)
        {
            object? window = null, document = null, folder = null, self = null, browser = null;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                window = ((dynamic)windows).Item(i);
                var frame = new IntPtr((long)((dynamic)window).HWND);
                if (requestedFrame != IntPtr.Zero && frame != requestedFrame) continue;
                if (DialogFollowNative.Class(frame) is not ("CabinetWClass" or "ExploreWClass")) continue;
                var service = (IComServiceProvider)window;
                var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iid = new Guid("000214E2-0000-0000-C000-000000000046");
                var hr = service.QueryService(ref sid, ref iid, out pointer);
                var browserWindow = IntPtr.Zero;
                if (hr >= 0 && pointer != IntPtr.Zero)
                {
                    browser = Marshal.GetObjectForIUnknown(pointer);
                    ((IShellBrowserWindow)browser).GetWindow(out browserWindow);
                }
                var browserClass = DialogFollowNative.Class(browserWindow);
                // Inactive Windows 11 tabs may still report WS_VISIBLE. The first tab child is the active browser.
                var active = browserClass == "ShellTabWindowClass"
                    ? DialogFollowNative.FindWindowEx(frame, IntPtr.Zero, "ShellTabWindowClass", null) == browserWindow
                    : browserWindow == frame;
                if (!active || !DialogFollowNative.IsWindowVisible(browserWindow)) continue;
                document = ((dynamic)window).Document;
                folder = ((dynamic)document).Folder;
                self = ((dynamic)folder).Self;
                string path = ((dynamic)self).Path;
                bool isFileSystem = ((dynamic)self).IsFileSystem;
                bool isFolder = ((dynamic)self).IsFolder;
                locations.Add(new(frame.ToInt64(), browserWindow.ToInt64(), browserClass, active && DialogFollowNative.IsWindowVisible(browserWindow), path, isFileSystem, frame == foreground, hr, isFolder));
            }
            catch (Exception ex) { locations.Add(new(0, 0, "error", false, ex.GetType().Name, false, false, ex.HResult)); }
            finally
            {
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                foreach (var value in new[] { self, folder, document, browser, window }) if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            }
        }
        return locations;
    }
    public void Dispose()
    {
        Marshal.ReleaseComObject(windows); Marshal.ReleaseComObject(shell);
    }
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
}
[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowserWindow
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
}
