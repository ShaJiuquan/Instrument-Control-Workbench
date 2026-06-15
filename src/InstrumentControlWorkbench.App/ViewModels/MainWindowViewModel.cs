using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string AppTitle => "Instrument Control Workbench";
    public string WorkspaceName => "Default lab workspace";
    public string WorkspaceSubtitle => "6 instruments - 1 active run";
    public string BackendStatus => "Mock bench";
    public string RunningCount => ActiveRunState == "Running" ? "1 running" : "0 running";
    public string QueuedCount => "0 queued";
    public string FailedCount => "0 failed";
    public string SafetyState => OutputEnabled ? "Output enabled" : "Safe output off";
    public string OutputState => OutputEnabled ? "ON" : "OFF";
    public string ConnectionState => Connected ? "Connected" : "Disconnected";
    public string SourceLevelDisplay => $"{SourceLevel:0.000} V";
    public string ComplianceDisplay => $"{ComplianceAmps * 1_000_000:0.###} uA";
    public string SweepSummary => $"{SweepPoints} pts  {StartVoltage:0.###} V -> {StopVoltage:0.###} V";
    public string CompletedSummary => $"{CompletedPoints}/{Math.Max(2, SweepPoints)} points";
    public double ProgressPercent => Math.Max(2, SweepPoints) == 0 ? 0 : CompletedPoints * 100.0 / Math.Max(2, SweepPoints);
    public string LeaseStatus => ActiveRunState == "Running" ? "Exclusive lease active" : "No active lease";
    public string SourceRangeText => $"{StartVoltage:0.###} V to {StopVoltage:0.###} V";
    public string DwellText => $"{DwellSeconds:0.###} s / point";
    public string StartVoltageText
    {
        get => StartVoltage.ToString("0.###");
        set
        {
            if (double.TryParse(value, out var parsed))
            {
                StartVoltage = parsed;
            }
        }
    }

    public string StopVoltageText
    {
        get => StopVoltage.ToString("0.###");
        set
        {
            if (double.TryParse(value, out var parsed))
            {
                StopVoltage = parsed;
            }
        }
    }

    public string ComplianceText
    {
        get => ComplianceAmps.ToString("0.#########");
        set
        {
            if (double.TryParse(value, out var parsed))
            {
                ComplianceAmps = parsed;
            }
        }
    }

    public string SweepPointsText
    {
        get => SweepPoints.ToString();
        set
        {
            if (int.TryParse(value, out var parsed))
            {
                SweepPoints = Math.Max(2, parsed);
            }
        }
    }

    public string DwellSecondsText
    {
        get => DwellSeconds.ToString("0.###");
        set
        {
            if (double.TryParse(value, out var parsed))
            {
                DwellSeconds = Math.Max(0.02, parsed);
            }
        }
    }

    private readonly NotificationHub notifications;
    private readonly InstrumentManager instrumentManager;
    private readonly MeasurementRunService runService;
    private readonly CancellationTokenSource notificationCancellation = new();

    public ObservableCollection<NavItem> WorkspaceNav { get; } = new();
    public ObservableCollection<NavItem> ControlNav { get; } = new();
    public ObservableCollection<NavItem> DataNav { get; } = new();
    public ObservableCollection<NavItem> SystemNav { get; } = new();
    public ObservableCollection<InstrumentCard> Instruments { get; } = new();
    public ObservableCollection<JobRow> Jobs { get; } = new();
    public ObservableCollection<MeasurementPointRow> RecentPoints { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    public MainWindowViewModel()
    {
        notifications = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        instrumentManager = new InstrumentManager(driver, notifications);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        runService = new MeasurementRunService(instrumentManager, leases, notifications);

        InstrumentName = driver.DisplayName;
        InstrumentId = driver.Id.Value;
        BuildShellData();
        EventLog.Add("Ready. Mock Keithley 2450 is available.");
        _ = ListenForNotificationsAsync(notificationCancellation.Token);
    }

    [ObservableProperty] private string instrumentName = "";
    [ObservableProperty] private string instrumentId = "";
    [ObservableProperty] private string lifecycle = "Disconnected";
    [ObservableProperty] private string identifier = "not connected";
    [ObservableProperty] private bool connected;
    [ObservableProperty] private bool outputEnabled;
    [ObservableProperty] private string sourceFunction = "Voltage";
    [ObservableProperty] private double sourceLevel;
    [ObservableProperty] private double complianceAmps = 105e-6;
    [ObservableProperty] private string senseFunction = "Current";
    [ObservableProperty] private string sensingMode = "FourWire";
    [ObservableProperty] private string lastHeartbeat = "-";
    [ObservableProperty] private string activeRunName = "IV sweep";
    [ObservableProperty] private string activeRunState = "Idle";
    [ObservableProperty] private string elapsed = "00:00:00";
    [ObservableProperty] private double startVoltage = -0.2;
    [ObservableProperty] private double stopVoltage = 0.8;
    [ObservableProperty] private int sweepPoints = 21;
    [ObservableProperty] private double dwellSeconds = 0.1;
    [ObservableProperty] private string latestReading = "-";
    [ObservableProperty] private int completedPoints;

    partial void OnOutputEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyState));
        OnPropertyChanged(nameof(OutputState));
    }

    partial void OnConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectionState));
    partial void OnSourceLevelChanged(double value) => OnPropertyChanged(nameof(SourceLevelDisplay));
    partial void OnStartVoltageChanged(double value)
    {
        OnPropertyChanged(nameof(SweepSummary));
        OnPropertyChanged(nameof(SourceRangeText));
        OnPropertyChanged(nameof(StartVoltageText));
    }

    partial void OnStopVoltageChanged(double value)
    {
        OnPropertyChanged(nameof(SweepSummary));
        OnPropertyChanged(nameof(SourceRangeText));
        OnPropertyChanged(nameof(StopVoltageText));
    }

    partial void OnComplianceAmpsChanged(double value)
    {
        OnPropertyChanged(nameof(ComplianceDisplay));
        OnPropertyChanged(nameof(ComplianceText));
    }

    partial void OnDwellSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DwellText));
        OnPropertyChanged(nameof(DwellSecondsText));
    }

    partial void OnActiveRunStateChanged(string value)
    {
        OnPropertyChanged(nameof(LeaseStatus));
        OnPropertyChanged(nameof(RunningCount));
    }
    partial void OnSweepPointsChanged(int value)
    {
        OnPropertyChanged(nameof(SweepSummary));
        OnPropertyChanged(nameof(CompletedSummary));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SweepPointsText));
    }

    partial void OnCompletedPointsChanged(int value)
    {
        OnPropertyChanged(nameof(CompletedSummary));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            await instrumentManager.ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            EventLog.Insert(0, $"Connect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartRunAsync()
    {
        try
        {
            RecentPoints.Clear();
            CompletedPoints = 0;
            ActiveRunState = "Starting";
            var recipe = new IvSweepRecipe(
                ActiveRunName,
                StartVoltage,
                StopVoltage,
                Math.Max(2, SweepPoints),
                ComplianceAmps,
                Math.Max(0.02, DwellSeconds));
            await runService.StartIvSweepAsync(recipe, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ActiveRunState = "Faulted";
            EventLog.Insert(0, $"Run failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AbortRun()
    {
        runService.Abort();
        EventLog.Insert(0, "Abort requested by operator.");
    }

    private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken))
        {
            Dispatcher.UIThread.Post(() => ApplyNotification(notification));
        }
    }

    private void ApplyNotification(WorkbenchNotification notification)
    {
        switch (notification.Kind)
        {
            case WorkbenchNotificationKind.InstrumentStateChanged when notification.Snapshot is { } snapshot:
                ApplySnapshot(snapshot);
                break;
            case WorkbenchNotificationKind.MeasurementPointReceived when notification.Point is { } point:
                CompletedPoints += 1;
                RecentPoints.Insert(0, MeasurementPointRow.FromPoint(CompletedPoints, point));
                LatestReading = $"{point.SourceValue:0.###} {point.SourceUnit} -> {point.MeasuredValue:0.###E+0} {point.MeasuredUnit}";
                Elapsed = point.Elapsed.ToString(@"hh\:mm\:ss");
                ActiveRunState = "Running";
                break;
            case WorkbenchNotificationKind.RunStarted:
                ActiveRunState = "Running";
                break;
            case WorkbenchNotificationKind.RunCompleted:
                ActiveRunState = "Completed";
                break;
            case WorkbenchNotificationKind.RunAborted:
                ActiveRunState = "Aborted";
                break;
            case WorkbenchNotificationKind.RunFaulted:
                ActiveRunState = "Faulted";
                break;
        }

        EventLog.Insert(0, $"{notification.OccurredAt:HH:mm:ss}  {notification.Message}");
        while (EventLog.Count > 80)
        {
            EventLog.RemoveAt(EventLog.Count - 1);
        }
    }

    private void ApplySnapshot(SourceMeterSnapshot snapshot)
    {
        Identifier = snapshot.Identifier;
        Lifecycle = snapshot.Lifecycle.ToString();
        Connected = snapshot.Connected;
        OutputEnabled = snapshot.OutputEnabled;
        SourceFunction = snapshot.Source.Function.ToString();
        SourceLevel = snapshot.Source.Level;
        ComplianceAmps = snapshot.Source.Compliance;
        SenseFunction = snapshot.Sense.Function.ToString();
        SensingMode = snapshot.Sense.Mode.ToString();
        LastHeartbeat = snapshot.LastHeartbeat == DateTimeOffset.MinValue ? "-" : snapshot.LastHeartbeat.ToString("HH:mm:ss.fff");
    }

    private void BuildShellData()
    {
        WorkspaceNav.Add(new NavItem("Overview", "OV", "", false));
        WorkspaceNav.Add(new NavItem("Instruments", "IN", "6", true));
        WorkspaceNav.Add(new NavItem("Experiments", "EX", "3", false));
        WorkspaceNav.Add(new NavItem("Measurements", "ME", "", false));

        ControlNav.Add(new NavItem("Connections", "CN", "2", false));
        ControlNav.Add(new NavItem("Source/Meter", "SM", "", false));
        ControlNav.Add(new NavItem("Scripts", "SC", "4", false));
        ControlNav.Add(new NavItem("Workflows", "WF", "1", false));

        DataNav.Add(new NavItem("Live Data", "LD", "", false));
        DataNav.Add(new NavItem("History", "HI", "18", false));
        DataNav.Add(new NavItem("Plot", "PL", "", false));
        DataNav.Add(new NavItem("Export", "EX", "", false));

        SystemNav.Add(new NavItem("Jobs", "JB", "0", false));
        SystemNav.Add(new NavItem("Logs", "LG", "", false));
        SystemNav.Add(new NavItem("Safety", "SF", "", false));
        SystemNav.Add(new NavItem("Settings", "ST", "", false));

        Instruments.Add(new InstrumentCard("Keithley 2450 SourceMeter", "k2450", "SMU", "TCP 127.0.0.1:5025", "Disconnected", "source meter", "selected"));
        Instruments.Add(new InstrumentCard("Keithley 2182A Nanovoltmeter", "k2182a", "Meter", "GPIB0::7::INSTR", "Idle", "nanovoltmeter", ""));
        Instruments.Add(new InstrumentCard("SR830 Lock-in Amplifier", "sr830", "Lock-in", "GPIB0::8::INSTR", "Idle", "ac measurement", ""));
        Instruments.Add(new InstrumentCard("K6221 Current Source", "k6221", "Source", "GPIB0::12::INSTR", "Disconnected", "current source", ""));
        Instruments.Add(new InstrumentCard("K3706 Switch Matrix", "k3706", "Switch", "TCP 127.0.0.1:5026", "Stale", "matrix", ""));
        Instruments.Add(new InstrumentCard("Temperature Controller", "temp", "Env", "COM4", "Idle", "environment", ""));

        Jobs.Add(new JobRow("Idle", "IV sweep recipe", "ready", "No active run"));
        Jobs.Add(new JobRow("Ready", "Instrument poller", "background", "TTL heartbeat 5 s"));
    }
}

public sealed record MeasurementPointRow(
    string Index,
    string Time,
    string Source,
    string Measured,
    string Status)
{
    public static MeasurementPointRow FromPoint(int index, MeasurementPoint point)
    {
        return new MeasurementPointRow(
            index.ToString("000"),
            point.Elapsed.ToString(@"mm\:ss\.ff"),
            $"{point.SourceValue,7:0.000} {point.SourceUnit}",
            $"{point.MeasuredValue,10:0.000E+0} {point.MeasuredUnit}",
            "OK");
    }
}

public sealed record NavItem(string Label, string Glyph, string Count, bool Selected)
{
    public bool HasCount => !string.IsNullOrWhiteSpace(Count);
}

public sealed record InstrumentCard(
    string Name,
    string Id,
    string Role,
    string Address,
    string State,
    string Detail,
    string SelectedToken)
{
    public bool IsSelected => SelectedToken == "selected";
}

public sealed record JobRow(string Status, string Title, string Type, string Detail);
