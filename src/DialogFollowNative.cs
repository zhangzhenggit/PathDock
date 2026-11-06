using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PathDock;

internal sealed record FollowDialog(DialogIdentity Identity, IntPtr Filename, IntPtr AddressToolbar, IntPtr[] AddressWindows, string? AddressLabel);
internal sealed record FollowDraft(string Filename, IntPtr Focus, int SelectionStart, int SelectionEnd, uint InputTime);

internal static class DialogFollowNative
{
    static readonly int[] heldInputKeys = { 1, 2, 0x10, 0x11, 0x12, 0x5B, 0x5C };
    internal delegate void EventCallback(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time);
    delegate bool Enumerate(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, EventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? name);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, Enumerate callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref InputInfo info);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int type, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("oleacc.dll", PreserveSig = false)] static extern void AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object accessible);
    [StructLayout(LayoutKind.Sequential)] struct InputInfo { internal uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] struct GuiInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    internal static string Class(IntPtr window)
    {
        var text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString();
    }
    internal static int ProcessId(IntPtr window) { GetWindowThreadProcessId(window, out var pid); return (int)pid; }
    static IntPtr Focus(IntPtr window)
    {
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) ? info.Focus : IntPtr.Zero;
    }
    internal static uint InputTime()
    {
        var info = new InputInfo { Size = (uint)Marshal.SizeOf<InputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : uint.MaxValue;
    }
    internal static bool InputBusy() => heldInputKeys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    static List<IntPtr> Children(IntPtr window)
    {
        var result = new List<IntPtr>(); EnumChildWindows(window, (child, _) => { result.Add(child); return true; }, IntPtr.Zero); return result;
    }
    static IEnumerable<string> Ancestors(IntPtr window, IntPtr root)
    {
        for (var parent = GetParent(window); parent != IntPtr.Zero && parent != root; parent = GetParent(parent)) yield return Class(parent);
    }
    static bool AddressEdit(IntPtr window, IntPtr root) => DialogFollowRules.IsAddressEdit(Class(window), IsWindowVisible(window), GetAncestor(window, 2) == root, Ancestors(window, root));
    internal static FollowDialog? Describe(IntPtr window)
    {
        if (!IsWindow(window) || Class(window) != "#32770" || !IsWindowVisible(window) || !IsWindowEnabled(window)) return null;
        var children = Children(window);
        if (!children.Any(h => Class(h) is "Breadcrumb Parent" or "BreadcrumbParent")) return null;
        var address = children.FirstOrDefault(h => Class(h) == "ToolbarWindow32" && GetDlgCtrlID(h) == 1001
            && Ancestors(h, window).Contains("Address Band Root"));
        var filename = children.Where(h => Class(h) == "Edit" && IsWindowVisible(h) && GetDlgCtrlID(h) is 1001 or 1148 or 1152 && !AddressEdit(h, window)).ToArray();
        if (address == IntPtr.Zero || filename.Length > 1) return null;
        using var process = Process.GetProcessById(ProcessId(window));
        if (process.Id == Environment.ProcessId || !SameElevation(process)) return null;
        var header = children.Where(h => Ancestors(h, window).Contains("Address Band Root") || Class(h) == "Address Band Root").ToArray();
        return new(new(window.ToInt64(), process.Id, process.StartTime.ToUniversalTime().Ticks), filename.FirstOrDefault(), address, header, AddressName(address));
    }
    static bool SameElevation(Process process)
    {
        using var current = Process.GetCurrentProcess();
        return Elevation(current.Handle, out var mine) && Elevation(process.Handle, out var theirs) && mine == theirs;
    }
    static bool Elevation(IntPtr process, out int elevation)
    {
        elevation = 0;
        if (!OpenProcessToken(process, 8, out var token)) return false;
        try { return GetTokenInformation(token, 20, out elevation, sizeof(int), out _); }
        finally { CloseHandle(token); }
    }
    internal static bool IdentityMatches(FollowDialog dialog)
    {
        var window = new IntPtr(dialog.Identity.Window);
        if (!IsWindow(window) || ProcessId(window) != dialog.Identity.Process) return false;
        try { using var process = Process.GetProcessById(dialog.Identity.Process); return process.StartTime.ToUniversalTime().Ticks == dialog.Identity.Started; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    internal static bool FilenameMatches(FollowDialog dialog) => IsWindow(dialog.Filename)
        && ProcessId(dialog.Filename) == dialog.Identity.Process
        && GetAncestor(dialog.Filename, 2) == new IntPtr(dialog.Identity.Window);
    static bool Text(IntPtr window, out string text)
    {
        text = "";
        if (window == IntPtr.Zero) return true;
        var buffer = new StringBuilder(32768);
        if (SendMessageTimeout(window, 0xD, new IntPtr(buffer.Capacity), buffer, 2, 75, out var count) == IntPtr.Zero || count.ToUInt64() >= (ulong)buffer.Capacity - 1) return false;
        text = buffer.ToString(); return true;
    }
    internal static FollowDraft? Capture(FollowDialog dialog)
    {
        var inputTime = InputTime();
        var root = new IntPtr(dialog.Identity.Window); var focus = Focus(root);
        if (focus == IntPtr.Zero || AddressEdit(focus, root) || !Text(dialog.Filename, out var name)) return null;
        SendMessageTimeout(dialog.Filename, 0xB0, IntPtr.Zero, IntPtr.Zero, 2, 75, out var selection);
        var packed = selection.ToUInt64();
        return InputTime() == inputTime ? new(name, focus, (int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF), inputTime) : null;
    }
    internal static bool FolderUnchanged(FollowDialog dialog) => dialog.AddressLabel != null && AddressName(dialog.AddressToolbar) == dialog.AddressLabel;
    internal static bool DraftUnchanged(FollowDialog dialog, FollowDraft draft) => InputTime() == draft.InputTime && Text(dialog.Filename, out var text) && text == draft.Filename;

    // Submit a verified folder path through the dialog's native navigation handler.
    // Redraw suppression covers only the filename field while its draft is borrowed.
    internal static bool Navigate(FollowDialog dialog, FollowDraft draft, string path, Func<bool> valid)
    {
        var root = new IntPtr(dialog.Identity.Window);
        var clock = Stopwatch.StartNew();
        var folderText = path.TrimEnd('\\') + "\\";
        var filenameBorrowed = false;
        var redrawSuppressed = false;
        bool Active() => clock.ElapsedMilliseconds < 850 && valid() && GetForegroundWindow() == root
            && InputTime() == draft.InputTime && !InputBusy();
        try
        {
            if (!Active() || !IdentityMatches(dialog) || !FilenameMatches(dialog) || !DraftUnchanged(dialog, draft)) return false;
            redrawSuppressed = true;
            if (SendMessageTimeout(dialog.Filename, 0xB, IntPtr.Zero, IntPtr.Zero, 2, 100, out _) == IntPtr.Zero) return false;
            filenameBorrowed = true;
            if (SendMessageTimeout(dialog.Filename, 0xC, IntPtr.Zero, new StringBuilder(folderText), 2, 100, out _) == IntPtr.Zero) return false;
            if (!Active() || !Text(dialog.Filename, out var typed) || typed != folderText) return false;
            // Complete the command before restoring the draft. A queued command could otherwise read the restored filename.
            // This runs on the single background worker, never on PathDock's dispatcher.
            SendMessage(root, 0x111, new IntPtr(1), IntPtr.Zero);
            while (Active())
            {
                if (LocationMatches(dialog.AddressToolbar, path)) return true;
                Thread.Sleep(10);
            }
            return false;
        }
        finally
        {
            try
            {
                if (filenameBorrowed && IdentityMatches(dialog) && FilenameMatches(dialog) && Text(dialog.Filename, out var current)
                    && (current == folderText || current.Length == 0))
                    SendMessageTimeout(dialog.Filename, 0xC, IntPtr.Zero, new StringBuilder(draft.Filename), 2, 100, out _);
            }
            finally
            {
                if (redrawSuppressed && FilenameMatches(dialog))
                {
                    PostMessage(dialog.Filename, 0xB, new IntPtr(1), IntPtr.Zero);
                    RedrawWindow(dialog.Filename, IntPtr.Zero, IntPtr.Zero, 1);
                }
            }
            if (valid() && GetForegroundWindow() == root && DraftUnchanged(dialog, draft)
                && IsWindow(draft.Focus) && GetAncestor(draft.Focus, 2) == root)
            {
                SendMessageTimeout(root, 0x28, draft.Focus, new IntPtr(1), 2, 75, out _);
                if (draft.Focus == dialog.Filename)
                    PostMessage(dialog.Filename, 0xB1, new IntPtr(draft.SelectionStart), new IntPtr(draft.SelectionEnd));
            }
        }
    }
    static bool LocationMatches(IntPtr toolbar, string path) => AddressName(toolbar) is string name && DialogFollowRules.AddressLabelMatches(name, path);
    static string? AddressName(IntPtr toolbar)
    {
        object? accessible = null;
        try
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            AccessibleObjectFromWindow(toolbar, unchecked((uint)-4), ref iid, out accessible);
            return ((Accessibility.IAccessible)accessible).get_accName(0);
        }
        catch (COMException) { return null; }
        finally { if (accessible != null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
    }
}
