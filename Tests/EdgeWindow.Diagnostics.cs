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
            return Math.Abs((leftSide ? edge.Left - host.Left : edge.Right - host.Right)) <= 1
                && Math.Abs((edge.Top + edge.Bottom) - (host.Top + host.Bottom)) <= 2;
        }
    }
}
