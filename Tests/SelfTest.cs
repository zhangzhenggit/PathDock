using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PathDock;

internal static class SelfTest
{
    public static int Run(string output)
    {
        var root = Path.Combine(Path.GetTempPath(), "PathDock-Test-" + Guid.NewGuid().ToString("N"));
        var results = new List<object>(); var failures = 0;
        void Test(string name, Action action) { try { action(); results.Add(new { Name = name, Passed = true, Error = "" }); } catch (Exception ex) { failures++; results.Add(new { Name = name, Passed = false, Error = ex.Message }); } }
        void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
        try
        {
            Directory.CreateDirectory(root);
            Test("Nested collection batches persist once at outer completion", () => {
                var store = new DockStore(Path.Combine(root, "batch"), false);
                var before = File.ReadAllText(store.StateFile);
                using (store.BatchUpdates()) {
                    store.Visit(@"C:\BatchA", "test");
                    using (store.BatchUpdates()) { store.Visit(@"C:\BatchB", "test"); store.AddPinned(@"C:\BatchC"); }
                    Require(File.ReadAllText(store.StateFile) == before);
                }
                var restored = new DockStore(store.Root, false);
                Require(restored.State.Folders.Count == 3 && restored.State.Folders.Count(f => f.Pinned) == 1);
                using (store.BatchUpdates()) { }
                store.AddPinned(@"C:\AfterBatch");
                Require(new DockStore(store.Root, false).State.Folders.Count == 4);
            });
            Test("Collection batch scope is released after exception", () => {
                var store = new DockStore(Path.Combine(root, "batch-exception"), false);
                try { using (store.BatchUpdates()) { store.Visit(@"C:\BeforeError", "test"); throw new InvalidOperationException(); } }
                catch (InvalidOperationException) { }
                Require(new DockStore(store.Root, false).State.Folders.Count == 1);
                store.AddPinned(@"C:\StillWorks"); Require(new DockStore(store.Root, false).State.Folders.Count == 2);
            });
            Test("Nearest edge supports offset and negative monitor coordinates", () => {
                foreach (var area in new[] { new System.Drawing.Rectangle(0, 0, 3840, 2100), new System.Drawing.Rectangle(-2560, -200, 2560, 1400), new System.Drawing.Rectangle(3840, 104, 3072, 1850) }) {
                    Require(MainWindow.NearestSide(area, area.Left + 2, area.Top + area.Height / 2) == "left");
                    Require(MainWindow.NearestSide(area, area.Right - 2, area.Top + area.Height / 2) == "right");
                    Require(MainWindow.NearestSide(area, area.Left + area.Width / 2, area.Top + 2) == "top");
                    Require(MainWindow.NearestSide(area, area.Left + area.Width / 2, area.Bottom - 2) == "bottom");
                }
            });
            Test("Four sides and horizontal position survive reload", () => {
                foreach (var side in new[] { "left", "right", "top", "bottom" }) {
                    var path = Path.Combine(root, "dock-" + side); var store = new DockStore(path, false);
                    store.State.Side = side; store.State.HorizontalRatio = .73; store.Save();
                    var restored = new DockStore(path, false); Require(restored.State.Side == side && restored.State.HorizontalRatio == .73);
                }
            });
            Test("Root drive is preserved", () => Require(DockStore.Normalize("C:\\") == "C:\\"));
            Test("Relative and newline paths rejected", () => { foreach (var path in new[] { "..\\abc", "folder", "C:\\abc\nother" }) { var rejected = false; try { DockStore.Normalize(path); } catch (ArgumentException) { rejected = true; } Require(rejected); } });
            Test("Bookmark persistence with Unicode and punctuation", () => {
                var store = new DockStore(Path.Combine(root, "bookmarks"), false);
                var path = Path.Combine(root, "中文 📁 [test] O'Brien $a; &");
                store.AddPinned(path, "我的项目"); var restored = new DockStore(store.Root, false); Require(restored.State.Folders.Single().Path == path && restored.State.Folders[0].Name == "我的项目");
            });
            Test("Case and trailing slash do not duplicate bookmarks", () => { var store = new DockStore(Path.Combine(root, "dedup"), false); store.AddPinned("C:\\Projects"); store.AddPinned("c:\\PROJECTS\\"); Require(store.State.Folders.Count == 1); });
            Test("Visits preserve fixed name and reject old timestamps", () => { var store = new DockStore(Path.Combine(root, "visits"), false); var entry = store.AddPinned("C:\\Projects", "名字"); var stamp = DateTimeOffset.Now; Require(store.Visit(entry.Path, "test", stamp)); Require(!store.Visit(entry.Path, "old", stamp.AddSeconds(-1))); Require(entry.Pinned && entry.Name == "名字" && entry.Visits == 1 && entry.Source == "test"); });
            Test("Paused tracking does not create items", () => { var store = new DockStore(Path.Combine(root, "paused"), false); store.State.RecordRecent = false; Require(!store.Visit("C:\\Projects", "test") && store.State.Folders.Count == 0); });
            Test("Removed shortcut is not immediately rediscovered", () => { var store = new DockStore(Path.Combine(root, "hidden"), false); var item = store.AddPinned("C:\\Projects"); store.Hide(item); Require(!store.Visit(item.Path, "explorer") && store.State.Folders.Count == 0); store.AddPinned(item.Path); Require(store.State.Folders.Count == 1); });
            Test("Recent cap preserves pinned entries", () => { var store = new DockStore(Path.Combine(root, "cap"), false); store.AddPinned("C:\\Pinned"); for (var i = 0; i < 90; i++) store.Visit("C:\\test\\" + i, "test", DateTimeOffset.Now.AddSeconds(i)); Require(store.State.Folders.Count == 81 && store.State.Folders.Single(f => f.Pinned).Path == "C:\\Pinned"); });
            Test("Malformed configuration has a preserved recovery copy", () => { var directory = Path.Combine(root, "broken"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "settings.json"), "not json"); var store = new DockStore(directory, false); Require(store.RecoveryNotice != null && Directory.GetFiles(directory, "*.damaged-*").Length == 1); });
            Test("Invalid configuration structure recovers instead of crashing", () => { var directory = Path.Combine(root, "bad-schema"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Folders\":null}"); var store = new DockStore(directory, false); Require(store.RecoveryNotice != null && store.State.Folders != null); });
            Test("Null bookmark entries are recovered safely", () => { var directory = Path.Combine(root, "null-entry"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Folders\":[null]}"); var store = new DockStore(directory, false); Require(store.RecoveryNotice != null); });
            Test("Duplicate loaded paths are merged with pinned entries preferred", () => { var directory = Path.Combine(root, "duplicates"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Folders\":[{\"Path\":\"C:\\\\Projects\",\"Pinned\":false},{\"Path\":\"c:\\\\projects\",\"Pinned\":true}]}"); var store = new DockStore(directory, false); Require(store.State.Folders.Count == 1 && store.State.Folders[0].Pinned); });
            Test("Only declared terminal processes trigger terminal choices", () => { Require(new TargetWindow(IntPtr.Zero, "Codex", "WindowsTerminal", 0).IsTerminal); Require(!new TargetWindow(IntPtr.Zero, "PowerShell documentation", "msedge", 0).IsTerminal); });
            var codexView = "› Ask Codex to do anything\nGPT-6.1 · 99% context left\n? for shortcuts";
            Test("Codex input signature requires prompt and current footer", () => Require(AutoTarget.LooksLikeCodexInput(codexView)));
            Test("Existing Codex draft is recognized when the idle hint disappears", () => Require(AutoTarget.LooksLikeCodexInput("› 请检查这个项目\nGPT-6.1 · 99% context left\nCtrl+G to edit in editor")));
            Test("Wrapped multiline Codex draft remains recognized", () => Require(AutoTarget.LooksLikeCodexInput("› 第一行内容\n" + string.Join("\n", Enumerable.Repeat("还有一行已经输入的内容", 40)) + "\nGPT-6.1 · 99% context left")));
            Test("Historical context status before a new prompt is rejected", () => Require(!AutoTarget.LooksLikeCodexInput("GPT-6.1 · 99% context left\n› another program\n? for shortcuts")));
            Test("Startup registration toggles only its own entry and quotes paths", () => {
                var wasEnabled = StartupRegistration.IsEnabled;
                var registryPath = @"Software\PathDock\Tests\Startup-" + Guid.NewGuid().ToString("N");
                try {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(registryPath);
                    key.SetValue("OtherApp", "preserve");
                    StartupRegistration.SetInKey(key, @"D:\Tools With Spaces\PathDock.exe", true);
                    Require((string?)key.GetValue("PathDock") == "\"D:\\Tools With Spaces\\PathDock.exe\" --startup");
                    StartupRegistration.SetInKey(key, @"D:\Tools With Spaces\PathDock.exe", false);
                    Require(key.GetValue("PathDock") == null && (string?)key.GetValue("OtherApp") == "preserve");
                    Require(StartupRegistration.IsEnabled == wasEnabled);
                } finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
            });
            Test("Old Codex output followed by PowerShell is rejected", () => Require(!AutoTarget.LooksLikeCodexInput(codexView + "\nPS C:\\Projects> ")));
            Test("Python REPL is not treated as Codex input", () => Require(!AutoTarget.LooksLikeCodexInput(codexView + "\n>>> ")));
            Test("Footer or title alone cannot authorize insertion", () => Require(!AutoTarget.LooksLikeCodexInput("OpenAI Codex\n? for shortcuts")));
            Test("A unique live shell marker selects its own session", () => {
                var first = new ShellSession { Id = "one", TitleMarker = "[PD:0123456789ab]" }; var second = new ShellSession { Id = "two", TitleMarker = "[PD:abcdef012345]" };
                var target = new TargetWindow(IntPtr.Zero, "PowerShell " + second.TitleMarker, "WindowsTerminal", 0);
                Require(Services.MatchSession(new[] { first, second }, target)?.Id == "two");
            });
            Test("Missing or ambiguous shell marker never selects a session", () => {
                var item = new ShellSession { Id = "one", TitleMarker = "[PD:0123456789ab]" };
                Require(Services.MatchSession(new[] { item }, new TargetWindow(IntPtr.Zero, "PowerShell", "WindowsTerminal", 0)) == null);
                Require(Services.MatchSession(new[] { item, item }, new TargetWindow(IntPtr.Zero, item.TitleMarker, "WindowsTerminal", 0)) == null);
            });
            File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(new { Passed = results.Count - failures, Failed = failures, Tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("PathDock-Test-")) Directory.Delete(root, true); }
        return failures == 0 ? 0 : 1;
    }
}
