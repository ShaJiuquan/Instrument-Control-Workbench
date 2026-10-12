using System;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace InstrumentControlWorkbench.App.Controls;

// Keep the standard Button theme, keyboard behavior and command admission.
public sealed class OperatorButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    protected override AutomationPeer OnCreateAutomationPeer() => OperatingSystem.IsMacOS()
        ? new MacOperatorButtonPeer(this) : base.OnCreateAutomationPeer();

    private sealed class MacOperatorButtonPeer(Button owner) : ButtonAutomationPeer(owner), IInvokeProvider
    {
        // Avalonia.Native11.3.8 lets ElementNotEnabledException cross its native
        // callback boundary. A late accessibility invocation is an inert action.
        // This is not a catch-all: enabled command/driver failures remain visible.
        void IInvokeProvider.Invoke()
        {
            if (Owner.IsEffectivelyEnabled) base.Invoke();
        }
    }
}
