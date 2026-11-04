using System;
using System.Windows;
namespace PathDock;
internal sealed partial class EdgeWindow
{
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
