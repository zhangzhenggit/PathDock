using System;
using System.Diagnostics;
using System.Windows.Media.Effects;
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
    readonly Border trail = new() { CornerRadius = new CornerRadius(14), Background = Ui.Brush("#8CB9EA"), Opacity = 0, IsHitTestVisible = false };
    readonly RotateTransform tilt = new();
    readonly TranslateTransform trailOffset = new();
    readonly Stopwatch dragClock = new();
    Point dragTarget, dragPosition;
    double lastDragFrame;

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
        var layers = new Grid(); layers.Children.Add(trail); layers.Children.Add(shell); Content = layers;
        shell.RenderTransformOrigin = new Point(.5, .5); shell.RenderTransform = tilt;
        trail.RenderTransform = trailOffset;
        Closed += (_, _) => StopDragVisual();
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
            dragTarget = new Point(cursor.X, cursor.Y);
            if (!dragging) BeginDragVisual(dragTarget);
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
    void BeginDragVisual(Point cursor)
    {
        dragging = true; dragTarget = cursor;
        var bounds = Native.WindowBounds(handle);
        dragPosition = new Point((bounds.Left + bounds.Right) / 2d, (bounds.Top + bounds.Bottom) / 2d);
        shell.Margin = trail.Margin = new Thickness(9);
        shell.CornerRadius = new CornerRadius(12); shell.BorderThickness = new Thickness(1);
        shell.Effect = new DropShadowEffect { Color = Color.FromRgb(32, 70, 116), BlurRadius = 9, ShadowDepth = 2, Opacity = .22 };
        trail.Opacity = .16;
        dragClock.Restart(); lastDragFrame = 0;
        CompositionTarget.Rendering -= FollowDrag; CompositionTarget.Rendering += FollowDrag;
    }
    void FollowDrag(object? sender, EventArgs e)
    {
        if (!dragging) return;
        var now = dragClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - lastDragFrame, 0, .05); lastDragFrame = now;
        var factor = 1 - Math.Exp(-dt / .035);
        var dx = dragTarget.X - dragPosition.X; var dy = dragTarget.Y - dragPosition.Y;
        dragPosition = new Point(dragPosition.X + dx * factor, dragPosition.Y + dy * factor);
        var scale = Native.ScaleAtPoint((int)dragTarget.X, (int)dragTarget.Y);
        var size = (int)Math.Round(62 * scale);
        tilt.Angle = Math.Clamp(dx / scale * .12, -5, 5);
        trailOffset.X = Math.Clamp(-dx / scale * .16, -5, 5);
        trailOffset.Y = Math.Clamp(-dy / scale * .16, -5, 5);
        trail.Opacity = Math.Clamp((Math.Abs(dx) + Math.Abs(dy)) / scale / 160, 0, .18);
        Native.PositionDockWindow(handle, (int)Math.Round(dragPosition.X - size / 2d), (int)Math.Round(dragPosition.Y - size / 2d), size, size);
    }
    void StopDragVisual()
    {
        dragging = false; CompositionTarget.Rendering -= FollowDrag; dragClock.Stop();
        shell.Margin = trail.Margin = new Thickness(0); shell.Effect = null; tilt.Angle = 0;
        trail.Opacity = 0; trailOffset.X = trailOffset.Y = 0;
    }
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
