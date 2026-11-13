using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PathDock;

// One frozen snapshot, one passive HWND and one drawing surface per transition.
// Text is never remeasured while the card contracts into the edge capsule.
internal sealed class IslandWindow : Window
{
    internal const double ScenePadding = 12;
    readonly IslandVisual scene = new();
    IntPtr handle;
    double scale = 1, panelWidth, panelHeight;
    string side = "right";
    (int X, int Y, int Width, int Height)? placement;
    int remainingHandoffFrames;
    TimeSpan handoffTime = TimeSpan.MinValue;
    int presentationFrames;
    TimeSpan presentationTime = TimeSpan.MinValue;
    bool drawingDirty;
    public bool HasPresented { get; private set; }
    public bool HasSnapshot => scene.Snapshot != null;
    internal bool SnapshotFrozen => scene.Snapshot?.IsFrozen == true;
    public IslandFrame Frame => scene.Frame;
    public Rect CardScreenBounds { get; private set; }
    public IslandWindow()
    {
        Title = "PathDock transition"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowActivated = false; ShowInTaskbar = false; IsHitTestVisible = false;
        Content = scene;
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; Native.PassThrough(this); };
        Closed += (_, _) => { CompositionTarget.Rendering -= FinishHandoff; CompositionTarget.Rendering -= WatchPresentation; scene.Release(); };
    }
    public void Prepare(BitmapSource snapshot, double width, double height, double dpiScale, string dockSide, BitmapSource capsule)
    {
        CompositionTarget.Rendering -= FinishHandoff; remainingHandoffFrames = 0;
        panelWidth = width; panelHeight = height; scale = dpiScale; side = dockSide;
        scene.Snapshot = snapshot;
        scene.Capsule = capsule;
        var soft = new TransformedBitmap(snapshot, new ScaleTransform(.16, .16));
        var stride = soft.PixelWidth * 4;
        var pixels = new byte[stride * soft.PixelHeight]; soft.CopyPixels(pixels, stride, 0);
        var materialized = BitmapSource.Create(soft.PixelWidth, soft.PixelHeight, soft.DpiX, soft.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
        materialized.Freeze(); scene.SoftSnapshot = materialized;
        drawingDirty = true;
        scene.Width = width + ScenePadding * 2; scene.Height = height + ScenePadding * 2;
        Width = scene.Width; Height = scene.Height;
        if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).EnsureHandle();
        placement = null; HasPresented = IsVisible;
        presentationFrames = 0; presentationTime = TimeSpan.MinValue;
        CompositionTarget.Rendering -= WatchPresentation;
        if (!HasPresented) CompositionTarget.Rendering += WatchPresentation;
    }
    void WatchPresentation(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == presentationTime) return;
            presentationTime = frame.RenderingTime;
        }
        if (++presentationFrames < 2) return;
        HasPresented = true;
        CompositionTarget.Rendering -= WatchPresentation;
    }
    public void Present(double reveal, Point basePosition)
    {
        var geometry = IslandGeometry.Sample(panelWidth, panelHeight, side, reveal, scale);
        var changed = drawingDirty || scene.Frame != geometry;
        scene.Frame = geometry;
        var next = ((int)Math.Round(basePosition.X - ScenePadding * scale), (int)Math.Round(basePosition.Y - ScenePadding * scale),
            (int)Math.Round((panelWidth + ScenePadding * 2) * scale), (int)Math.Round((panelHeight + ScenePadding * 2) * scale));
        if (placement != next && Native.PositionDockWindow(handle, next.Item1, next.Item2, next.Item3, next.Item4)) placement = next;
        CardScreenBounds = new(basePosition.X + geometry.Bounds.X * scale, basePosition.Y + geometry.Bounds.Y * scale,
            geometry.Bounds.Width * scale, geometry.Bounds.Height * scale);
        if (changed) { scene.DrawFrame(); drawingDirty = false; }
        if (!IsVisible)
        {
            Show();
            // WPF's first Show can reapply its pre-monitor-DPI size. Confirm
            // the physical bounds afterwards so the card doesn't shift on its
            // first transition or after crossing to a differently scaled screen.
            if (Native.PositionDockWindow(handle, next.Item1, next.Item2, next.Item3, next.Item4)) placement = next;
        }
    }
    public void CompleteHandoff()
    {
        Native.KeepAbove(handle);
        remainingHandoffFrames = 2; handoffTime = TimeSpan.MinValue;
        CompositionTarget.Rendering -= FinishHandoff; CompositionTarget.Rendering += FinishHandoff;
    }
    public void Cancel()
    {
        CompositionTarget.Rendering -= FinishHandoff; CompositionTarget.Rendering -= WatchPresentation;
        remainingHandoffFrames = 0; HasPresented = false;
        Hide(); scene.Release();
    }
    public void Prewarm()
    {
        if (IsVisible || HasSnapshot) return;
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Pbgra32, null, new byte[32 * 32 * 4], 32 * 4);
        bitmap.Freeze();
        Prepare(bitmap, 32, 56, 1, "right", bitmap);
        scene.Measure(new Size(56, 80)); scene.Arrange(new Rect(0, 0, 56, 80));
        foreach (var amount in new[] { 1d, .5, 0 })
        {
            scene.Frame = IslandGeometry.Sample(32, 56, "right", amount); scene.DrawFrame();
            var target = new RenderTargetBitmap(56, 80, 96, 96, PixelFormats.Pbgra32); target.Render(scene);
        }
        Cancel();
    }
    void FinishHandoff(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == handoffTime) return;
            handoffTime = frame.RenderingTime;
        }
        if (--remainingHandoffFrames > 0) return;
        CompositionTarget.Rendering -= FinishHandoff;
        Hide(); scene.Release();
    }
    internal FrameworkElement DrawingSurface => scene;
    sealed class IslandVisual : FrameworkElement
    {
        readonly DrawingVisual drawing = new();
        public IslandVisual() { AddVisualChild(drawing); }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0 ? drawing : throw new ArgumentOutOfRangeException(nameof(index));
        public BitmapSource? Snapshot, SoftSnapshot, Capsule;
        public IslandFrame Frame;
        readonly RectangleGeometry clip = new();
        static readonly Brush shadowOuter = FrozenBrush(5), shadowMiddle = FrozenBrush(9), shadowInner = FrozenBrush(12);
        static Brush FrozenBrush(byte alpha) { var brush = new SolidColorBrush(Color.FromArgb(alpha, 22, 30, 42)); brush.Freeze(); return brush; }
        public void Release() { Snapshot = null; SoftSnapshot = null; Capsule = null; using var context = drawing.RenderOpen(); }
        public void DrawFrame()
        {
            using var context = drawing.RenderOpen();
            if (Snapshot == null) return;
            var rect = Frame.Bounds; rect.Offset(ScenePadding, ScenePadding);
            if (Frame.IconOpacity == 1)
            {
                context.DrawImage(Capsule, rect);
                return;
            }
            // Small fixed shadow rings, not a live blur over a full window.
            context.PushOpacity(4 * Frame.SharpOpacity * (1 - Frame.SharpOpacity));
            var shadow = rect; shadow.Inflate(6, 6); context.DrawRoundedRectangle(shadowOuter, null, shadow, Frame.Radius + 6, Frame.Radius + 6);
            shadow = rect; shadow.Inflate(3, 3); context.DrawRoundedRectangle(shadowMiddle, null, shadow, Frame.Radius + 3, Frame.Radius + 3);
            shadow = rect; shadow.Inflate(1, 1); context.DrawRoundedRectangle(shadowInner, null, shadow, Frame.Radius + 1, Frame.Radius + 1);
            context.Pop();
            var blend = Frame.IconOpacity;
            var fill = new SolidColorBrush(Color.FromRgb((byte)(251 - 219 * blend), (byte)(251 - 218 * blend), (byte)(252 - 215 * blend)));
            context.DrawRoundedRectangle(fill, null, rect, Frame.Radius, Frame.Radius);
            clip.Rect = rect; clip.RadiusX = clip.RadiusY = Frame.Radius;
            context.PushClip(clip);
            if (Frame.SoftOpacity > 0) { context.PushOpacity(Frame.SoftOpacity); context.DrawImage(SoftSnapshot, rect); context.Pop(); }
            if (Frame.SharpOpacity > 0) { context.PushOpacity(Frame.SharpOpacity); context.DrawImage(Snapshot, rect); context.Pop(); }
            if (Frame.IconOpacity > 0)
            {
                context.PushOpacity(Frame.IconOpacity);
                context.DrawImage(AppIcon.Vector, new Rect(rect.X + (rect.Width - 26) / 2, rect.Y + (rect.Height - 26) / 2, 26, 26));
                context.Pop();
            }
            context.Pop();
            var border = rect; border.Inflate(-.5, -.5);
            var borderBrush = new SolidColorBrush(Color.FromRgb((byte)(215 - 163 * blend), (byte)(219 - 165 * blend), (byte)(226 - 167 * blend)));
            context.DrawRoundedRectangle(null, new Pen(borderBrush, 1), border, Frame.Radius - .5, Frame.Radius - .5);
        }
    }
}
