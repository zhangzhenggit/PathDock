using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PathDock;

internal static partial class Native
{
    internal static int WindowPositionUpdates, VisibleRegionUpdates;
    static partial void RecordWindowPosition() => WindowPositionUpdates++;
    static partial void RecordRegionUpdate() => VisibleRegionUpdates++;
}

public sealed partial class MainWindow
{
    readonly List<double> animationFrameTimes = new(2048);
    long animationFrameStarted, animationFrameAllocated, animationAllocatedBytes;
    partial void BeginAnimationFrame()
    {
        animationFrameStarted = Stopwatch.GetTimestamp();
        animationFrameAllocated = GC.GetAllocatedBytesForCurrentThread();
    }
    partial void EndAnimationFrame()
    {
        animationAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - animationFrameAllocated;
        animationFrameTimes.Add(Stopwatch.GetElapsedTime(animationFrameStarted).TotalMilliseconds);
    }
    object AnimationMetrics()
    {
        var sorted = animationFrameTimes.OrderBy(value => value).ToArray();
        return new {
            Frames = sorted.Length,
            MeanCpuMs = sorted.Length == 0 ? 0 : sorted.Average(),
            P95CpuMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)],
            MaxCpuMs = sorted.Length == 0 ? 0 : sorted[^1],
            AllocatedBytes = animationAllocatedBytes,
            Native.WindowPositionUpdates, Native.VisibleRegionUpdates,
            Scope = "UI animation callback only; excludes GPU presentation and idle polling"
        };
    }
}
