using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.App.Views;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;

using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var loadingText = new TextBlock { Text = "正在读取设备配置…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var loading = new Window
            {
                Title = "Instrument Workbench · 启动",
                Width = 700,
                Height = 340,
                Content = new Border { Padding = new Thickness(28), Child = loadingText }
            };
            desktop.MainWindow = loading;
            loading.Closed += (_, _) => startupCancellation.Cancel();
            base.OnFrameworkInitializationCompleted();
            loading.Show();
            startupTask = InitializeWorkbenchAsync(desktop, loading, loadingText);
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private readonly CancellationTokenSource startupCancellation = new();
    private Task? startupTask;
    private async Task InitializeWorkbenchAsync(IClassicDesktopStyleApplicationLifetime desktop, Window loading, TextBlock loadingText)
    {
        FileRunArchive? archive = null;
        MainWindowViewModel? owner = null;
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstrumentControlWorkbench");
        var devicePath = Path.Combine(dataRoot, "devices.json");
        try
        {
            var notifications = new NotificationHub();
            var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var deviceDocument = new JsonDeviceConfigurationDocument();
            archive = new FileRunArchive(Path.Combine(dataRoot, "runs"));
            var started = await new DeviceConfigurationStartup(deviceDocument, new ConfiguredDeviceFactory(), leases, notifications)
                .StartAsync(devicePath, DemoDeviceConfiguration.Create(), startupCancellation.Token, [archive]);
            startupCancellation.Token.ThrowIfCancellationRequested();
            var runtime = started.Devices;
            var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), started.Runs, new PpmsExperimentPlanImporter());
            var settings = new DeviceSettingsViewModel(new DeviceConfigurationWorkspace(deviceDocument, new PpmsDeviceConfigurationImporter()), started.Document, devicePath);
            owner = new MainWindowViewModel(runtime.Configuration.PrimarySource is null ? null : runtime.PrimarySource,
                started.Runs, notifications, workspace, DemoExperimentPlans.Initial(runtime), Path.Combine(dataRoot, "plans", "experiment.plan.json"),
                new RunHistoryService(archive), Path.Combine(dataRoot, "exports"), runtime.Registry, settings, archive);
            owner.EventLog.Insert(0, started.Origin == DeviceConfigurationOrigin.SavedFile ? "已使用保存的设备配置启动。" : "首次启动使用明确的仿真配置；可在仪器页保存设备文档。");
            var window = new MainWindow { DataContext = owner };
            desktop.MainWindow = window;
            window.Show(); loading.Close();
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        { if (owner is not null) await owner.DisposeAsync(); else if (archive is not null) await archive.DisposeAsync(); }
        catch (Exception ex)
        {
            if (owner is not null) await owner.DisposeAsync(); else if (archive is not null) await archive.DisposeAsync();
            loadingText.Text = $"设备启动失败：{ex.Message}\n\n配置路径：{devicePath}\n\n修正配置后重新启动。已有配置不会自动替换为仿真；请检查设备类型、连接参数与支持的驱动。";
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
