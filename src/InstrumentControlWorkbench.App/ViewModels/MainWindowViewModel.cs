using System;
using System.Collections.ObjectModel;
using System.Globalization;
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

namespace InstrumentControlWorkbench.App.ViewModels;

public enum WorkbenchPage { Measurement, Instruments, Data, Events }
public enum OperatorRunState { Ready, Starting, Running, Stopping, Completed, Aborted, Faulted }

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly NotificationHub notifications;
    private readonly InstrumentManager instrumentManager;
    private readonly MeasurementRunService runService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task notificationListener;
    private bool disposed;
    private bool closing;
    private int plannedPoints = 21;

    public MainWindowViewModel(InstrumentManager instrumentManager, MeasurementRunService runService, NotificationHub notifications)
    {
        this.instrumentManager = instrumentManager;
        this.runService = runService;
        this.notifications = notifications;
        InstrumentId = instrumentManager.InstrumentId.Value;
        EventLog.Add("工作台已就绪。当前使用 Keithley 2450 仿真驱动。");
        notificationListener = ListenForNotificationsAsync(lifetime.Token);
    }

    public string InstrumentId { get; }
    public ObservableCollection<MeasurementPoint> PlotPoints { get; } = new();
    public ObservableCollection<MeasurementPointRow> RecentPoints { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    [ObservableProperty] private WorkbenchPage page = WorkbenchPage.Measurement;
    [ObservableProperty] private OperatorRunState runState = OperatorRunState.Ready;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private bool connected;
    [ObservableProperty] private bool outputEnabled;
    [ObservableProperty] private bool hasSnapshot;
    [ObservableProperty] private bool showTable;
    [ObservableProperty] private string identifier = "尚未连接";
    [ObservableProperty] private string lifecycle = "Disconnected";
    [ObservableProperty] private string sampleName = "Sample A01";
    [ObservableProperty] private string startVoltageText = "-0.2";
    [ObservableProperty] private string stopVoltageText = "0.8";
    [ObservableProperty] private string sweepPointsText = "21";
    [ObservableProperty] private string dwellSecondsText = "0.1";
    [ObservableProperty] private string complianceText = "105";
    [ObservableProperty] private string runId = "尚未运行";
    [ObservableProperty] private string runRecipeSummary = "启动后记录本次扫描配置";
    [ObservableProperty] private string runSample = "—";
    [ObservableProperty] private string elapsed = "00:00.00";
    [ObservableProperty] private string lastUpdate = "—";
    [ObservableProperty] private string latestCurrent = "—";
    [ObservableProperty] private string latestVoltage = "—";
    [ObservableProperty] private string readbackVoltage = "—";
    [ObservableProperty] private string readbackCompliance = "—";
    [ObservableProperty] private int completedPoints;
    [ObservableProperty] private string operationError = "";

    public bool IsMeasurementPage => Page == WorkbenchPage.Measurement;
    public bool IsInstrumentsPage => Page == WorkbenchPage.Instruments;
    public bool IsDataPage => Page == WorkbenchPage.Data;
    public bool IsEventsPage => Page == WorkbenchPage.Events;
    public bool CanEdit => !IsBusy && !IsConnecting && !runService.HasActiveRun;
    public bool HasPoints => CompletedPoints > 0;
    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);
    public string PageTitle => Page switch
    {
        WorkbenchPage.Measurement => "测量工作台",
        WorkbenchPage.Instruments => "仪器与连接",
        WorkbenchPage.Data => "运行数据",
        _ => "事件记录",
    };
    public string PageDescription => Page switch
    {
        WorkbenchPage.Measurement => "配置扫描，观察读数，检查测量结果。",
        WorkbenchPage.Instruments => "查看已接入的设备与最近一次状态回报。",
        WorkbenchPage.Data => "查看当前会话最近一次运行的数据与固定配置。",
        _ => "查看连接、采集与停止过程中的事件。",
    };
    public string ConnectionText => IsConnecting ? "正在连接" : Connected ? "已连接" : "未连接";
    public string SafetyText => !HasSnapshot ? "输出状态未知" : OutputEnabled ? "输出已启用" : "已回报输出关闭";
    public string StatusText => RunState switch
    {
        OperatorRunState.Starting => "准备测量",
        OperatorRunState.Running => "正在采集",
        OperatorRunState.Stopping => "正在停止 · 等待关断回报",
        OperatorRunState.Completed => "测量完成",
        OperatorRunState.Aborted => "测量已停止",
        OperatorRunState.Faulted => "运行异常",
        _ => "准备就绪",
    };
    public string CompletedSummary => $"{CompletedPoints} / {plannedPoints}";
    public double ProgressPercent => CompletedPoints * 100.0 / plannedPoints;
    public string PointCountText => $"{CompletedPoints} 个采集点";
    public string LastEvent => EventLog.Count > 0 ? EventLog[0] : "工作台就绪";
    public string ValidationError => TryGetRecipe(out _, out var error) ? "" : error;
    public bool HasValidationError => ValidationError.Length > 0;
    public string PlanSummary => TryGetRecipe(out var recipe, out _)
        ? $"{recipe.Points} 点  ·  {recipe.StartVoltage:0.###} → {recipe.StopVoltage:0.###} V  ·  间隔 {recipe.DwellSeconds:0.###} s"
        : "请检查扫描参数";

    partial void OnPageChanged(WorkbenchPage value)
    {
        OnPropertyChanged(nameof(IsMeasurementPage));
        OnPropertyChanged(nameof(IsInstrumentsPage));
        OnPropertyChanged(nameof(IsDataPage));
        OnPropertyChanged(nameof(IsEventsPage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
    }
    partial void OnRunStateChanged(OperatorRunState value)
    {
        OnPropertyChanged(nameof(StatusText));
        AbortRunCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) => NotifyCommandState();
    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionText));
        NotifyCommandState();
    }
    partial void OnConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionText));
        NotifyCommandState();
    }
    partial void OnOutputEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyText));
        StartRunCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasSnapshotChanged(bool value) => OnPropertyChanged(nameof(SafetyText));
    partial void OnOperationErrorChanged(string value) => OnPropertyChanged(nameof(HasOperationError));
    partial void OnCompletedPointsChanged(int value)
    {
        OnPropertyChanged(nameof(CompletedSummary));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(PointCountText));
        OnPropertyChanged(nameof(HasPoints));
    }
    partial void OnStartVoltageTextChanged(string value) => NotifyRecipeChanged();
    partial void OnStopVoltageTextChanged(string value) => NotifyRecipeChanged();
    partial void OnSweepPointsTextChanged(string value) => NotifyRecipeChanged();
    partial void OnDwellSecondsTextChanged(string value) => NotifyRecipeChanged();
    partial void OnComplianceTextChanged(string value) => NotifyRecipeChanged();

    private void NotifyRecipeChanged()
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(PlanSummary));
        StartRunCommand.NotifyCanExecuteChanged();
    }
    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanEdit));
        StartRunCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        AbortRunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void OpenMeasurement() => Page = WorkbenchPage.Measurement;
    [RelayCommand] private void OpenInstruments() => Page = WorkbenchPage.Instruments;
    [RelayCommand] private void OpenData() => Page = WorkbenchPage.Data;
    [RelayCommand] private void OpenEvents() => Page = WorkbenchPage.Events;
    [RelayCommand] private void ToggleTable() => ShowTable = !ShowTable;

    private bool CanConnect() => !closing && CanEdit && !Connected;
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsConnecting = true;
        OperationError = "";
        try { ApplySnapshot(await instrumentManager.ConnectAsync(lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { OperationError = $"连接失败：{ex.Message}"; }
        finally { IsConnecting = false; }
    }

    private bool CanStartRun() => !closing && CanEdit && !OutputEnabled && TryGetRecipe(out _, out _);
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync()
    {
        if (!CanStartRun() || !TryGetRecipe(out var recipe, out _)) return;
        IsBusy = true;
        OperationError = "";
        plannedPoints = recipe.Points;
        PlotPoints.Clear();
        RecentPoints.Clear();
        CompletedPoints = 0;
        OnPropertyChanged(nameof(CompletedSummary));
        RunState = OperatorRunState.Starting;
        Elapsed = "00:00.00";
        LatestCurrent = "—";
        LatestVoltage = "—";
        RunSample = string.IsNullOrWhiteSpace(SampleName) ? "未命名样品" : SampleName.Trim();
        RunRecipeSummary = $"{recipe.StartVoltage:0.###} → {recipe.StopVoltage:0.###} V · {recipe.Points} 点 · {recipe.DwellSeconds:0.###} s · 限流 {recipe.ComplianceAmps * 1e6:0.###} µA";
        try { await runService.StartIvSweepAsync(recipe, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RunState = OperatorRunState.Faulted;
            OperationError = $"测量失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanAbortRun() => IsBusy && RunState != OperatorRunState.Stopping;
    [RelayCommand(CanExecute = nameof(CanAbortRun))]
    private void AbortRun()
    {
        RunState = OperatorRunState.Stopping;
        runService.Abort();
        AppendEvent($"{DateTimeOffset.Now:HH:mm:ss}  操作员请求停止采集；等待设备关断回报。");
    }

    private bool TryGetRecipe(out IvSweepRecipe recipe, out string error) => SweepConfiguration.TryCreate(
        StartVoltageText, StopVoltageText, SweepPointsText, DwellSecondsText, ComplianceText, out recipe, out error);

    private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken))
                Dispatcher.UIThread.Post(() => { if (!disposed) ApplyNotification(notification); });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
    private void ApplyNotification(WorkbenchNotification notification)
    {
        switch (notification.Kind)
        {
            case WorkbenchNotificationKind.InstrumentStateChanged when notification.Snapshot is { } snapshot:
                ApplySnapshot(snapshot);
                break;
            case WorkbenchNotificationKind.MeasurementPointReceived when notification.Point is { } point:
                PlotPoints.Add(point);
                CompletedPoints = PlotPoints.Count;
                RecentPoints.Insert(0, MeasurementPointRow.FromPoint(CompletedPoints, point));
                LatestVoltage = $"{point.SourceValue:0.000}";
                LatestCurrent = $"{point.MeasuredValue * 1e3:0.0000}";
                Elapsed = point.Elapsed.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
                if (RunState != OperatorRunState.Stopping) RunState = OperatorRunState.Running;
                break;
            case WorkbenchNotificationKind.RunStarted:
                RunId = notification.RunId ?? "—";
                if (RunState != OperatorRunState.Stopping) RunState = OperatorRunState.Running;
                break;
            case WorkbenchNotificationKind.RunCompleted: RunState = OperatorRunState.Completed; break;
            case WorkbenchNotificationKind.RunAborted: RunState = OperatorRunState.Aborted; break;
            case WorkbenchNotificationKind.RunFaulted: RunState = OperatorRunState.Faulted; break;
        }
        AppendEvent($"{notification.OccurredAt:HH:mm:ss}  {notification.Message}");
    }
    private void AppendEvent(string message)
    {
        EventLog.Insert(0, message);
        while (EventLog.Count > 200) EventLog.RemoveAt(EventLog.Count - 1);
        OnPropertyChanged(nameof(LastEvent));
    }
    private void ApplySnapshot(SourceMeterSnapshot snapshot)
    {
        Identifier = snapshot.Identifier;
        Lifecycle = snapshot.Lifecycle.ToString();
        Connected = snapshot.Connected;
        HasSnapshot = true;
        OutputEnabled = snapshot.OutputEnabled;
        ReadbackVoltage = $"{snapshot.Source.Level:0.000} V";
        ReadbackCompliance = $"{snapshot.Source.Compliance * 1e6:0.###} µA";
        LastUpdate = snapshot.LastHeartbeat == DateTimeOffset.MinValue ? "—" : snapshot.LastHeartbeat.ToString("HH:mm:ss.fff");
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        closing = true;
        NotifyCommandState();
        runService.Abort();
        var runTask = StartRunCommand.ExecutionTask;
        var connectTask = ConnectCommand.ExecutionTask;
        if (runTask is not null) await runTask;
        if (connectTask is not null) await connectTask;
        disposed = true;
        lifetime.Cancel();
        await notificationListener;
        lifetime.Dispose();
    }
}

public sealed record MeasurementPointRow(string Index, string Time, string Source, string Measured)
{
    public static MeasurementPointRow FromPoint(int index, MeasurementPoint point) => new(
        index.ToString("0000", CultureInfo.InvariantCulture),
        point.Elapsed.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture),
        $"{point.SourceValue:0.000000}", $"{point.MeasuredValue:0.000000E+0}");
}
