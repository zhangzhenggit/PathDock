using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PathDock;

internal sealed partial class EdgeWindow : Window
{
    IntPtr handle;
    IntPtr anchor;
    bool leftSide;
    readonly Border shell = new();
    public EdgeWindow(Action expand)
    {
        Title = "PathDock"; Width = 28; Height = 56;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Ui.Brush("#F1F5FA"); Icon = AppIcon.Image;
        Ui.CrispText(this);
        var button = Ui.Button(new Image { Source = AppIcon.Image, Width = 16, Height = 16 });
        button.Background = Brushes.Transparent; button.Padding = new Thickness(0); button.ToolTip = "PathDock";
        button.Click += (_, _) => expand();
        shell.Child = button; shell.BorderBrush = Ui.Brush("#D8E0EA"); shell.BorderThickness = new Thickness(1);
        shell.Background = new LinearGradientBrush(Color.FromRgb(247, 250, 253), Color.FromRgb(233, 240, 248), 90);
        shell.CornerRadius = new CornerRadius(10, 0, 0, 10); Content = shell;
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
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PositionAtAnchor));
    }
    public void ShowAt(IntPtr mainWindow, bool dockLeft)
    {
        anchor = mainWindow; leftSide = dockLeft;
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        PositionAtAnchor();
        if (!IsVisible) Show();
        PositionAtAnchor();
    }
    void PositionAtAnchor()
    {
        if (anchor == IntPtr.Zero || handle == IntPtr.Zero || !Native.IsWindow(anchor)) return;
        var bounds = Native.WindowBounds(anchor); var scale = Native.WindowScale(anchor);
        var width = (int)Math.Round(28 * scale); var height = (int)Math.Round(56 * scale);
        Native.PositionDockWindow(handle, leftSide ? bounds.Left : bounds.Right - width,
            bounds.Top + (bounds.Bottom - bounds.Top - height) / 2, width, height);
        shell.CornerRadius = leftSide ? new CornerRadius(0, 10, 10, 0) : new CornerRadius(10, 0, 0, 10);
        shell.BorderThickness = leftSide ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
        Native.RoundEdgeWindow(handle, width, height, (int)Math.Round(10 * scale), leftSide);
    }
}
