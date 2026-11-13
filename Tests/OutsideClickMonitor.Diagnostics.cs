using System;
using System.Threading;
using System.Threading.Tasks;

namespace PathDock;

internal sealed partial class OutsideClickMonitor
{
    internal Task<bool> ProbeListener()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Environment.CurrentManagedThreadId;
        if (!SpinWait.SpinUntil(() => { lock (gate) return dispatcher != null; }, TimeSpan.FromSeconds(3)))
            throw new TimeoutException("Mouse listener did not start.");
        lock (gate)
            dispatcher!.BeginInvoke(new Action(() => completion.SetResult(
                hook != IntPtr.Zero && Environment.CurrentManagedThreadId != caller)));
        return completion.Task;
    }
}
