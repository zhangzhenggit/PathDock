using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PathDock;

public sealed partial class MainWindow
{
    internal async Task<object> CheckIsland()
    {
        var matrix = new List<object>();
        var alphaFailures = new List<object>();
        var endpointDifferences = new List<object>();
        var switchDifferences = new List<object>();
        bool endpointMatches = true;
        bool continuousAtCapsule = true;
        bool retainedSurfaceSuppressed = true;
        bool rightViewportStaysOnPanelScreen = true;
        bool frozen = true, geometry = true, stableSnapshots = true, opaque = true, reverse = true, released = true;
        var canUse = (RenderCapability.Tier >> 16) >= 2;
        if (!canUse) return new { Passed = false, Reason = "Hardware rendering unavailable; slide fallback remains enabled." };
        store.State.FloatingPanel = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            store.State.Side = side;
            store.State.ScreenX = screen.Bounds.Left + screen.Bounds.Width / 2;
            store.State.ScreenY = screen.Bounds.Top + screen.Bounds.Height / 2;
            collapsed = false; reveal = 1; Place(); Show(); await Task.Delay(100);
            StartTransition(false, true);
            CompositionTarget.Rendering -= AnimateDock;
            var island = islandWindow ?? throw new InvalidOperationException("Snapshot transition did not start.");
            var maskedRegion = Native.DockRegionState(new System.Windows.Interop.WindowInteropHelper(island).Handle);
            retainedSurfaceSuppressed &= island.HasPresented || maskedRegion.Width == 0 && maskedRegion.Height == 0;
            if (side == "right")
            {
                var overlayBounds = Native.WindowBounds(new System.Windows.Interop.WindowInteropHelper(island).Handle);
                var panelBounds = Native.WindowBounds(handle);
                rightViewportStaysOnPanelScreen &= overlayBounds.Right <= panelBounds.Right;
            }
            var timeout = Stopwatch.StartNew();
            while (!island.HasPresented && timeout.ElapsedMilliseconds < 2000) await Task.Delay(10);
            frozen &= island.SnapshotFrozen;
            Hide(); edgeWindow?.Hide(); islandCovered = true;
            var captures = islandSnapshotCount;
            var images = new List<BitmapSource>();
            foreach (var amount in new[] { 1d, .9, .75, .6, .45, .3, .15, .05, 0 })
            {
                reveal = amount; PresentIslandFrame();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                island.DrawingSurface.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(island.DrawingSurface);
                var target = island.Frame;
                geometry &= target == IslandGeometry.Sample(ActualWidth, ActualHeight, side, amount, dpi.DpiScaleX);
                var image = new RenderTargetBitmap((int)Math.Round(island.DrawingSurface.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Round(island.DrawingSurface.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                image.Render(island.DrawingSurface); image.Freeze(); images.Add(image);
                var centre = new Int32Rect((int)Math.Round((IslandWindow.ScenePadding + target.Bounds.X + target.Bounds.Width / 2) * dpi.DpiScaleX),
                    (int)Math.Round((IslandWindow.ScenePadding + target.Bounds.Y + target.Bounds.Height / 2) * dpi.DpiScaleY), 1, 1);
                var pixel = new byte[4]; image.CopyPixels(centre, pixel, 4, 0); opaque &= pixel[3] == 255;
                if (pixel[3] != 255) alphaFailures.Add(new { Screen = screen.DeviceName, Side = side, Reveal = amount, Alpha = pixel[3], Dpi = dpi.DpiScaleX, Bounds = target.Bounds.ToString(), X = centre.X, Y = centre.Y });
                stableSnapshots &= captures == islandSnapshotCount;
            }
            SaveIslandStudy(images, screen.DeviceName + "-" + side);
            var endpoint = island.Frame.Bounds;
            var endpointDpi = VisualTreeHelper.GetDpi(island.DrawingSurface).DpiScaleX;
            var realCapsule = edgeWindow!.CaptureCapsule();
            var landing = new CroppedBitmap(images[^1], new Int32Rect((int)Math.Round((IslandWindow.ScenePadding + endpoint.X) * endpointDpi),
                (int)Math.Round((IslandWindow.ScenePadding + endpoint.Y) * endpointDpi), realCapsule.PixelWidth, realCapsule.PixelHeight));
            var landingPixels = new byte[realCapsule.PixelWidth * realCapsule.PixelHeight * 4];
            var realPixels = new byte[landingPixels.Length];
            landing.CopyPixels(landingPixels, realCapsule.PixelWidth * 4, 0); realCapsule.CopyPixels(realPixels, realCapsule.PixelWidth * 4, 0);
            var differences = landingPixels.Zip(realPixels, (a, b) => Math.Abs(a - b)).ToArray();
            // Mixed-DPI image sampling can differ by one premultiplied channel
            // level; the former halo differed by up to 194 levels.
            endpointMatches &= differences.All(d => d <= 1);
            endpointDifferences.Add(new { Screen = screen.DeviceName, Side = side, ChangedChannels = differences.Count(d => d != 0), MeanDifference = differences.Average(), MaxDifference = differences.Max() });
            foreach (var threshold in new[] { .025, 0d })
            {
                var pair = new List<byte[]>();
                foreach (var amount in new[] { threshold + .000001, Math.Max(0, threshold - .000001) })
                {
                    reveal = amount; PresentIslandFrame();
                    var sample = new RenderTargetBitmap(images[^1].PixelWidth, images[^1].PixelHeight, images[^1].DpiX, images[^1].DpiY, PixelFormats.Pbgra32);
                    sample.Render(island.DrawingSurface);
                    var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4]; sample.CopyPixels(pixels, sample.PixelWidth * 4, 0); pair.Add(pixels);
                }
                var change = pair[0].Zip(pair[1], (a, b) => Math.Abs(a - b)).ToArray();
                continuousAtCapsule &= change.All(v => v <= 2);
                switchDifferences.Add(new { Screen = screen.DeviceName, Side = side, Threshold = threshold, MaxDifference = change.Max(), ChangedChannels = change.Count(v => v > 2) });
            }
            reveal = .35; PresentIslandFrame();
            var before = island.Frame;
            StartTransition(true, true);
            reverse &= before == island.Frame && captures == islandSnapshotCount;
            timeout.Restart();
            while (animating && timeout.ElapsedMilliseconds < 2500) await Task.Delay(10);
            reverse &= !animating && !collapsed && IsVisible;
            while (island.HasSnapshot && timeout.ElapsedMilliseconds < 3500) await Task.Delay(10);
            released &= !island.HasSnapshot && !island.IsVisible;
            matrix.Add(new { Screen = screen.DeviceName, Side = side, Snapshots = islandSnapshotCount - captures + 1 });
        }
        Collapse();
        StartTransition(true, false);
        var reducedMotionCancels = !animating && !islandActive && IsVisible && islandWindow?.HasSnapshot == false && islandWindow.IsVisible == false;
        // A floating panel's hidden HWND is positioned away from the edge on
        // opening. Preparing its snapshot must not relocate the visible capsule.
        store.State.FloatingPanel = true;
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        store.State.PanelX = work.Left + 150; store.State.PanelY = work.Top + 100;
        StartTransition(false, false);
        var capsuleHandle = new System.Windows.Interop.WindowInteropHelper(edgeWindow!).Handle;
        var capsuleBefore = Native.WindowBounds(capsuleHandle);
        Expand();
        var capsuleAfter = Native.WindowBounds(capsuleHandle);
        var floatingCapsuleRemainsAtEdge = capsuleBefore == capsuleAfter;
        StartTransition(true, false);
        return new { Passed = rightViewportStaysOnPanelScreen && floatingCapsuleRemainsAtEdge && retainedSurfaceSuppressed && continuousAtCapsule && endpointMatches && reducedMotionCancels && frozen && geometry && stableSnapshots && opaque && reverse && released,
            FrozenSnapshots = frozen, GeometryCorrect = geometry, SingleCapturePerTransition = stableSnapshots,
            CardRemainsOpaque = opaque, ReversalContinuous = reverse, SnapshotReleased = released, ReducedMotionCancelsSnapshot = reducedMotionCancels,
            RetainedSurfaceSuppressedUntilPresented = retainedSurfaceSuppressed, FloatingCapsuleRemainsAtEdge = floatingCapsuleRemainsAtEdge,
            RightViewportStaysOnPanelScreen = rightViewportStaysOnPanelScreen,
            ContinuousAtCapsule = continuousAtCapsule, EndpointPixelsMatchWithinRounding = endpointMatches, SwitchDifferences = switchDifferences, Cases = matrix, AlphaFailures = alphaFailures, EndpointDifferences = endpointDifferences, ManualVisualSmoothnessVerified = false };
    }
    static void SaveIslandStudy(List<BitmapSource> frames, string screen)
    {
        const int cellWidth = 180, height = 430;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 234, 239)), null, new Rect(0, 0, frames.Count * cellWidth, height));
            for (var i = 0; i < frames.Count; i++)
            {
                var image = frames[i];
                var factor = Math.Min(170 / image.Width, 390 / image.Height);
                context.DrawImage(image, new Rect(i * cellWidth + 5, 5, image.Width * factor, image.Height * factor));
                context.DrawText(new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.DimGray, 1), new Point(i * cellWidth + 80, 408));
            }
        }
        var bitmap = new RenderTargetBitmap(frames.Count * cellWidth, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results", "island-study-" + new string(screen.Where(char.IsLetterOrDigit).ToArray()) + ".png");
        using var file = File.Create(path); encoder.Save(file);
    }
}
