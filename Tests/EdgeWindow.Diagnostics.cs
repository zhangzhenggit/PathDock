using System;
using System.Windows;
namespace PathDock;
internal sealed partial class EdgeWindow
{
    public async System.Threading.Tasks.Task<bool> CheckDragVisual()
    {
        var bounds = Native.WindowBounds(handle);
        var target = new Point((bounds.Left + bounds.Right) / 2d - 120, (bounds.Top + bounds.Bottom) / 2d + 80);
        BeginDragVisual();
        MoveDragTo(target);
        var immediate = true;
        foreach (var offset in new[] { new Vector(30, 20), new Vector(-80, 40), new Vector(0, 0) }) {
            var point = target + offset; MoveDragTo(point);
            var actual = Native.WindowBounds(handle);
            immediate &= Math.Abs((actual.Left + actual.Right) / 2d - point.X) <= 1 && Math.Abs((actual.Top + actual.Bottom) / 2d - point.Y) <= 1;
        }
        await System.Threading.Tasks.Task.Delay(50);
        var floating = shell.CornerRadius == new CornerRadius(10) && shell.BorderThickness == new Thickness(1) && shell.Effect == null && dragging;

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"); System.IO.Directory.CreateDirectory(directory);
        using (var file = System.IO.File.Create(System.IO.Path.Combine(directory, "edge-drag.png"))) encoder.Save(file);
        StopDragVisual(); PositionAtAnchor();
        return floating && immediate && shell.Effect == null && !dragging;
    }
    public bool HasAccessibleButton
    {
        get {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(shell);
            return peer?.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Button
                && peer.GetName() == "展开 PathDock" && peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider;
        }
    }
    public void InvokeAccessibleButton()
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(shell)!;
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
    }
    public void SetRenderedStateForTest(bool ready) => HasRenderedContent = ready;
    public bool IsCoveredByMain => warming && IsVisible && !shell.IsEnabled && Native.CoveredByWindow(handle, anchor);
    public bool PresentationMatches(System.Windows.Controls.Border other)
    {
        var a = (System.Windows.Controls.Image)shell.Child; var b = (System.Windows.Controls.Image)other.Child;
        var x = (System.Windows.Media.LinearGradientBrush)shell.Background; var y = (System.Windows.Media.LinearGradientBrush)other.Background;
        return shell.CornerRadius == other.CornerRadius && shell.BorderThickness == other.BorderThickness
            && ReferenceEquals(a.Source, b.Source) && a.Width == b.Width && a.Height == b.Height
            && x.GradientStops[0].Color == y.GradientStops[0].Color && x.GradientStops[1].Color == y.GradientStops[1].Color;
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
