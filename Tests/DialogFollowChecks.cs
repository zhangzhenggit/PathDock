using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PathDock;

internal static class DialogFollowChecks
{
    internal static void Run(Action<string, Action> test, Action<bool> require, string root)
    {
        test("Filename restoration is restricted to the original dialog's controls", () => {
            using var owner = new System.Windows.Forms.Form();
            using var other = new System.Windows.Forms.Form();
            using var edit = new System.Windows.Forms.TextBox();
            owner.Controls.Add(edit);
            var dialog = new FollowDialog(new(owner.Handle.ToInt64(), Environment.ProcessId, 0), edit.Handle,
                IntPtr.Zero, Array.Empty<IntPtr>(), null);
            require(DialogFollowNative.FilenameMatches(dialog));
            require(!DialogFollowNative.FilenameMatches(dialog with { Identity = dialog.Identity with { Window = other.Handle.ToInt64() } }));
            require(!DialogFollowNative.FilenameMatches(dialog with { Identity = dialog.Identity with { Process = -1 } }));
            edit.Dispose();
            require(!DialogFollowNative.FilenameMatches(dialog));
        });
        test("Repeated dialog observer disposal releases its native hook", () => {
            var hookField = typeof(DialogFollow).GetField("foregroundHook", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var iteration = 0; iteration < 64; iteration++)
            {
                using var observer = new DialogFollow(_ => { });
                var hook = (IntPtr)hookField.GetValue(observer)!;
                require(hook != IntPtr.Zero);
                observer.Dispose();
                require((IntPtr)hookField.GetValue(observer)! == IntPtr.Zero);
                require(!DialogFollowNative.UnhookWinEvent(hook));
            }
        });
        test("Address acknowledgement handles toolbar mnemonic ampersands", () => {
            require(DialogFollowRules.AddressLabelMatches(@"地址: D:\A  B", @"D:\A & B"));
            require(!DialogFollowRules.AddressLabelMatches(@"地址: D:\Other", @"D:\A & B"));
        });
        test("Dialog following is opt-in for existing configuration", () => {
            var store = new DockStore(Path.Combine(root, "follow-settings"), false);
            require(!store.State.FollowFileDialogs);
            store.State.FollowFileDialogs = true; store.Save();
            require(new DockStore(store.Root, false).State.FollowFileDialogs);
        });
        test("Address edit is identified by its address-band ancestry", () => require(DialogFollowRules.IsAddressEdit("Edit", true, true,
            new[] { "ComboBox", "ComboBoxEx32", "msctls_progress32", "Address Band Root" })));
        test("Save-as filename edit 1001 is not an address edit", () => require(!DialogFollowRules.IsAddressEdit("Edit", true, true,
            new[] { "ComboBox", "FloatNotifySink", "DirectUIHWND", "DUIViewWndClassName" })));
        test("Matching numeric edit IDs without an address band are rejected", () => require(!DialogFollowRules.IsAddressEdit("Edit", true, true, new[] { "ComboBox", "ComboBoxEx32" })));
        test("Hidden and foreign-root address controls are rejected", () => {
            require(!DialogFollowRules.IsAddressEdit("Edit", false, true, new[] { "Address Band Root" }));
            require(!DialogFollowRules.IsAddressEdit("Edit", true, false, new[] { "Address Band Root" }));
        });
        var active = new ExplorerLocation(10, 101, "ShellTabWindowClass", true, @"D:\目标 [one]", true, false, 0);
        test("Explorer source is resolved for the originating frame only", () => require(DialogFollowRules.Select(10,
            new[] { active with { Frame = 20, Browser = 201, Path = @"D:\wrong" }, active }).Location == active));
        test("Inactive Explorer tabs are excluded", () => require(DialogFollowRules.Select(10,
            new[] { active with { Browser = 100, BrowserVisible = false }, active }).Location == active));
        test("Ambiguous active Explorer tabs cause no selection", () => require(DialogFollowRules.Select(10,
            new[] { active, active with { Browser = 102, Path = @"D:\other" } }).Location == null));
        test("Virtual Explorer locations and failed browser lookups are rejected", () => {
            require(DialogFollowRules.Select(10, new[] { active with { FileSystem = false } }).Location == null);
            require(DialogFollowRules.Select(10, new[] { active with { QueryResult = -1 } }).Location == null);
        });
        test("File-system candidates reject relative, device and control-character paths", () => {
            foreach (var path in new[] { @"D:relative", @"\\.\pipe\test", @"\\?\GLOBALROOT\Device\test", "D:\\bad\npath" }) require(!DialogFollowRules.ValidFileSystemPath(path));
            require(DialogFollowRules.ValidFileSystemPath(@"\\server\share\folder"));
        });
        var target = new DialogIdentity(1, 100, 1000);
        var intent = new NavigationIntent(target, 1, @"D:\folder", 4, 2, 100, 3);
        var live = new LiveState(target, true, true, true, true, true, true, 4, 2, false, false, 0, 200, 3);
        test("Stable dialog follow work passes preflight", () => require(DialogCommitRules.Check(intent, live) == "Ready"));
        test("Reused HWND and PID start times cancel follow work", () => {
            require(DialogCommitRules.Check(intent, live with { Target = target with { Process = 101 } }) == "TargetChanged");
            require(DialogCommitRules.Check(intent, live with { Target = target with { Started = 2000 } }) == "TargetChanged");
        });
        test("Focus changes and superseded requests cancel follow work", () => {
            require(DialogCommitRules.Check(intent, live with { Foreground = false }) == "FocusChanged");
            require(DialogCommitRules.Check(intent, live with { ForegroundGeneration = 5 }) == "FocusChanged");
        });
        test("Typing and manual dialog navigation cancel pending work", () => {
            require(DialogCommitRules.Check(intent, live with { FilenameGeneration = 3 }) == "UserEdited");
            require(DialogCommitRules.Check(intent, live with { FolderGeneration = 4 }) == "UserNavigated");
        });
        test("Alt-Tab release is awaited with a finite deadline", () => {
            require(DialogCommitRules.Check(intent, live with { ModifiersHeld = true, ReadinessWaitMs = 10 }) == "WaitInputRelease");
            require(DialogCommitRules.Check(intent, live with { ModifiersHeld = true, ReadinessWaitMs = 300 }) == "InputBusy");
        });
        test("Mouse operations and expired work do not navigate", () => {
            require(DialogCommitRules.Check(intent, live with { MouseHeld = true }) == "WaitInputRelease");
            require(DialogCommitRules.Check(intent, live with { NowMs = 1101 }) == "Expired");
        });
        test("Unavailable directories, unknown dialogs and privilege mismatch are rejected", () => {
            require(DialogCommitRules.Check(intent, live with { DirectoryExists = false }) == "DirectoryUnavailable");
            require(DialogCommitRules.Check(intent, live with { Supported = false }) == "UnsupportedDialog");
            require(DialogCommitRules.Check(intent, live with { WritableIntegrity = false }) == "HigherIntegrity");
        });
        test("Follow navigation does not import global keyboard or focus injection", () => {
            var imports = typeof(DialogFollowNative).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Select(m => m.GetCustomAttribute<DllImportAttribute>()?.EntryPoint).Where(n => n != null).ToArray();
            require(!imports.Any(n => n is "SendInput" or "keybd_event" or "SetForegroundWindow" or "AttachThreadInput"));
        });
    }
}
