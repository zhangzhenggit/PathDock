using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PathDock;

public sealed partial class MainWindow
{
    IslandWindow? islandWindow;
    bool islandActive, islandCovered, islandUnavailable;
    Point islandExpandedPosition, islandDockPosition;
    int islandSnapshotCount;
    bool BeginIslandTransition()
    {
        if (islandActive && islandWindow?.HasSnapshot == true) return true;
        if (islandUnavailable || SystemParameters.HighContrast || (RenderCapability.Tier >> 16) < 2) return false;
        try
        {
            var bounds = Native.WindowBounds(handle);
            var scale = Native.WindowScale(handle);
            var width = (int)Math.Round(ActualWidth * scale); var height = (int)Math.Round(ActualHeight * scale);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > 8_000_000) return false;
            var transform = surface.RenderTransform;
            BitmapSource snapshot;
            try
            {
                surface.RenderTransform = Transform.Identity;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(surface) { Stretch = Stretch.Fill }, null, new Rect(0, 0, ActualWidth, ActualHeight));
                var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(drawing); bitmap.Freeze(); snapshot = bitmap;
            }
            finally { surface.RenderTransform = transform; }
            islandSnapshotCount++;
            islandExpandedPosition = floatingTransition ? floatingPosition : new Point(bounds.Left, bounds.Top);
            islandDockPosition = floatingTransition ? dockPosition : islandExpandedPosition;
            islandWindow ??= new IslandWindow();
            islandWindow.Prepare(snapshot, ActualWidth, ActualHeight, scale, store.State.Side);
            islandActive = true; islandCovered = false;
            PresentIslandFrame();
            return true;
        }
        catch (Exception error)
        {
            Trace.WriteLine("PathDock snapshot transition unavailable: " + error);
            islandActive = false; islandCovered = false; islandUnavailable = true;
            islandWindow?.Cancel(); return false;
        }
    }
    void PresentIslandFrame()
    {
        var position = new Point(islandDockPosition.X + (islandExpandedPosition.X - islandDockPosition.X) * reveal,
            islandDockPosition.Y + (islandExpandedPosition.Y - islandDockPosition.Y) * reveal);
        islandWindow!.Present(reveal, position);
    }
}
