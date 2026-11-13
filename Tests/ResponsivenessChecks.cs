using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace PathDock;

internal static class ResponsivenessChecks
{
    internal static void Run(Action<string, Action> test, Action<bool> require, string root)
    {
        test("Identical settings snapshots do not repeat disk writes", () => {
            var count = 0;
            var writer = new DeferredFileWriter("unused", _ => { }, (_, _) => Interlocked.Increment(ref count));
            for (var i = 0; i < 10; i++) { writer.Enqueue("same"); require(writer.FlushAsync().Wait(3000)); }
            require(count == 1);
        });
        test("Slow settings writes do not block callers and pending snapshots coalesce", () => {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writes = new List<string>();
            var writer = new DeferredFileWriter("unused", _ => { }, (_, snapshot) => {
                lock (writes) writes.Add(snapshot);
                if (snapshot == "first") { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); }
            });
            writer.Enqueue("first");
            try {
                require(entered.Wait(3000));
                writer.Enqueue("intermediate"); writer.Enqueue("latest");
                require(!writer.FlushAsync().IsCompleted);
            } finally { release.Set(); }
            require(writer.FlushAsync().Wait(3000));
            require(writes.Count == 2 && writes[0] == "first" && writes[1] == "latest");
        });
        test("Settings writer reports failure and can save the next snapshot", () => {
            var errors = 0; var actual = "";
            var writer = new DeferredFileWriter("unused", _ => Interlocked.Increment(ref errors), (_, snapshot) => {
                if (snapshot == "fail") throw new IOException("Simulated disk failure");
                actual = snapshot;
            });
            writer.Enqueue("fail"); require(writer.FlushAsync().Wait(3000));
            writer.Enqueue("recovered"); require(writer.FlushAsync().Wait(3000));
            require(errors == 1 && actual == "recovered");
        });
        test("Background settings own an immutable snapshot and flush the latest state", () => {
            var store = new DockStore(Path.Combine(root, "async-settings"), false);
            store.EnableBackgroundSaving();
            store.AddPinned(@"C:\Snapshot");
            store.State.Folders[0].Name = "unsaved mutation";
            require(store.FlushAsync().Wait(3000));
            require(new DockStore(store.Root, false).State.Folders[0].Name == "Snapshot");
            for (var i = 0; i < 100; i++) { store.State.Folders[0].Name = "saved-" + i; store.Save(); }
            require(store.FlushAsync().Wait(3000));
            require(new DockStore(store.Root, false).State.Folders[0].Name == "saved-99");
        });
        test("Activity scans retry partial files and skip unchanged valid entries", () => {
            var activityRoot = Path.Combine(root, "activity-reader");
            var directory = Path.Combine(activityRoot, "activity"); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "shell.json");
            var stamp = DateTime.UtcNow.AddMinutes(-1);
            var reader = new RecentActivityReader();
            File.WriteAllText(file, "{"); File.SetLastWriteTimeUtc(file, stamp);
            require(reader.Read(activityRoot).Count == 0);
            File.WriteAllText(file, JsonSerializer.Serialize(new { Path = @"C:\Recovered", Utc = DateTimeOffset.UtcNow }));
            File.SetLastWriteTimeUtc(file, stamp);
            var entries = reader.Read(activityRoot);
            require(entries.Count == 1 && entries[0].Path == @"C:\Recovered");
            require(reader.Read(activityRoot).Count == 0);
            File.Delete(file); require(reader.Read(activityRoot).Count == 0);
            File.WriteAllText(file, JsonSerializer.Serialize(new { Path = @"C:\Recreated", Utc = DateTimeOffset.UtcNow }));
            File.SetLastWriteTimeUtc(file, stamp);
            require(reader.Read(activityRoot).Count == 1);
        });
        test("Activity scans reject expired and invalid paths", () => {
            var activityRoot = Path.Combine(root, "invalid-activity");
            var directory = Path.Combine(activityRoot, "activity"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "old.json"), JsonSerializer.Serialize(new { Path = @"C:\Old", Utc = DateTimeOffset.UtcNow.AddDays(-8) }));
            File.WriteAllText(Path.Combine(directory, "relative.json"), JsonSerializer.Serialize(new { Path = "relative", Utc = DateTimeOffset.UtcNow }));
            require(new RecentActivityReader().Read(activityRoot).Count == 0);
        });
    }
}
