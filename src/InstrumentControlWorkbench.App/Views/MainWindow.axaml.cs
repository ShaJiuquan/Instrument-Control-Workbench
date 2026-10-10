using Avalonia.Controls;
using InstrumentControlWorkbench.App.ViewModels;

namespace InstrumentControlWorkbench.App.Views;

public partial class MainWindow : Window
{
    private bool cleanupComplete;
    private bool cleanupStarted;
    public MainWindow() => InitializeComponent();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (cleanupComplete) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (cleanupStarted) return;
        cleanupStarted = true;
        if (DataContext is MainWindowViewModel viewModel) await viewModel.DisposeAsync();
        cleanupComplete = true;
        Close();
    }
}
