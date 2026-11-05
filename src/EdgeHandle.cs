using System;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PathDock;

// Add Invoke semantics without changing the dock's mouse capture or activation.
internal sealed class EdgeHandle : Border
{
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
