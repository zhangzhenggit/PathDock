using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PathDock;

// Add Invoke semantics without changing the dock's mouse capture or activation.
internal sealed class EdgeHandle : Border
{
    internal const double ShortExtent = 32, LongExtent = 44, IconSize = 24;
    public static void ConfigureVisual(Border border)
    {
        border.Child = new Image { Source = AppIcon.Vector, Width = IconSize, Height = IconSize, IsHitTestVisible = false, Opacity = border is EdgeHandle ? 0 : 1 };
        border.BorderBrush = Ui.EdgeOutline; border.BorderThickness = new Thickness(1);
        border.Background = Ui.EdgeBackground;
    }
    public static void ApplyDockSide(Border border, string side)
    {
        border.CornerRadius = new CornerRadius(ShortExtent / 2);
        border.BorderThickness = new Thickness(1);
    }
    public Action? Expand { get; set; }
    protected override void OnRender(DrawingContext context)
    {
        // Use the same primitive and inset as the transition, rather than
        // Border's different inner/outer rounded-edge rasterization.
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        context.DrawRoundedRectangle(Background, null, rect, CornerRadius.TopLeft, CornerRadius.TopLeft);
        rect.Inflate(-.5, -.5);
        context.DrawRoundedRectangle(null, new Pen(BorderBrush, 1), rect, CornerRadius.TopLeft - .5, CornerRadius.TopLeft - .5);
        context.DrawImage(AppIcon.Vector, new Rect((ActualWidth - IconSize) / 2, (ActualHeight - IconSize) / 2, IconSize, IconSize));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new HandlePeer(this);
    sealed class HandlePeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        readonly EdgeHandle handle;
        public HandlePeer(EdgeHandle owner) : base(owner) => handle = owner;
        protected override string GetNameCore() => "展开 PathDock";
        protected override string GetClassNameCore() => "PathDockEdgeHandle";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke()
        {
            if (!handle.IsEnabled || !handle.IsVisible) throw new ElementNotEnabledException();
            handle.Dispatcher.BeginInvoke(new Action(() => { if (handle.IsEnabled && handle.IsVisible) handle.Expand?.Invoke(); }));
        }
    }
}
