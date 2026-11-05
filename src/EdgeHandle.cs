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
    public static void ConfigureVisual(Border border)
    {
        border.Child = new Image { Source = AppIcon.Vector, Width = 26, Height = 26, IsHitTestVisible = false };
        border.BorderBrush = Ui.Brush("#BCCCDD"); border.BorderThickness = new Thickness(1);
        border.Background = new LinearGradientBrush(Color.FromRgb(247, 250, 253), Color.FromRgb(233, 240, 248), 90);
    }
    public static void ApplyDockSide(Border border, string side)
    {
        border.CornerRadius = side switch { "left" => new(0, 10, 10, 0), "top" => new(0, 0, 10, 10), "bottom" => new(10, 10, 0, 0), _ => new(10, 0, 0, 10) };
        border.BorderThickness = side switch { "left" => new(0, 1, 1, 1), "top" => new(1, 0, 1, 1), "bottom" => new(1, 1, 1, 0), _ => new(1, 1, 0, 1) };
    }
    public Action? Expand { get; set; }
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
