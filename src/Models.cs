using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PathDock;

public sealed class FolderEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string Source { get; set; } = "";
    public int Visits { get; set; }
}

public sealed class DockState
{
    public int Schema { get; set; } = 1;
    public List<FolderEntry> Folders { get; set; } = new();
    public bool KeepOpen { get; set; }
    public bool TrackExplorer { get; set; } = true;
    public bool RecordRecent { get; set; } = true;
    public string Side { get; set; } = "right";
    public string ActionMode { get; set; } = "auto";
    public double ScreenX { get; set; }
    public double ScreenY { get; set; }
    public bool FloatingPanel { get; set; }
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double HorizontalRatio { get; set; } = .5;
    public double VerticalRatio { get; set; } = .42;
    public List<string> HiddenRecent { get; set; } = new();
}

public sealed class DockStore
{
    public string Root { get; }
    public string StateFile => System.IO.Path.Combine(Root, "settings.json");
    public DockState State { get; private set; }
    public string? RecoveryNotice { get; private set; }
    public DockStore(string root, bool seed = true)
    {
        Root = root; Directory.CreateDirectory(root);
        if (File.Exists(StateFile))
        {
            try
            {
                State = JsonSerializer.Deserialize<DockState>(File.ReadAllText(StateFile)) ?? throw new InvalidDataException("空配置");
                if (State.Schema != 1 || State.Folders == null || State.HiddenRecent == null) throw new InvalidDataException("不支持的配置结构");
                foreach (var entry in State.Folders)
                {
                    if (entry == null) throw new InvalidDataException("收藏条目为空");
                    try { entry.Path = Normalize(entry.Path); } catch (ArgumentException ex) { throw new InvalidDataException("配置中的路径无效", ex); }
                    if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = DefaultName(entry.Path);
                }
                if (State.ActionMode is not ("auto" or "insert" or "terminal" or "explorer")) State.ActionMode = "auto";
                if (State.Side is not ("left" or "right" or "top" or "bottom")) State.Side = "right";
                if (!double.IsFinite(State.ScreenX) || !double.IsFinite(State.ScreenY) || Math.Abs(State.ScreenX) > 1000000 || Math.Abs(State.ScreenY) > 1000000) { State.ScreenX = 0; State.ScreenY = 0; }
                if (!double.IsFinite(State.PanelX) || !double.IsFinite(State.PanelY) || Math.Abs(State.PanelX) > 1000000 || Math.Abs(State.PanelY) > 1000000) { State.FloatingPanel = false; State.PanelX = State.PanelY = 0; }
                State.HorizontalRatio = double.IsFinite(State.HorizontalRatio) ? Math.Clamp(State.HorizontalRatio, 0, 1) : .5;
                State.VerticalRatio = double.IsFinite(State.VerticalRatio) ? Math.Clamp(State.VerticalRatio, 0, 1) : .42;
                State.Folders = State.Folders.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(f => f.Pinned).ThenByDescending(f => f.LastUsed).First()).ToList();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                var backup = StateFile + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                File.Copy(StateFile, backup); State = new(); RecoveryNotice = "配置文件损坏，已保留副本并恢复默认设置。";
            }
        }
        else
        {
            State = new();
            if (seed)
            {
                AddPinned(Environment.CurrentDirectory, "工具目录");
                AddPinned(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "下载");
                AddPinned(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面");
            }
        }
        Save();
    }
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("请输入完整的文件夹路径。");
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("请使用完整路径，例如 D:\\Projects。");
        var full = System.IO.Path.GetFullPath(path);
        return full.TrimEnd('\\', '/') is var trimmed && trimmed.Length >= (System.IO.Path.GetPathRoot(full)?.Length ?? 0) ? trimmed : full;
    }
    public static string DefaultName(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
    public FolderEntry AddPinned(string path, string? name = null)
    {
        path = Normalize(path);
        State.HiddenRecent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        var existing = State.Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.Pinned = true; if (!string.IsNullOrWhiteSpace(name)) existing.Name = name.Trim(); Save(); return existing; }
        var item = new FolderEntry { Path = path, Name = string.IsNullOrWhiteSpace(name) ? DefaultName(path) : name.Trim(), Pinned = true };
        State.Folders.Add(item); Save(); return item;
    }
    public bool Visit(string path, string source, DateTimeOffset? time = null)
    {
        if (!State.RecordRecent) return false;
        path = Normalize(path);
        if (State.HiddenRecent.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
        var item = State.Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) { item = new() { Path = path, Name = DefaultName(path) }; State.Folders.Add(item); }
        var stamp = time ?? DateTimeOffset.Now;
        if (item.LastUsed != null && stamp <= item.LastUsed) return false;
        item.LastUsed = stamp; item.Source = source; item.Visits++;
        var surplus = State.Folders.Where(f => !f.Pinned).OrderByDescending(f => f.LastUsed).Skip(80).ToList();
        foreach (var old in surplus) State.Folders.Remove(old);
        Save(); return true;
    }
    public void Hide(FolderEntry item)
    {
        State.HiddenRecent.Add(item.Path); State.Folders.Remove(item); Save();
    }
    public void Save()
    {
        var temporary = StateFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, StateFile, true);
    }
}
