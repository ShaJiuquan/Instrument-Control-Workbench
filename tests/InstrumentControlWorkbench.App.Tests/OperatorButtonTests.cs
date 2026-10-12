using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using InstrumentControlWorkbench.App.Controls;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class OperatorButtonTests
{
    [Fact]
    public void CapturedProviderUsesCurrentEnabledStateWithoutInvokingDisabledCommands()
    {
        var command = new CountingCommand(); var button = new OperatorButton { Command = command };
        var peer = ControlAutomationPeer.CreatePeerForElement(button)!; var provider = peer.GetProvider<IInvokeProvider>()!;
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        provider.Invoke(); Assert.Equal(1, command.Calls);
        button.IsEnabled = false;
        InvokeDisabled(provider); Assert.Equal(1, command.Calls); Assert.False(peer.IsEnabled());
        button.IsEnabled = true;
        provider.Invoke(); Assert.Equal(2, command.Calls);
    }
    [Fact]
    public void CommandAdmissionAndDisabledAncestorAreRespectedByNativeProvider()
    {
        var command = new CountingCommand(); var button = new OperatorButton { Command = command };
        var parent = new StackPanel(); parent.Children.Add(button);
        var provider = ControlAutomationPeer.CreatePeerForElement(button)!.GetProvider<IInvokeProvider>()!;
        command.Allowed = false; command.Notify();
        InvokeDisabled(provider); Assert.Equal(0, command.Calls);
        command.Allowed = true; command.Notify(); parent.IsEnabled = false;
        InvokeDisabled(provider); Assert.Equal(0, command.Calls);
        parent.IsEnabled = true; provider.Invoke(); Assert.Equal(1, command.Calls);
    }
    [Fact]
    public void EnabledCommandFaultIsNotSwallowedByTheAccessibilityGuard()
    {
        var command = new CountingCommand { Fail = true }; var button = new OperatorButton { Command = command };
        var provider = ControlAutomationPeer.CreatePeerForElement(button)!.GetProvider<IInvokeProvider>()!;
        Assert.Throws<InvalidOperationException>(() => provider.Invoke()); Assert.Equal(1, command.Calls);
    }
    private static void InvokeDisabled(IInvokeProvider provider)
    {
        if (OperatingSystem.IsMacOS()) provider.Invoke();
        else Assert.Throws<ElementNotEnabledException>(() => provider.Invoke());
    }
    private sealed class CountingCommand : ICommand
    {
        public int Calls { get; private set; }
        public bool Allowed { get; set; } = true;
        public bool Fail { get; set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => Allowed;
        public void Execute(object? parameter) { Calls++; if (Fail) throw new InvalidOperationException("Visible command failure"); }
        public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
