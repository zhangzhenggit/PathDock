using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;

namespace PathDock;

public sealed partial class MainWindow
{
    internal async Task<object> CheckTailRendering()
    {
        var samples = new List<object>();
        bool alphaCorrect = true, noFinalChange = true, cutoutCorrect = true, coordinatesCorrect = true, sourceReleased = true, reversalCorrect = true;
        bool iconOpaque = true, handleMaskStable = true, singleAxisCorrect = true;
        store.State.FloatingPanel = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        foreach (var side in new[] { "right", "left", "top", "bottom" })
        {
            store.State.ScreenX = screen.Bounds.Left + screen.Bounds.Width / 2;
            store.State.ScreenY = screen.Bounds.Top + screen.Bounds.Height / 2;
            store.State.Side = side;
            collapsed = false; reveal = 1; Place(); Show(); await Task.Delay(100);
            var edge = EnsureEdgeWindow();
            edge.WarmBehind(handle, side); await Task.Delay(80);
            collapsed = true; animating = true; transitionTo = 0;
            byte[]? final = null;
            byte[]? handleMask = null;
            var strip = new List<(byte[] Pixels, int Width, int Height, double Mix)>();
            var horizontal = side is "top" or "bottom";
            var takeoverReveal = 28 / (horizontal ? ActualHeight : ActualWidth);
            var previousExtent = int.MaxValue;
            foreach (var mix in new[] { 0d, .075, .25, .5, .75, .95, 1 })
            {
                reveal = takeoverReveal * (1 - mix); UpdateDockRegion();
                edge.RevealHandle(); await Task.Delay(35);
                var image = edge.RenderTailPixels();
                strip.Add((image.Pixels, image.Width, image.Height, mix));
                var currentMask = edge.RenderHandlePixels();
                if (handleMask != null) handleMaskStable &= currentMask.SequenceEqual(handleMask);
                handleMask = currentMask;
                var px = side == "left" ? image.Width - 1 : 0;
                var py = side == "top" ? image.Height - 1 : 0;
                var alpha = image.Pixels[(py * image.Width + px) * 4 + 3];
                alphaCorrect &= alpha <= 2;
                var centreAlpha = image.Pixels[((image.Height / 2) * image.Width + image.Width / 2) * 4 + 3];
                iconOpaque &= centreAlpha == 255;
                var box = EdgeSourceBounds(); var dpi = VisualTreeHelper.GetDpi(this);
                var nativeRegion = Native.DockRegionState(handle);
                var extent = horizontal ? nativeRegion.Height : nativeRegion.Width;
                singleAxisCorrect &= extent <= previousExtent;
                previousExtent = extent;
                if (mix < 1) singleAxisCorrect &= Math.Abs((horizontal ? nativeRegion.Width : nativeRegion.Height)
                    - (horizontal ? ActualWidth : ActualHeight) * dpi.DpiScaleX) <= 2;
                var anchor = Native.WindowBounds(handle);
                var actual = Native.WindowBounds(new WindowInteropHelper(edge).Handle);
                coordinatesCorrect &= actual.Left == anchor.Left + (int)Math.Round(box.X * dpi.DpiScaleX)
                    && actual.Top == anchor.Top + (int)Math.Round(box.Y * dpi.DpiScaleY);
                samples.Add(new { Screen = screen.DeviceName, Side = side, Scale = dpi.DpiScaleX, Mix = mix, CornerAlpha = alpha,
                    CentreAlpha = centreAlpha, EdgeRendered = edge.HasRenderedContent, edge.TailActive,
                    PanelWidth = nativeRegion.Width, PanelHeight = nativeRegion.Height });
                if (mix == 1) {
                    final = image.Pixels;
                    var region = Native.DockRegionState(handle);
                    cutoutCorrect &= region.Width == 0 && region.Height == 0;
                    samples.Add(new { Screen = screen.DeviceName, Side = side, RegionWidth = region.Width, RegionHeight = region.Height });
                }
            }
            if (side == "right") SaveHandleStrip(strip, screen.DeviceName);
            animating = false; ShowEdgeWindow(); await Task.Delay(40);
            var settled = edge.RenderTailPixels();
            var finalPixels = final ?? throw new InvalidOperationException("Missing final tail frame.");
            var differences = finalPixels.Zip(settled.Pixels, (a, b) => Math.Abs(a - b)).ToArray();
            noFinalChange &= finalPixels.Length == settled.Pixels.Length && differences.All(d => d == 0);
            sourceReleased &= edge.TailSourceReleased;
            samples.Add(new { Screen = screen.DeviceName, Side = side, FinalChangedChannels = differences.Count(d => d != 0), FinalMaxDifference = differences.Max() });

            // Reverse after the transparent HWND has already taken over.
            collapsed = false; reveal = 1; Place(); Show();
            edge.WarmBehind(handle, side); await Task.Delay(40);
            collapsed = true; animating = true; transitionTo = 0; reveal = .02; UpdateDockRegion(); await Task.Delay(35);
            Expand();
            var timeout = Stopwatch.StartNew();
            while (animating && timeout.ElapsedMilliseconds < 2000) await Task.Delay(10);
            reversalCorrect &= !animating && !collapsed && reveal == 1 && IsVisible && !edge.IsVisible;
            sourceReleased &= edge.TailSourceReleased;
        }
        return new { Passed = iconOpaque && handleMaskStable && singleAxisCorrect && alphaCorrect && noFinalChange && cutoutCorrect && coordinatesCorrect && sourceReleased && reversalCorrect,
            AlphaCorrect = alphaCorrect, IconStaysOpaque = iconOpaque, HandlePixelsStable = handleMaskStable, SingleAxisRetractionCorrect = singleAxisCorrect,
            NoFinalPixelChange = noFinalChange, EmptyFinalRegionCorrect = cutoutCorrect,
            CoordinatesCorrect = coordinatesCorrect, SourceReleased = sourceReleased, TailReversalCorrect = reversalCorrect, Samples = samples };
    }
}
