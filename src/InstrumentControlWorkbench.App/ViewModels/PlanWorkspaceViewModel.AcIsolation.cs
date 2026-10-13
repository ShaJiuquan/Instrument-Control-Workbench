using System;
using System.Collections.Generic;
using System.Linq;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private IReadOnlyList<PlanSr830AcIsolationReadbackViewModel>? acIsolationReadbacks;
    private string? acObservationRun;
    public IReadOnlyList<PlanSr830AcIsolationReadbackViewModel> Sr830AcIsolationReadbacks => acIsolationReadbacks ??=
        Array.AsReadOnly(workspace.Sr830AcIsolationDefinitions.Select(d => new PlanSr830AcIsolationReadbackViewModel(d)).ToArray());
    public bool HasSr830AcIsolationReadbacks => Sr830AcIsolationReadbacks.Count != 0;

    // Device facts are unscoped observations; no IO, plan edits, or ownership claims occur here.
    public void ApplyAcIsolationFact(WorkbenchNotification fact)
    {
        if (disposed) return;
        if (fact.Kind == WorkbenchNotificationKind.DeviceStateChanged && fact.Device is { } device)
        {
            Sr830AcIsolationReadbacks.SingleOrDefault(r => r.Definition.Device.Id == device.Descriptor.Id)?.Apply(device);
            return;
        }
        if (fact.Kind == WorkbenchNotificationKind.RunStarted && fact.RunId is { } started)
        {
            acObservationRun = started;
            foreach (var item in Sr830AcIsolationReadbacks.Where(r => validated?.Preview.Devices.Contains(r.Definition.Device.Id) == true))
                item.Invalidate(fact.OccurredAt, "等待本次运行的新回读");
        }
        if (fact.RunId is null || fact.RunId != acObservationRun) return;
        if (fact.Kind == WorkbenchNotificationKind.RunFinalizing)
            foreach (var item in Sr830AcIsolationReadbacks.Where(r => validated?.Preview.Devices.Contains(r.Definition.Device.Id) == true))
                item.Invalidate(fact.OccurredAt, "正在确认终止断开");
        if (fact.Kind is WorkbenchNotificationKind.RunFaulted or WorkbenchNotificationKind.RunAborted)
            foreach (var item in Sr830AcIsolationReadbacks.Where(r => validated?.Preview.Devices.Contains(r.Definition.Device.Id) == true))
                item.Invalidate(fact.OccurredAt, "运行已结束；隔离状态需重新回读");
    }
    public void InvalidateAcIsolationObservation()
    {
        foreach (var item in Sr830AcIsolationReadbacks) item.Invalidate(DateTimeOffset.UtcNow, "通知观察已中断；回读显示失效", force: true);
    }
}
