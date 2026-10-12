using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquireSr830RowAsync(ReadSr830FrameStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var observed = await run.Resources!.ReadSr830FrameAsync(step.Device, step.Input, run.Token, step.IncludeReference);
        var acquired = step.CaptureValues(observed);
        var values = new Dictionary<ParameterId, Quantity>(run.Axes) { [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second) };
        foreach (var value in acquired) { values.Add(value.Key, value.Value); run.SetVariable(value.Key, value.Value, step.Id); }
        await CommitRowAsync(new(observed.Frame.CapturedAt, values, quality: step.Channels.Take(4).Select(channel => new AcquisitionQuality(step.Id, step.Device, channel.Column, observed.Frame.CapturedAt, null))), step.Id, run, started);
    }
}
