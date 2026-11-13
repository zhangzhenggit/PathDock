using System;
using System.IO;
using System.Threading.Tasks;

namespace PathDock;

// Owns immutable snapshots; the UI remains the only owner of mutable settings.
internal sealed class DeferredFileWriter
{
    readonly object gate = new();
    readonly string path;
    readonly Action<Exception> failed;
    readonly Action<string, string> write;
    string? pending;
    string? written;
    Task worker = Task.CompletedTask;
    internal DeferredFileWriter(string path, Action<Exception> failed, Action<string, string>? write = null)
    { this.path = path; this.failed = failed; this.write = write ?? Write; }
    internal void Enqueue(string snapshot)
    {
        lock (gate)
        {
            pending = snapshot;
            if (worker.IsCompleted) worker = Task.Run(Drain);
        }
    }
    void Drain()
    {
        while (true)
        {
            string snapshot;
            lock (gate)
            {
                if (pending == null) { worker = Task.CompletedTask; return; }
                snapshot = pending; pending = null;
            }
            try { if (snapshot != written) { write(path, snapshot); written = snapshot; } }
            catch (Exception ex) { try { failed(ex); } catch { } }
        }
    }
    internal Task FlushAsync() { lock (gate) return worker; }
    internal static void Write(string path, string snapshot)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, snapshot);
        File.Move(temporary, path, true);
    }
}
