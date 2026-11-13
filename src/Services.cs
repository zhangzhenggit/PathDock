using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PathDock;

public sealed class ShellSession
{
    public string Id { get; set; } = "";
    public int Pid { get; set; }
    public long StartedTicks { get; set; }
    public long Generation { get; set; }
    public string Cwd { get; set; } = "";
    public string State { get; set; } = "";
    public string TitleMarker { get; set; } = "";
    public int BridgeVersion { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

internal static class Services
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly SemaphoreSlim navigationGate = new(1, 1);
    public static Task SetClipboardText(string text) => Sta(() => { System.Windows.Clipboard.SetText(text); return true; });
    public static async Task<string> RunPowerShell(string script, params string[] args)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var info = new ProcessStartInfo(File.Exists(executable) ? executable : "pwsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var part in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script }.Concat(args)) info.ArgumentList.Add(part);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 PowerShell。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(cancellation.Token); } catch { try { process.Kill(true); } catch { } throw new TimeoutException("PowerShell 操作超时。"); }
        var text = await output; var error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "PowerShell 操作失败。" : error.Trim());
        return text;
    }
    public static Task<List<string>> ExplorerFolders() => Sta(() =>
    {
        var list = new List<string>(); object? shell = null; object? windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            foreach (dynamic window in (dynamic)windows)
            {
                try
                {
                    var url = (string)window.LocationURL;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc) list.Add(DockStore.Normalize(uri.LocalPath));
                }
                catch { }
                finally { Marshal.ReleaseComObject(window); }
            }
        }
        catch { }
        finally { if (windows != null) Marshal.ReleaseComObject(windows); if (shell != null) Marshal.ReleaseComObject(shell); }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    });
    static Task<T> Sta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(work()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static List<ShellSession> Sessions(string root)
    {
        var directory = Path.Combine(root, "sessions"); if (!Directory.Exists(directory)) return new();
        var list = new List<ShellSession>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var session = JsonSerializer.Deserialize<ShellSession>(File.ReadAllText(file), Json);
                if (session == null || session.Id != Path.GetFileNameWithoutExtension(file) || session.State != "Idle" || (DateTimeOffset.UtcNow - session.UpdatedUtc).TotalMilliseconds is < 0 or > 1200) continue;
                using var process = Process.GetProcessById(session.Pid);
                if (process.StartTime.ToUniversalTime().Ticks == session.StartedTicks && process.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)) list.Add(session);
            }
            catch { }
        }
        return list;
    }
    public static ShellSession? MatchSession(string root, TargetWindow target)
    {
        return MatchSession(Sessions(root), target);
    }
    internal static ShellSession? MatchSession(IEnumerable<ShellSession> candidates, TargetWindow target)
    {
        var matches = candidates.Where(s => !string.IsNullOrEmpty(s.TitleMarker) && target.Title.Contains(s.TitleMarker, StringComparison.Ordinal)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    public static async Task SwitchSession(string root, ShellSession session, string path, TargetWindow? target = null)
    {
        if (!await navigationGate.WaitAsync(0)) throw new InvalidOperationException("正在切换目录，请稍候再试。");
        try { await SwitchSessionCore(root, session, path, target); }
        finally { navigationGate.Release(); }
    }
    static async Task SwitchSessionCore(string root, ShellSession session, string path, TargetWindow? target)
    {
        var fresh = await Task.Run(() => Sessions(root).FirstOrDefault(s => s.Id == session.Id && s.Generation == session.Generation));
        if (fresh == null) throw new InvalidOperationException("会话状态已变化。请确认目标处于空提示符后重试。");
        var requestId = Guid.NewGuid().ToString("N");
        var request = Path.Combine(root, "requests", session.Id + ".json"); var reply = Path.Combine(root, "replies", requestId + ".json");
        var temporary = request + ".tmp";
        var lifetime = fresh.BridgeVersion < 2 ? 1200 : 2500;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request)!); Directory.CreateDirectory(Path.GetDirectoryName(reply)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Id = requestId, Session = session.Id, Generation = session.Generation, Path = path, ExpiresUtc = DateTimeOffset.UtcNow.AddMilliseconds(lifetime), RequireForeground = target != null, TargetWindow = target?.Handle.ToInt64() ?? 0, TargetTitle = target?.Title ?? "" }));
            File.Move(temporary, request, true);
        });
        try
        {
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                if (!File.Exists(reply)) continue;
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reply));
                var response = document.RootElement;
                if (response.GetProperty("Result").GetString() != "Applied")
                {
                    var code = response.TryGetProperty("Code", out var reason) ? reason.GetString() : "LegacyBridge";
                    throw new InvalidOperationException(code switch
                    {
                        "FocusChanged" => "目标窗口或标签已变化，未切换目录。",
                        "Expired" => "切换请求超时，已取消。",
                        "GenerationChanged" => "目标正在输入或执行命令，未切换目录。",
                        "NavigationFailed" => "无法访问目录：" + (response.TryGetProperty("Message", out var detail) ? detail.GetString() : path),
                        "LegacyBridge" => "切换未完成，请重新打开目标 PowerShell 标签后再试。",
                        _ => "切换未执行：" + code
                    });
                }
                if (!response.TryGetProperty("Path", out var actual) || !string.Equals(DockStore.Normalize(actual.GetString()!), DockStore.Normalize(path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("会话返回的目录与请求不一致，请检查目标会话。");
                return;
            }
            throw new TimeoutException("未收到会话确认，请检查目标目录；请求已过期，不会延迟执行。");
        }
        finally { try { if (File.Exists(request)) File.Delete(request); if (File.Exists(reply)) File.Delete(reply); } catch { } }
    }
    public static async Task ManageIntegration(string root, bool install) => await RunPowerShell(Path.Combine(AppContext.BaseDirectory, "Integration", "manage.ps1"), "-Mode", install ? "Install" : "Remove", "-Root", root);
}
