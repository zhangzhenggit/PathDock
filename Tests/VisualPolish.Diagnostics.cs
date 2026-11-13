using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PathDock;

public sealed partial class MainWindow
{
    Rect EdgeSourceBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var horizontal = store.State.Side is "top" or "bottom";
        var w = Math.Round((horizontal ? 56 : 28) * dpi.DpiScaleX);
        var h = Math.Round((horizontal ? 28 : 56) * dpi.DpiScaleY);
        var bounds = Native.WindowBounds(handle);
        var x = horizontal ? Math.Round((bounds.Right - bounds.Left - w) / 2d) : store.State.Side == "left" ? 0 : bounds.Right - bounds.Left - w;
        var y = horizontal ? store.State.Side == "top" ? 0 : bounds.Bottom - bounds.Top - h : Math.Round((bounds.Bottom - bounds.Top - h) / 2d);
        return new Rect(x / dpi.DpiScaleX, y / dpi.DpiScaleY, w / dpi.DpiScaleX, h / dpi.DpiScaleY);
    }
    static void SaveHandleStrip(List<(byte[] Pixels, int Width, int Height, double Mix)> samples, string screen)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 39, 59)), null, new Rect(0, 0, samples.Count * 120, 200));
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                var bitmap = BitmapSource.Create(sample.Width, sample.Height, 96, 96, PixelFormats.Pbgra32, null, sample.Pixels, sample.Width * 4);
                context.DrawImage(bitmap, new Rect(i * 120 + 32, 40, 56, 112));
                var text = new FormattedText(sample.Mix.ToString("P0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1);
                context.DrawText(text, new Point(i * 120 + 40, 170));
            }
        }
        var result = new RenderTargetBitmap(samples.Count * 120, 200, 96, 96, PixelFormats.Pbgra32); result.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results", "handle-strip-" + new string(screen.Where(char.IsLetterOrDigit).ToArray()) + ".png");
        using var file = File.Create(path); encoder.Save(file);
    }
    async Task<bool> CheckCalmDismissAndAccent()
    {
        var keepOpen = store.State.KeepOpen;
        store.State.KeepOpen = false;
        try
        {
            var bounds = Native.WindowBounds(handle);
            OutsideClick(bounds.Left - 50, bounds.Top - 50);
            var deferred = outsideHideTimer.IsEnabled && !collapsed && !animating
                && outsideHideTimer.Interval.TotalMilliseconds >= 100;
            RestartHideTimer(); // Returning to or interacting with the panel.
            await Task.Delay(160);
            var cancelled = !outsideHideTimer.IsEnabled && !collapsed && !animating;
            OutsideClick(bounds.Left - 50, bounds.Top - 50);
            var timeout = Stopwatch.StartNew();
            while (!collapsed && timeout.ElapsedMilliseconds < 1500) await Task.Delay(10);
            var applied = collapsed;
            while (animating && timeout.ElapsedMilliseconds < 2500) await Task.Delay(10);
            var collapsedClean = !animating && headerGlow.Opacity == 0 && motionLight.Opacity == 0;
            Expand(); timeout.Restart();
            var openingCoverSafe = !openingHandleCover || edgeWindow?.IsOpeningCoverSafe == true;
            while (animating && timeout.ElapsedMilliseconds < 2000) await Task.Delay(10);
            var expandedClean = !animating && !collapsed && headerGlow.Opacity == 0 && motionLight.Opacity == 0;
            return openingCoverSafe && deferred && cancelled && applied && collapsedClean && expandedClean
                && !headerGlow.IsHitTestVisible && !motionLight.IsHitTestVisible && motionLight.Effect == null && headerGlow.Effect == null && surface.Effect == null
                && motionLight.Background.IsFrozen
                && Ui.MotionGlow.IsFrozen && Ui.PanelBackground.IsFrozen && Ui.EdgeBackground.IsFrozen;
        }
        finally { outsideHideTimer.Stop(); store.State.KeepOpen = keepOpen; RestartHideTimer(); }
    }
}
