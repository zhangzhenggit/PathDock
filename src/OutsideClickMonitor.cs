using System;
using System.Runtime.InteropServices;

namespace PathDock;

// Observes button presses without intercepting the click or changing its destination.
internal sealed class OutsideClickMonitor : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct MouseInfo { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    readonly HookProc callback;
    readonly Action<int, int> clicked;
    IntPtr hook;
    public OutsideClickMonitor(Action<int, int> clicked)
    {
        this.clicked = clicked; callback = OnMouse;
        hook = SetWindowsHookEx(14, callback, IntPtr.Zero, 0);
    }
    IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt64() == 0x201 || message.ToInt64() == 0x204 || message.ToInt64() == 0x207 || message.ToInt64() == 0x20B))
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            clicked(info.X, info.Y);
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
}
