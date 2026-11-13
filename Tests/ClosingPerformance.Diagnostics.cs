using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PathDock;

internal sealed partial class IslandWindow
{
    internal int IntersectingScreens
    {
        get
        {
            var bounds = Native.WindowBounds(handle);
            var area = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            return System.Windows.Forms.Screen.AllScreens.Count(screen => screen.Bounds.IntersectsWith(area));
        }
    }
    long presentStarted, positionFinished;
    internal readonly List<(double Move, double Draw)> PresentTimes = new();
    partial void BeginPresent() => presentStarted = Stopwatch.GetTimestamp();
    partial void PositionComplete() => positionFinished = Stopwatch.GetTimestamp();
    partial void EndPresent() => PresentTimes.Add((Stopwatch.GetElapsedTime(presentStarted, positionFinished).TotalMilliseconds, Stopwatch.GetElapsedTime(positionFinished).TotalMilliseconds));
}

public sealed partial class MainWindow
{
    internal static bool UnclippedViewport;
    partial void ConfigureRightEdgeConstraint(ref bool enabled) { if (UnclippedViewport) enabled = false; }
    internal async Task<object> CheckClosingPerformance()
    {
        var samples = new List<object>();
        async Task WaitMotion()
        {
            var timeout = Stopwatch.StartNew();
            while (animating && timeout.ElapsedMilliseconds < 4000) await Task.Delay(5);
            if (animating) throw new TimeoutException("Performance transition did not complete");
            await Task.Delay(100);
        }
        foreach (var floating in new[] { false, true })
        {
            store.State.FloatingPanel = floating;
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            store.State.ScreenX = screen.Left + screen.Width / 2; store.State.ScreenY = screen.Top + screen.Height / 2;
            store.State.Side = "right"; store.State.PanelX = screen.Left + 120; store.State.PanelY = screen.Top + 100;
            collapsed = false; reveal = 1; Place(); Show(); await Task.Delay(100);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                foreach (var closing in new[] { true, false })
                {
                    var gaps = new List<double>();
                    var callbacks = new List<double>();
                    TimeSpan? previous = null;
                    long? wallPrevious = null;
                    var wallGaps = new List<double>();
                    void Observe(object? sender, EventArgs args)
                    {
                        if (args is not RenderingEventArgs frame || !animating || !islandCovered || previous == frame.RenderingTime) return;
                        if (previous is TimeSpan last) gaps.Add((frame.RenderingTime - last).TotalMilliseconds);
                        var wall = Stopwatch.GetTimestamp();
                        if (wallPrevious is long old) wallGaps.Add(Stopwatch.GetElapsedTime(old, wall).TotalMilliseconds);
                        wallPrevious = wall; previous = frame.RenderingTime; callbacks.Add(reveal);
                    }
                    CompositionTarget.Rendering += Observe;
                    var nativeBefore = Native.WindowPositionUpdates;
                    if (closing) Collapse(); else Expand();
                    var overlay = islandWindow!;
                    overlay.PresentTimes.Clear();
                    await WaitMotion();
                    CompositionTarget.Rendering -= Observe;
                    static object Stats(IEnumerable<double> source)
                    {
                        var all = source.OrderBy(x => x).ToArray();
                        return new { Count = all.Length, Mean = all.Length == 0 ? 0 : all.Average(), P95 = all.Length == 0 ? 0 : all[(int)((all.Length - 1) * .95)], Max = all.Length == 0 ? 0 : all[^1], Over25Ms = all.Count(v => v > 25) };
                    }
                    samples.Add(new { Floating = floating, Closing = closing, Iteration = iteration,
                        ViewportScreens = overlay.IntersectingScreens,
                        CompositionGaps = Stats(gaps), WallGaps = Stats(wallGaps), PositionMs = Stats(overlay.PresentTimes.Select(x => x.Move)),
                        DrawMs = Stats(overlay.PresentTimes.Select(x => x.Draw)), WindowMoves = Native.WindowPositionUpdates - nativeBefore,
                        Reveals = callbacks.ToArray() });
                }
            }
        }
        return new { Passed = true, Scope = "Rendering callback gaps and CPU submission; does not measure GPU presentation", Samples = samples };
    }
}
