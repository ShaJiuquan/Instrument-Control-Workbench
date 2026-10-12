using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.App.ViewModels;

public enum WorkbenchPage { Measurement, Instruments, Data, Events, Plans, History }
public enum OperatorRunState { Ready, Starting, Running, Stopping, Completed, Aborted, Faulted, Pausing, Paused, Recovering }

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly NotificationSubscription displayNotifications;
    public const int MaximumPlotPoints = 4096;
    public const int MaximumRecentPoints = 200;
    private readonly InstrumentManager? instrumentManager;
    private readonly IAsyncDisposable? storage;
    private readonly MeasurementRunService runService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task notificationListener;
    private bool disposed;
    private bool closing;
    public bool NotificationObservationLost { get; private set; }
    public string LiveDisplaySummary => $"曲线显示最近 {PlotPoints.Count} 点 · 表格最近 {RecentPoints.Count} 点 · 已省略 {displayNotifications.OmittedDisplayFacts} 条实时更新";
    private int plannedPoints = 21;

    public MainWindowViewModel(InstrumentManager? instrumentManager, MeasurementRunService runService, NotificationHub notifications,
        ExperimentPlanWorkspace? workspace = null, ExperimentPlan? initialPlan = null, string? planPath = null,
        RunHistoryService? history = null, string? exportDirectory = null,
        DeviceRegistry? registry = null, DeviceSettingsViewModel? settings = null, IAsyncDisposable? storage = null)
    {
        this.instrumentManager = instrumentManager;
        this.runService = runService;
        displayNotifications = notifications.SubscribeDisplay();
        InstrumentId = instrumentManager?.InstrumentId.Value ?? "未选择源表";
        this.storage = storage ?? history; Settings = settings;
        if (registry is not null) Devices = new(registry, () => !closing && !NotificationObservationLost && !IsBusy && !IsConnecting && !runService.HasActiveRun && Plans is not { IsDocumentBusy: true }, NotifyCommandState);
        if (instrumentManager is null) page = WorkbenchPage.Plans;
        if (history is not null && exportDirectory is not null) History = new(history, exportDirectory);
        if (workspace is not null && initialPlan is not null && planPath is not null)
            Plans = new(workspace, initialPlan, planPath, () => CanEdit, NotifyCommandState);
        EventLog.Add(instrumentManager is null ? "工作台已就绪。未选择源表，可使用已绑定的实验计划。" : $"工作台已就绪。主源表：{instrumentManager.Descriptor.DisplayName} · {instrumentManager.Descriptor.DriverKind}。");
        notificationListener = ListenForNotificationsAsync(lifetime.Token);
    }

    public DeviceOperatorViewModel? Devices { get; }
    public DeviceSettingsViewModel? Settings { get; }
    public bool HasDevices => Devices is not null;
    public bool HasSettings => Settings is not null;
    public bool HasPrimarySource => instrumentManager is not null;
    public string PrimarySourceName => instrumentManager?.Descriptor.DisplayName ?? "未选择源表";
    public string PrimarySourceKind => instrumentManager?.Descriptor.DriverKind.ToString() ?? "计划按设备绑定执行";
    public string RuntimeKind => Devices is null ? "工作台" : $"{Devices.Devices.Count} 台已启用设备";
    public PagedRunHistoryViewModel? History { get; }
    public bool HasHistory => History is not null;
    public bool IsHistoryPage => Page == WorkbenchPage.History;
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

    public bool IsMeasurementPage => Page == WorkbenchPage.Measurement && HasPrimarySource;
    public bool IsInstrumentsPage => Page == WorkbenchPage.Instruments;
    public bool IsDataPage => Page == WorkbenchPage.Data;
    public bool IsEventsPage => Page == WorkbenchPage.Events;
    public bool CanEdit => !closing && !NotificationObservationLost && !IsBusy && !IsConnecting && !runService.HasActiveRun && Plans is not { IsDocumentBusy: true } && Devices is not { IsBusy: true };
    public bool HasPoints => CompletedPoints > 0;
    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);
    public string PageTitle => Page switch
    {
        WorkbenchPage.Measurement => "测量工作台",
        WorkbenchPage.Instruments => "仪器与连接",
        WorkbenchPage.Data => "运行数据",
        WorkbenchPage.Plans => "实验计划",
        WorkbenchPage.History => "运行历史",
        _ => "事件记录",
    };
    public string PageDescription => Page switch
    {
        WorkbenchPage.Measurement => "配置扫描，观察读数，检查测量结果。",
        WorkbenchPage.Instruments => "查看已接入的设备与最近一次状态回报。",
        WorkbenchPage.Data => "查看当前会话最近一次运行的数据与固定配置。",
        WorkbenchPage.Plans => "编辑并验证计划，执行多设备扫描，观察步骤与数据。",
        WorkbenchPage.History => "检查已保存的运行与部分数据，导出带单位和设备快照的结果。",
        _ => "查看连接、采集与停止过程中的事件。",
    };
    public string ConnectionText => NotificationObservationLost ? "连接显示已过期" : !HasPrimarySource ? "未选择主源表" : IsConnecting ? "正在连接" : Connected ? "已连接" : "未连接";
    public string SafetyText => NotificationObservationLost ? "状态显示已过期 · 需重新确认" : NeedsSafetyRecovery ? "停止状态未确认 · 资源保留" : !HasPrimarySource ? "停止回报见设备页" : !HasSnapshot ? "输出状态未知" : OutputEnabled ? "输出已启用" : "已回报输出关闭";
    public string StatusText => NotificationObservationLost ? "状态显示已过期" : RunState switch
    {
        OperatorRunState.Starting => "准备测量",
        OperatorRunState.Running => "正在采集",
        OperatorRunState.Pausing => "正在暂停 · 等待停止回报",
        OperatorRunState.Paused => "已暂停 · 资源保留",
        OperatorRunState.Recovering => "正在确认设备停止状态",
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
        OnPropertyChanged(nameof(IsPlanPage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsIvDataPage)); OnPropertyChanged(nameof(IsPlanDataPage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
    }
    partial void OnRunStateChanged(OperatorRunState value)
    {
        OnPropertyChanged(nameof(StatusText));
        AbortRunCommand.NotifyCanExecuteChanged();
        NotifyPlanCommands();
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
        NotifyPlanCommands();
        Devices?.RefreshCommands();
    }

    [RelayCommand] private void OpenMeasurement() => Page = WorkbenchPage.Measurement;
    [RelayCommand] private void OpenInstruments() => Page = WorkbenchPage.Instruments;
    [RelayCommand] private void OpenData() => Page = WorkbenchPage.Data;
    [RelayCommand] private void OpenEvents() => Page = WorkbenchPage.Events;
    [RelayCommand]
    private async Task OpenHistoryAsync()
    {
        Page = WorkbenchPage.History;
        if (History is not null && History.RefreshCommand.CanExecute(null)) await History.RefreshCommand.ExecuteAsync(null);
    }
    [RelayCommand] private void ToggleTable() => ShowTable = !ShowTable;

    private bool CanConnect() => HasPrimarySource && !closing && CanEdit && !Connected;
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsConnecting = true;
        OperationError = "";
        try { ApplySnapshot(await instrumentManager!.ConnectAsync(lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { OperationError = $"连接失败：{ex.Message}"; }
        finally { IsConnecting = false; }
    }

    private bool CanStartRun() => HasPrimarySource && !closing && CanEdit && !OutputEnabled && TryGetRecipe(out _, out _);
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync()
    {
        if (!CanStartRun() || !TryGetRecipe(out var recipe, out _)) return;
        IsBusy = true;
        PauseRequested = false;
        IsPlanRun = false;
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
        try { await runService.StartIvSweepAsync(recipe, lifetime.Token, new RunMetadata(sampleName: RunSample)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RunState = OperatorRunState.Faulted;
            OperationError = $"测量失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanAbortRun() => IsBusy && RunState != OperatorRunState.Stopping &&
        runService.Phase is RunExecutionPhase.Starting or RunExecutionPhase.Running or RunExecutionPhase.Paused;
    [RelayCommand(CanExecute = nameof(CanAbortRun))]
    private void AbortRun()
    {
        if (!CanAbortRun()) { NotifyCommandState(); return; }
        RunState = OperatorRunState.Stopping;
        runService.Abort();
        AppendEvent($"{DateTimeOffset.Now:HH:mm:ss}  操作员请求停止采集；等待设备停止状态回报。");
    }

    private bool TryGetRecipe(out IvSweepRecipe recipe, out string error) => SweepConfiguration.TryCreate(
        StartVoltageText, StopVoltageText, SweepPointsText, DwellSecondsText, ComplianceText, out recipe, out error);

    private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in displayNotifications.Reader.ReadAllAsync(cancellationToken))
                await Dispatcher.UIThread.InvokeAsync(() => { if (!disposed) ApplyFact(notification); }, DispatcherPriority.Normal, cancellationToken).GetTask();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (NotificationLagException ex)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyObservationFailure(ex), DispatcherPriority.Normal, cancellationToken).GetTask();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
    public void ApplyObservationFailure(NotificationLagException failure)
    {
        if (disposed) return;
        NotificationObservationLost = true;
        OperationError = $"状态通知积压，显示已停止更新：{failure.Capacity} 条容量已用尽。请停止运行后重新打开工作台。";
        AppendEvent(OperationError);
        OnPropertyChanged(nameof(NotificationObservationLost));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectionText));
        NotifyCommandState();
    }

    public void ApplyFact(WorkbenchNotification notification)
    {
        if (disposed || NotificationObservationLost) return;
        switch (notification.Kind)
        {
            case WorkbenchNotificationKind.InstrumentStateChanged when notification.InstrumentId == instrumentManager?.InstrumentId && notification.Snapshot is { } snapshot:
                ApplySnapshot(snapshot);
                break;
            case WorkbenchNotificationKind.MeasurementPointReceived when notification.Point is { } point:
                PlotPoints.Add(point);
                while (PlotPoints.Count > MaximumPlotPoints) PlotPoints.RemoveAt(0);
                CompletedPoints = notification.AcquiredRowCount ?? CompletedPoints + 1;
                RecentPoints.Insert(0, MeasurementPointRow.FromPoint(CompletedPoints, point));
                while (RecentPoints.Count > MaximumRecentPoints) RecentPoints.RemoveAt(RecentPoints.Count - 1);
                LatestVoltage = $"{point.SourceValue:0.000}";
                LatestCurrent = $"{point.MeasuredValue * 1e3:0.0000}";
                Elapsed = point.Elapsed.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
                if (RunState is not (OperatorRunState.Stopping or OperatorRunState.Pausing or OperatorRunState.Paused)) RunState = OperatorRunState.Running;
                break;
            case WorkbenchNotificationKind.RunStarted:
                RunId = notification.RunId ?? "—";
                if (RunState is not (OperatorRunState.Stopping or OperatorRunState.Pausing or OperatorRunState.Paused)) RunState = OperatorRunState.Running;
                break;
            case WorkbenchNotificationKind.RunCompleted: RunState = OperatorRunState.Completed; break;
            case WorkbenchNotificationKind.RunAborted: RunState = OperatorRunState.Aborted; break;
            case WorkbenchNotificationKind.RunFaulted: RunState = OperatorRunState.Faulted; break;
        }
        if (!IsPlanRun && notification.Kind is (WorkbenchNotificationKind.RunCompleted or WorkbenchNotificationKind.RunAborted or WorkbenchNotificationKind.RunFaulted) && notification.AcquiredRowCount is { } total) CompletedPoints = total;
        OnPropertyChanged(nameof(LiveDisplaySummary));
        Devices?.ApplyFact(notification);
        if (IsPlanRun) Plans?.ApplyFact(notification);
        if (notification.Kind == WorkbenchNotificationKind.RunPaused) RunState = OperatorRunState.Paused;
        if (notification.Kind == WorkbenchNotificationKind.RunResumed) { RunState = OperatorRunState.Running; PauseRequested = false; }
        NotifyCommandState();
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
        HasSnapshot = snapshot.Connected || instrumentManager?.Descriptor.DriverKind == InstrumentControlWorkbench.Core.Devices.DriverKind.Simulation;
        OutputEnabled = snapshot.OutputEnabled;
        ReadbackVoltage = HasSnapshot ? $"{snapshot.Source.Level:0.000} {snapshot.Source.LevelUnit}" : "未连接 · 未读取";
        ReadbackCompliance = !HasSnapshot ? "未连接 · 未读取" : snapshot.Source.Function == SourceFunction.Voltage ? $"{snapshot.Source.Compliance * 1e6:0.###} µA" : $"{snapshot.Source.Compliance:0.###} V";
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
        var planTask = StartPlanCommand.ExecutionTask;
        var recoveryTask = RecoverSafetyCommand.ExecutionTask;
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (runTask is not null) await runTask.WaitAsync(shutdown.Token);
            if (connectTask is not null) await connectTask.WaitAsync(shutdown.Token);
            if (planTask is not null) await planTask.WaitAsync(shutdown.Token);
            if (recoveryTask is not null) await recoveryTask.WaitAsync(shutdown.Token);
            await runService.WaitForFinalizationAsync(shutdown.Token);
            if (NeedsSafetyRecovery) await runService.RecoverSafetyAsync(shutdown.Token, reconnect: true);
            if (Devices is not null) await Devices.DisconnectForCloseAsync(shutdown.Token);
        }
        catch (Exception ex)
        {
            closing = false;
            OperationError = $"关闭未完成，设备停止状态尚未确认：{ex.Message}";
            NotifyCommandState();
            throw;
        }
        if (Plans is not null) await Plans.DisposeAsync();
        if (History is not null) await History.DisposeAsync();
        if (Settings is not null) await Settings.DisposeAsync();
        if (Devices is not null) await Devices.DisposeAsync();
        if (storage is not null) await storage.DisposeAsync();
        disposed = true;
        lifetime.Cancel();
        displayNotifications.Dispose();
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
