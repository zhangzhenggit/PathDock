using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

namespace PathDock;

internal sealed class DialogFollow : IDisposable
{
    sealed record Visit(IntPtr Owner, int Process, long Epoch, IntPtr Explorer, int ExplorerProcess, long Token, long Time);
    sealed record Request(IntPtr Window, int Process, long Epoch, long Generation, long FilenameVersion, long Time);
    readonly Action<Action> post;
    readonly DialogFollowNative.EventCallback foregroundCallback, valueCallback;
    readonly Channel<Request> queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object workerLock = new();
    IntPtr foregroundHook, valueHook, destroyHook, previous, lastDialog;
    string previousClass = "";
    int lastDialogProcess, stopped, watchReady;
    long epoch, generation, visitToken, attemptedToken, filenameVersion, folderVersion;
    Visit? visit;
    FollowDialog? target;
    Thread? worker;

    internal DialogFollow(Action<Action> post)
    {
        this.post = post;
        foregroundCallback = ForegroundChanged; valueCallback = ValueChanged;
        foregroundHook = DialogFollowNative.SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
        if (foregroundHook == IntPtr.Zero) throw new InvalidOperationException("无法监听文件对话框切换。");
        ForegroundChanged(IntPtr.Zero, 3, DialogFollowNative.GetForegroundWindow(), 0, 0, 0, 0);
    }
    void ForegroundChanged(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (Volatile.Read(ref stopped) != 0 || window == IntPtr.Zero || window == previous || window != DialogFollowNative.GetForegroundWindow()) return;
        var current = Interlocked.Increment(ref generation);
        var now = clock.ElapsedMilliseconds;
        var source = Volatile.Read(ref visit);
        if (previousClass is "CabinetWClass" or "ExploreWClass" && source?.Explorer == previous)
            Volatile.Write(ref visit, source with { Time = now });
        previous = window; previousClass = DialogFollowNative.Class(window);
        var tracked = Volatile.Read(ref target);
        if (tracked != null && !DialogFollowNative.IsWindow(new IntPtr(tracked.Identity.Window))) Watch(null);
        if (previousClass is "CabinetWClass" or "ExploreWClass")
        {
            Volatile.Write(ref visit, new(lastDialog, lastDialogProcess, epoch, window, DialogFollowNative.ProcessId(window), ++visitToken, now));
            return;
        }
        if (previousClass != "#32770") return;
        var pid = DialogFollowNative.ProcessId(window);
        if (pid == Environment.ProcessId || pid == 0) { lastDialog = IntPtr.Zero; epoch++; Volatile.Write(ref visit, null); return; }
        if (lastDialog != window || lastDialogProcess != pid)
        {
            epoch++; lastDialog = window; lastDialogProcess = pid;
            var recent = Volatile.Read(ref visit);
            if (recent != null && (recent.Owner == IntPtr.Zero || !DialogFollowNative.IsWindow(recent.Owner)))
                Volatile.Write(ref visit, recent with { Owner = window, Process = pid, Epoch = epoch, Token = ++visitToken });
        }
        var request = new Request(window, pid, epoch, current, Interlocked.Read(ref filenameVersion), now);
        if (!queue.Writer.TryWrite(request)) return;
        lock (workerLock)
        {
            if (worker != null || Volatile.Read(ref stopped) != 0) return;
            worker = new Thread(ReadRequests) { IsBackground = true, Name = "PathDock dialog follow", Priority = ThreadPriority.BelowNormal };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
    }
    void ValueChanged(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        var current = Volatile.Read(ref target);
        if (current == null || Volatile.Read(ref stopped) != 0) return;
        if (evt == 0x8001)
        {
            if (window != new IntPtr(current.Identity.Window) || objectId != 0 || childId != 0) return;
            var recent = Volatile.Read(ref visit);
            if (recent?.Owner == window) Volatile.Write(ref visit, recent with { Owner = IntPtr.Zero });
            if (lastDialog == window) { lastDialog = IntPtr.Zero; lastDialogProcess = 0; epoch++; }
            Watch(null);
            return;
        }
        if (window == current.Filename) Interlocked.Increment(ref filenameVersion);
        else if (current.AddressWindows.Contains(window)) Interlocked.Increment(ref folderVersion);
    }
    void Watch(FollowDialog? next)
    {
        Volatile.Write(ref watchReady, 0);
        Volatile.Write(ref target, next);
        Interlocked.Exchange(ref filenameVersion, 0); Interlocked.Exchange(ref folderVersion, 0);
        post(() => {
            if (Volatile.Read(ref stopped) != 0 || !ReferenceEquals(Volatile.Read(ref target), next)) return;
            if (valueHook != IntPtr.Zero) DialogFollowNative.UnhookWinEvent(valueHook);
            if (destroyHook != IntPtr.Zero) DialogFollowNative.UnhookWinEvent(destroyHook);
            valueHook = next == null ? IntPtr.Zero : DialogFollowNative.SetWinEventHook(0x800C, 0x800E, IntPtr.Zero, valueCallback, (uint)next.Identity.Process, 0, 0);
            destroyHook = next == null ? IntPtr.Zero : DialogFollowNative.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, valueCallback, (uint)next.Identity.Process, 0, 0);
            Volatile.Write(ref watchReady, valueHook != IntPtr.Zero && destroyHook != IntPtr.Zero ? 1 : 0);
        });
    }
    bool Current(Request request) => Volatile.Read(ref stopped) == 0 && request.Generation == Interlocked.Read(ref generation)
        && clock.ElapsedMilliseconds - request.Time <= 1000 && DialogFollowNative.GetForegroundWindow() == request.Window;
    void ReadRequests()
    {
        ExplorerReader? reader = null;
        try
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                if (!queue.Reader.TryRead(out var request) || !Current(request)) continue;
                try
                {
                    var dialog = DialogFollowNative.Describe(request.Window);
                    var readiness = Stopwatch.StartNew();
                    while (dialog == null && Volatile.Read(ref visit) != null && Current(request) && readiness.ElapsedMilliseconds < 350)
                    {
                        Thread.Sleep(20);
                        dialog = DialogFollowNative.Describe(request.Window);
                    }
                    var cached = Volatile.Read(ref target);
                    if (dialog == null) { Watch(null); continue; }
                    if (cached == null || cached.Identity != dialog.Identity || cached.Filename != dialog.Filename)
                    {
                        Watch(dialog);
                        reader ??= new ExplorerReader();
                        var pending = Volatile.Read(ref visit);
                        if (pending == null || pending.Owner != request.Window || pending.Process != request.Process || pending.Epoch != request.Epoch) continue;
                        var registration = Stopwatch.StartNew();
                        while (Current(request) && Volatile.Read(ref watchReady) == 0 && registration.ElapsedMilliseconds < 150) Thread.Sleep(5);
                        request = request with { FilenameVersion = Interlocked.Read(ref filenameVersion) };
                    }
                    var source = Volatile.Read(ref visit);
                    if (Volatile.Read(ref watchReady) == 0 || source == null || source.Owner != request.Window || source.Process != request.Process || source.Epoch != request.Epoch
                        || source.Token == Interlocked.Read(ref attemptedToken) || clock.ElapsedMilliseconds - source.Time > 120000
                        || !DialogFollowNative.IsWindow(source.Explorer) || DialogFollowNative.ProcessId(source.Explorer) != source.ExplorerProcess) continue;
                    var wait = Stopwatch.StartNew();
                    while (Current(request) && DialogFollowNative.InputBusy() && wait.ElapsedMilliseconds < 300) Thread.Sleep(5);
                    if (!Current(request) || DialogFollowNative.InputBusy()) continue;
                    var draft = DialogFollowNative.Capture(dialog);
                    if (draft == null || request.FilenameVersion != Interlocked.Read(ref filenameVersion) || !DialogFollowNative.FolderUnchanged(dialog)) continue;
                    var readyFolderVersion = Interlocked.Read(ref folderVersion);
                    reader ??= new ExplorerReader();
                    var selection = DialogFollowRules.Select(source.Explorer.ToInt64(), reader.Read(source.Explorer));
                    if (selection.Location == null || !Current(request)) continue;
                    var path = selection.Location.Path;
                    var exists = Directory.Exists(path);
                    var intent = new NavigationIntent(dialog.Identity, source.Token, path, request.Generation, request.FilenameVersion, request.Time, readyFolderVersion);
                    var live = new LiveState(dialog.Identity, DialogFollowNative.IdentityMatches(dialog), DialogFollowNative.GetForegroundWindow() == request.Window,
                        true, true, exists, exists, Interlocked.Read(ref generation), Interlocked.Read(ref filenameVersion), DialogFollowNative.InputBusy(), false,
                        (int)wait.ElapsedMilliseconds, clock.ElapsedMilliseconds, Interlocked.Read(ref folderVersion));
                    if (DialogCommitRules.Check(intent, live) != "Ready" || !DialogFollowNative.DraftUnchanged(dialog, draft)) continue;
                    // Navigation temporarily borrows the filename field. Its own value events are expected.
                    bool Valid() => Current(request);
                    Interlocked.Exchange(ref attemptedToken, source.Token);
                    DialogFollowNative.Navigate(dialog, draft, path, Valid);
                }
                catch (Exception ex)
                {
                    if (ex is COMException or InvalidComObjectException)
                    {
                        try { reader?.Dispose(); } catch (COMException) { } catch (InvalidComObjectException) { }
                        reader = null;
                    }
                    // Unavailable providers cancel this visit without interrupting the application.
                }
            }
        }
        finally { try { reader?.Dispose(); } catch (COMException) { } catch (InvalidComObjectException) { } }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        Interlocked.Increment(ref generation);
        if (foregroundHook != IntPtr.Zero) { DialogFollowNative.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        if (valueHook != IntPtr.Zero) { DialogFollowNative.UnhookWinEvent(valueHook); valueHook = IntPtr.Zero; }
        if (destroyHook != IntPtr.Zero) { DialogFollowNative.UnhookWinEvent(destroyHook); destroyHook = IntPtr.Zero; }
        queue.Writer.TryComplete();
    }
}
