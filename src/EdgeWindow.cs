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
    readonly EdgeHandle shell = new();
    Point? dragOrigin;
    bool dragging, warming;
    bool tailActive;
    int dragRegionPixels;
    (int X, int Y, int Width, int Height, bool Behind)? lastPlacement;
    string? visualSide;
    public bool TailActive => tailActive;
    internal System.Windows.Media.Imaging.BitmapSource CaptureCapsule()
    {
        var opacity = shell.Opacity;
        try
        {
            shell.Opacity = 1; shell.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(shell);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Round(shell.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(shell.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(shell); bitmap.Freeze(); return bitmap;
        }
        finally { shell.Opacity = opacity; }
    }
    public void SuspendInput() => shell.IsEnabled = false;
    public void KeepOpeningCover() { Topmost = true; Native.KeepAbove(handle); }
    public void RevealHandle()
    {
        if (!warming || !HasRenderedContent) return;
        // The panel slides behind a fully opaque, fixed-size icon. There is
        // no rectangular backdrop or opacity dip to change its corner shape.
        shell.Opacity = 1;
        if (!tailActive)
        {
            tailActive = true; Topmost = true; PositionAtAnchor();
            var bounds = Native.WindowBounds(handle);
            Native.SetVisibleRegion(handle, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0);
        }
    }
    public void ClearTail()
    {
        tailActive = false; shell.Opacity = 1;
    }
    bool hasRenderedContent;
    partial void OverrideReadiness(ref bool ready);
    public bool HasRenderedContent { get { var ready = hasRenderedContent; OverrideReadiness(ref ready); return ready; } }
    public EdgeWindow(Action expand, Action<int, int> dropped)
    {
        Title = "PathDock"; Width = EdgeHandle.ShortExtent; Height = EdgeHandle.LongExtent;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent; Icon = AppIcon.Image;
        ContentRendered += (_, _) => hasRenderedContent = true;
        UseLayoutRounding = true;
        shell.Expand = expand;
        EdgeHandle.ConfigureVisual(shell);
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
        dragRegionPixels = 0;
        visualSide = null; lastPlacement = null;
        Width = Height = 44;
        shell.CornerRadius = new CornerRadius(22); shell.BorderThickness = new Thickness(1);
    }
    void MoveDragTo(Point cursor)
    {
        // Move in the mouse event itself: no render loop, interpolation or effects.
        var scale = Native.ScaleAtPoint((int)cursor.X, (int)cursor.Y);
        var size = (int)Math.Round(44 * scale);
        Native.PositionDockWindow(handle, (int)Math.Round(cursor.X - size / 2d), (int)Math.Round(cursor.Y - size / 2d), size, size);
        // SetWindowPos does not discard the previous dock capsule's HRGN.
        // In particular a horizontal 44x32 region cuts the bottom off 44x44.
        if (dragRegionPixels != size && Native.SetVisibleRegion(handle, 0, 0, size, size, 0)) dragRegionPixels = size;
    }
    void StopDragVisual() { dragging = false; lastPlacement = null; }
    public void PrepareAt(IntPtr mainWindow, string dockSide)
    {
        anchor = mainWindow; side = dockSide;
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        PositionAtAnchor();
    }
    public void WarmBehind(IntPtr mainWindow, string dockSide)
    {
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        Native.SetEmptyVisibleRegion(handle);
        tailActive = false; warming = true; shell.IsEnabled = false; Topmost = false;
        shell.Opacity = 0;
        PrepareAt(mainWindow, dockSide);
        if (!IsVisible) { Show(); PositionAtAnchor(); }
        Native.SetEmptyVisibleRegion(handle);
    }
    public void TrackBehind() { if (warming && IsVisible) PositionAtAnchor(false); }
    public void ShowAt(IntPtr mainWindow, string dockSide)
    {
        anchor = mainWindow; side = dockSide;
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        ClearTail(); warming = false; shell.IsEnabled = true; Topmost = true;
        PositionAtAnchor(); if (!IsVisible) { Show(); PositionAtAnchor(); }
        var bounds = Native.WindowBounds(handle);
        Native.SetVisibleRegion(handle, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0);
    }
    void PositionAtAnchor(bool force = true)
    {
        if (anchor == IntPtr.Zero || handle == IntPtr.Zero || !Native.IsWindow(anchor)) return;
        var bounds = Native.WindowBounds(anchor); var scale = Native.WindowScale(anchor);
        var horizontal = side is "top" or "bottom";
        Width = horizontal ? EdgeHandle.LongExtent : EdgeHandle.ShortExtent;
        Height = horizontal ? EdgeHandle.ShortExtent : EdgeHandle.LongExtent;
        var width = (int)Math.Round((horizontal ? EdgeHandle.LongExtent : EdgeHandle.ShortExtent) * scale);
        var height = (int)Math.Round((horizontal ? EdgeHandle.ShortExtent : EdgeHandle.LongExtent) * scale);
        var x = horizontal ? bounds.Left + (int)Math.Round((bounds.Right - bounds.Left - width) / 2d) : side == "left" ? bounds.Left : bounds.Right - width;
        var y = horizontal ? side == "top" ? bounds.Top : bounds.Bottom - height : bounds.Top + (int)Math.Round((bounds.Bottom - bounds.Top - height) / 2d);
        if (visualSide != side) { EdgeHandle.ApplyDockSide(shell, side); visualSide = side; }
        var placement = (x, y, width, height, warming && !tailActive);
        if (!force && lastPlacement == placement) return;
        var applied = placement.Item5 ? Native.PositionDockBehind(handle, anchor, x, y, width, height)
            : Native.PositionDockWindow(handle, x, y, width, height);
        if (applied) lastPlacement = placement;
    }
}
