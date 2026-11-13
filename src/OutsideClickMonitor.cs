using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace PathDock;

// Observes button presses without intercepting the click or changing its destination.
internal sealed partial class OutsideClickMonitor : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct MouseInfo { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    readonly HookProc callback;
    readonly Action<int, int> clicked;
    readonly object gate = new();
    Dispatcher? dispatcher;
    bool disposed;
    IntPtr hook;
    public OutsideClickMonitor(Action<int, int> clicked)
    {
        this.clicked = clicked; callback = OnMouse;
        var thread = new Thread(Listen) { IsBackground = true, Name = "PathDock mouse monitor" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    void Listen()
    {
        var loop = Dispatcher.CurrentDispatcher;
        lock (gate)
        {
            if (disposed) return;
            dispatcher = loop;
            hook = SetWindowsHookEx(14, callback, IntPtr.Zero, 0);
        }
        try { Dispatcher.Run(); }
        finally
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }
    IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt64() == 0x201 || message.ToInt64() == 0x204 || message.ToInt64() == 0x207 || message.ToInt64() == 0x20B))
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            // This callback runs in the global input path. The recipient must
            // only post work, never wait for the UI or call another application.
            try { clicked(info.X, info.Y); } catch { }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }
}
