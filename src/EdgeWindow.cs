using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PathDock;

internal sealed partial class EdgeWindow : Window
{
    IntPtr handle, anchor;
    string side = "right";
    readonly Border shell = new();
    Point? dragOrigin;
    bool dragging;
    public EdgeWindow(Action expand, Action<int, int> dropped)
    {
        Title = "PathDock"; Width = 28; Height = 56;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent; Icon = AppIcon.Image;
        UseLayoutRounding = true;
        shell.Child = new Image { Source = AppIcon.Vector, Width = 20, Height = 20, IsHitTestVisible = false };
        shell.BorderBrush = Ui.Brush("#BCCCDD"); shell.BorderThickness = new Thickness(1);
        shell.Background = new LinearGradientBrush(Color.FromRgb(247, 250, 253), Color.FromRgb(233, 240, 248), 90);
        shell.ToolTip = "PathDock · 拖动可更换停靠位置";
        Content = shell;
        shell.MouseLeftButtonDown += (_, e) =>
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            dragOrigin = new Point(cursor.X, cursor.Y); dragging = false;
            shell.CaptureMouse(); e.Handled = true;
        };
        shell.MouseMove += (_, e) =>
        {
            if (dragOrigin is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var cursor = System.Windows.Forms.Cursor.Position;
            if (!dragging && Math.Abs(cursor.X - start.X) < SystemParameters.MinimumHorizontalDragDistance * Native.WindowScale(handle)
                && Math.Abs(cursor.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance * Native.WindowScale(handle)) return;
            if (!dragging) BeginDragVisual();
            MoveDragTo(new Point(cursor.X, cursor.Y));
        };
        shell.MouseLeftButtonUp += (_, e) =>
        {
            if (dragOrigin == null) return;
            var wasDragging = dragging; dragOrigin = null; StopDragVisual(); shell.ReleaseMouseCapture();
            if (wasDragging) { var cursor = System.Windows.Forms.Cursor.Position; dropped(cursor.X, cursor.Y); }
            else expand();
            e.Handled = true;
        };
        shell.LostMouseCapture += (_, _) => { if (dragOrigin != null) { dragOrigin = null; StopDragVisual(); PositionAtAnchor(); } };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.ConfigureDockFrame(handle); Native.NoActivate(this, true);
            HwndSource.FromHwnd(handle).AddHook((IntPtr w, int m, IntPtr p, IntPtr l, ref bool handled) =>
            {
                if (m == 0x0021) { handled = true; return new IntPtr(3); }
                return IntPtr.Zero;
            });
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!dragging) PositionAtAnchor(); }));
    }
    void BeginDragVisual()
    {
        dragging = true;
        shell.CornerRadius = new CornerRadius(10); shell.BorderThickness = new Thickness(1);
    }
    void MoveDragTo(Point cursor)
    {
        // Move in the mouse event itself: no render loop, interpolation or effects.
        var scale = Native.ScaleAtPoint((int)cursor.X, (int)cursor.Y);
        var size = (int)Math.Round(44 * scale);
        Native.PositionDockWindow(handle, (int)Math.Round(cursor.X - size / 2d), (int)Math.Round(cursor.Y - size / 2d), size, size);
    }
    void StopDragVisual() => dragging = false;
    public void ShowAt(IntPtr mainWindow, string dockSide)
    {
        anchor = mainWindow; side = dockSide;
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        PositionAtAnchor(); if (!IsVisible) Show(); PositionAtAnchor();
    }
    void PositionAtAnchor()
    {
        if (anchor == IntPtr.Zero || handle == IntPtr.Zero || !Native.IsWindow(anchor)) return;
        var bounds = Native.WindowBounds(anchor); var scale = Native.WindowScale(anchor);
        var horizontal = side is "top" or "bottom";
        var width = (int)Math.Round((horizontal ? 56 : 28) * scale);
        var height = (int)Math.Round((horizontal ? 28 : 56) * scale);
        var x = horizontal ? bounds.Left + (bounds.Right - bounds.Left - width) / 2 : side == "left" ? bounds.Left : bounds.Right - width;
        var y = horizontal ? side == "top" ? bounds.Top : bounds.Bottom - height : bounds.Top + (bounds.Bottom - bounds.Top - height) / 2;
        shell.CornerRadius = side switch { "left" => new(0, 10, 10, 0), "top" => new(0, 0, 10, 10), "bottom" => new(10, 10, 0, 0), _ => new(10, 0, 0, 10) };
        shell.BorderThickness = side switch { "left" => new(0, 1, 1, 1), "top" => new(1, 0, 1, 1), "bottom" => new(1, 1, 1, 0), _ => new(1, 1, 0, 1) };
        Native.PositionDockWindow(handle, x, y, width, height);
    }
}
