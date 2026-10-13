using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.App.ViewModels;

public enum AcIsolationReadbackState { Unknown, Disconnected, Open, Closed, Mixed }

public partial class PlanSr830AcIsolationReadbackViewModel : ObservableObject
{
    private readonly TimeProvider clock;
    private DateTimeOffset fence;
    public PlanSr830AcIsolationReadbackViewModel(MatrixSwitchDefinition definition, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.AcIsolation is null) throw new ArgumentException("Registered AC isolation is required.", nameof(definition));
        Definition = definition; this.clock = clock ?? TimeProvider.System;
    }
    public MatrixSwitchDefinition Definition { get; }
    public string Label => $"{Definition.Device.DisplayName} · {Definition.Device.Id} ← {Definition.AcIsolation!.Generator}";
    [ObservableProperty] private AcIsolationReadbackState state = AcIsolationReadbackState.Unknown;
    [ObservableProperty] private string stateText = "尚无控制器回读";
    [ObservableProperty] private string observedAt = "—";
    public void Invalidate(DateTimeOffset timestamp, string reason, bool force = false)
    {
        if (!force && timestamp <= fence) return;
        if (timestamp > fence) fence = timestamp;
        State = AcIsolationReadbackState.Unknown; StateText = reason; ObservedAt = "—";
    }
    public void Apply(DeviceStatus device)
    {
        if (device.Descriptor.Id != Definition.Device.Id) return;
        if (device.Timestamp > clock.GetUtcNow() || device.Timestamp == default)
        { State = AcIsolationReadbackState.Unknown; StateText = "回读时间无效"; ObservedAt = "—"; return; }
        if (device.Timestamp <= fence) return;
        fence = device.Timestamp;
        ObservedAt = device.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (device.Matrix is not { } snapshot || snapshot.Definition != Definition || snapshot.Timestamp != device.Timestamp ||
            snapshot.Connected != device.Connected || device.Lifecycle == InstrumentLifecycleState.Faulted)
        { State = AcIsolationReadbackState.Unknown; StateText = "回读缺失、配置不匹配或设备故障"; return; }
        if (!snapshot.Connected) { State = AcIsolationReadbackState.Disconnected; StateText = "控制器未连接；通路状态未确认"; return; }
        if (!snapshot.RouteKnown) { State = AcIsolationReadbackState.Unknown; StateText = "控制器报告通路未知"; return; }
        var selected = Definition.AcIsolation!.Connections;
        var closed = snapshot.Closed.Count(selected.Contains);
        State = closed == 0 ? AcIsolationReadbackState.Open : closed == selected.Count ? AcIsolationReadbackState.Closed : AcIsolationReadbackState.Mixed;
        StateText = State switch
        {
            AcIsolationReadbackState.Open => "声明通路已回报断开",
            AcIsolationReadbackState.Closed => "声明通路已回报连接",
            _ => "声明通路部分连接；隔离未确认",
        };
    }
}
