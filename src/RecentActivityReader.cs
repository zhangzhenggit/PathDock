using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PathDock;

internal sealed record RecentActivity(string Path, DateTimeOffset Time);
internal sealed class RecentActivityReader
{
    readonly Dictionary<string, DateTime> seen = new(StringComparer.OrdinalIgnoreCase);
    // Called by one background poll at a time. Failed/partial files are retried.
    internal List<RecentActivity> Read(string root)
    {
        var result = new List<RecentActivity>();
        var directory = Path.Combine(root, "activity");
        if (!Directory.Exists(directory)) { seen.Clear(); return result; }
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            present.Add(file);
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (seen.TryGetValue(file, out var previous) && previous == stamp) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var entry = document.RootElement;
                var utc = entry.GetProperty("Utc").GetDateTimeOffset();
                var path = DockStore.Normalize(entry.GetProperty("Path").GetString()!);
                var age = DateTimeOffset.UtcNow - utc;
                if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(7)) result.Add(new(path, utc));
                seen[file] = stamp;
            }
            catch { }
        }
        foreach (var file in new List<string>(seen.Keys)) if (!present.Contains(file)) seen.Remove(file);
        return result;
    }
}
