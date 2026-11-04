using System;
using System.Windows;
namespace PathDock;
internal sealed partial class EdgeWindow
{
    public async System.Threading.Tasks.Task<bool> CheckDragVisual()
    {
        var bounds = Native.WindowBounds(handle);
        var target = new Point((bounds.Left + bounds.Right) / 2d - 120, (bounds.Top + bounds.Bottom) / 2d + 80);
        BeginDragVisual(target);
        await System.Threading.Tasks.Task.Delay(100);
        var floating = shell.CornerRadius == new CornerRadius(12) && shell.BorderThickness == new Thickness(1) && shell.Effect != null && dragging;
        await System.Threading.Tasks.Task.Delay(180);
        var converged = (dragPosition - target).Length < 2;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"); System.IO.Directory.CreateDirectory(directory);
        using (var file = System.IO.File.Create(System.IO.Path.Combine(directory, "edge-drag.png"))) encoder.Save(file);
        StopDragVisual(); PositionAtAnchor();
        return floating && converged && shell.Effect == null && trail.Opacity == 0 && !dragging;
    }
    public bool IsPresent => IsVisible && WindowState == WindowState.Normal && Native.ReceivesPointerAtCenter(handle);
    public bool IsAtAnchor
    {
        get
        {
            var host = Native.WindowBounds(anchor); var edge = Native.WindowBounds(handle);
            if (side is "top" or "bottom") return Math.Abs((side == "top" ? edge.Top - host.Top : edge.Bottom - host.Bottom)) <= 1
                && Math.Abs((edge.Left + edge.Right) - (host.Left + host.Right)) <= 2;
            return Math.Abs((side == "left" ? edge.Left - host.Left : edge.Right - host.Right)) <= 1
                && Math.Abs((edge.Top + edge.Bottom) - (host.Top + host.Bottom)) <= 2;
        }
    }
}
