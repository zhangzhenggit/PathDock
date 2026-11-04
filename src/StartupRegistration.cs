using System;
using System.IO;
using Microsoft.Win32;

namespace PathDock;

internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EntryName = "PathDock";
    static string Executable => Path.Combine(AppContext.BaseDirectory, "PathDock.exe");

    internal static string CommandFor(string executable) => "\"" + Path.GetFullPath(executable) + "\" --startup";

    public static bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return string.Equals(key?.GetValue(EntryName) as string, CommandFor(Executable), StringComparison.OrdinalIgnoreCase); }
    }
    public static void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(Executable)) throw new FileNotFoundException("找不到 PathDock 程序，请从发布目录运行后再设置自启。", Executable);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        SetInKey(key, Executable, enabled);
    }
    internal static void SetInKey(RegistryKey key, string executable, bool enabled)
    {
        if (enabled) key.SetValue(EntryName, CommandFor(executable), RegistryValueKind.String);
        else key.DeleteValue(EntryName, false);
    }
}
