using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

// One provenance boundary for live datasets, archive writers and streaming readers.
public sealed class RunDataRowValidator
{
    private readonly ExperimentPlan? plan;
    private readonly DataSetSchema schema;
    private readonly SampleAnalysisRowValidator analysis;
    private readonly Dictionary<StepId, MeasurementGroupStep> groups = [];
    private readonly Dictionary<ParameterId, EnvironmentRead> environment = [];
    private readonly Dictionary<ParameterId, IReadOnlyList<EnvironmentRead>> environmentPolicies = [];
    private readonly Dictionary<StepId, IReadOnlyDictionary<ParameterId, InstrumentId>> acquisitions = [];
    private readonly HashSet<(StepId Step, ParameterId Column)> nativeResistanceColumns = [];
    private readonly HashSet<(StepId Step, ParameterId Column)> standaloneVoltageColumns = [];
    private readonly Dictionary<StepId, ReadCurrentResistanceStep> independentResistance = [];
    private readonly Dictionary<StepId, Core.Routing.ResolvedSampleRoute> independentSamples = [];
    public bool HasRecoverableAcquisitions => independentResistance.Values.Any(s => s.Retry is { ContinueOnAcquisitionFailure: true });
    public bool HasIndependentResistance => independentResistance.Count != 0;
    private readonly Queue<MeasurementDataRow> recent = new();
    private int count;
    public bool HasMeasurementGroups => groups.Count != 0;
    public bool HasEnvironment => environment.Count != 0;
    public RunDataRowValidator(ExperimentPlan? plan, DataSetSchema schema)
    {
        this.plan = plan; this.schema = schema; analysis = new(plan, schema);
        if (plan is null) return;
        var pending = new Stack<(ExperimentStep Step, int Depth)>(); pending.Push((plan.Root, 0)); var visited = 0;
        while (pending.TryPop(out var item))
        {
            if (++visited > 10_000 || item.Depth > 128) throw new ArgumentException("Measurement provenance plan exceeds bounds.");
            switch (item.Step)
            {
                case ReadStep read:
                    foreach (var channel in read.Channels) if (channel.Address.Parameter == Keithley2182Parameters.Voltage.Id) standaloneVoltageColumns.Add((read.Id, channel.Column));
                    foreach (var channel in read.Channels) if (channel.Address.Parameter == Keithley2450Parameters.MeasuredResistance.Id) nativeResistanceColumns.Add((read.Id, channel.Column));
                    RegisterQuality(read.Id, read.Channels.Select(c => (c.Column, c.Address.Device))); RegisterEnvironment(read.Channels.Select(c => c.Column), read.EnvironmentReads); break;
                case ReadSourceSenseStep read: RegisterQuality(read.Id, new[] { read.VoltageColumn, read.CurrentColumn }.Select(c => (c, read.Device))); RegisterEnvironment([read.VoltageColumn, read.CurrentColumn], read.EnvironmentReads); break;
                case ReadCurrentResistanceStep read:
                    independentResistance.Add(read.Id, read);
                    standaloneVoltageColumns.Add((read.Id, read.VoltageColumn));
                    RegisterQuality(read.Id, [(read.CurrentColumn, read.Source), (read.VoltageColumn, read.Meter)]);
                    RegisterEnvironment([read.CurrentColumn, read.VoltageColumn, read.ResistanceColumn], read.EnvironmentReads); break;
                case ReadResistanceStep read: RegisterQuality(read.Id, new[] { read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn }.Select(c => (c, read.Device))); RegisterEnvironment([read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn], read.EnvironmentReads); break;
                case ReadSampleResistanceStep read:
                    if (read.Acquisition is ReadCurrentResistanceStep sampleCurrent)
                    {
                        independentResistance.Add(sampleCurrent.Id, sampleCurrent);
                        independentSamples.Add(sampleCurrent.Id, (plan.Routing ?? throw new ArgumentException("Independent samples require frozen routing.")).Resolve(read.Binding));
                        standaloneVoltageColumns.Add((sampleCurrent.Id, sampleCurrent.VoltageColumn));
                        RegisterQuality(sampleCurrent.Id, [(sampleCurrent.CurrentColumn, sampleCurrent.Source), (sampleCurrent.VoltageColumn, sampleCurrent.Meter)]);
                    }
                    else RegisterQuality(read.Acquisition.Id, new[] { read.Acquisition.VoltageColumn, read.Acquisition.CurrentColumn, read.Acquisition.ResistanceColumn }.Select(c => (c, read.Acquisition.Device)));
                    RegisterEnvironment([read.Acquisition.VoltageColumn, read.Acquisition.CurrentColumn, read.Acquisition.ResistanceColumn], read.Acquisition.EnvironmentReads); break;
                case SampleAnalysisStep sample: foreach (var read in sample.Readings) pending.Push((read, item.Depth + 1)); break;
                case MeasurementGroupStep group:
                    if (!groups.TryAdd(group.Id, group)) throw new ArgumentException("Duplicate measurement group ID.");
                    foreach (var column in group.Members.SelectMany(m => m.OutputColumns))
                        if (!schema.Columns.Any(c => c.Parameter.Id == column)) throw new ArgumentException("Measurement group requires its declared result columns.");
                    foreach (var member in group.Members) pending.Push((member.Body, item.Depth + 1)); break;
                case FallbackStep fallback: foreach (var child in fallback.Children) pending.Push((child, item.Depth + 1)); break;
                case RetryStep retry: pending.Push((retry.Body, item.Depth + 1)); break;
                case ResultDecoratorStep decorator: pending.Push((decorator.Body, item.Depth + 1)); break;
                case SequenceStep sequence: foreach (var child in sequence.Children) pending.Push((child, item.Depth + 1)); break;
                case ScanStep scan: pending.Push((scan.Body, item.Depth + 1)); break;
                case ForEachStep loop: pending.Push((loop.Body, item.Depth + 1)); break;
                case RepeatStep repeat: pending.Push((repeat.Body, item.Depth + 1)); break;
                case IfElseStep choice: pending.Push((choice.Then, item.Depth + 1)); if (choice.Else is { } other) pending.Push((other, item.Depth + 1)); break;
            }
        }
        foreach (var read in environment.Values)
        {
            var column = schema.Columns.SingleOrDefault(c => c.Parameter.Id == read.Column);
            if (column is null || !column.Optional || column.Source != read.Address || column.Parameter.Unit != EnvironmentRead.Unit(read.Role))
                throw new ArgumentException("Environment columns require their optional matching units and frozen source address.");
        }
        void RegisterQuality(StepId step, IEnumerable<(ParameterId Column, InstrumentId Device)> columns)
        {
            if (!acquisitions.TryAdd(step, columns.ToDictionary(c => c.Column, c => c.Device))) throw new ArgumentException("Duplicate acquisition quality step ID.");
        }
        void RegisterEnvironment(IEnumerable<ParameterId> columns, IReadOnlyList<EnvironmentRead> reads)
        {
            foreach (var column in columns)
                if (environmentPolicies.TryGetValue(column, out var previous) && !previous.SequenceEqual(reads))
                    throw new ArgumentException("Shared acquisition columns require consistent environment policies.");
                else environmentPolicies[column] = reads;
            foreach (var read in reads)
                if (environment.TryGetValue(read.Column, out var previous) && previous != read) throw new ArgumentException("Environment columns cannot change roles/sources.");
                else environment[read.Column] = read;
        }
    }
    public void Validate(MeasurementDataRow row)
    {
        schema.ValidateRow(row);
        ValidateEnvironment(row);
        if (row.MeasurementGroup is null && row.Analysis is null)
        {
            if (row.AcquisitionFailures.Count > 1 || row.AcquisitionFailures.Count != 0 && row.IndependentResistance.Count != 0)
                throw new ArgumentException("A raw acquisition has one successful or failed independent outcome.");
            foreach (var failure in row.AcquisitionFailures)
            {
                if (!independentResistance.TryGetValue(failure.Step, out var failedStep)) throw new ArgumentException("Acquisition failure requires its frozen measurement step.");
                failure.Validate(failedStep, row, plan!.CurrentSourcePreparations.Any(p => p.Device == failedStep.Source) ? CurrentSourceModel.Keithley6221 : CurrentSourceModel.Keithley2450);
                var allowed = schema.Columns.Single(c => c.Parameter.Id == failedStep.VoltageColumn).DependsOn.Concat(failedStep.EnvironmentReads.Select(e => e.Column)).ToHashSet();
                if (row.Quality.Count != 0 || row.Values.Keys.Any(c => !allowed.Contains(c))) throw new ArgumentException("Failed acquisition cannot claim other electrical values or successful quality.");
                if (independentSamples.TryGetValue(failure.Step, out var sample) && (row.Routing is null || row.Routing.Binding != sample.Binding || row.Routing.VoltageMeter != failure.Meter || failure.MeterSettings.Channel != 1))
                    throw new ArgumentException("Failed sample requires its frozen route/meter/input1 provenance.");
                if (!independentSamples.ContainsKey(failure.Step) && row.Routing is not null) throw new ArgumentException("Direct failed acquisition cannot claim sample provenance.");
            }
            if (independentResistance.Count != 0 && row.IndependentResistance.Count == 0 && row.AcquisitionFailures.Count == 0 && row.Quality.Count == 0)
                throw new ArgumentException("An electrical acquisition with no values requires explicit failure proof.");
            var hasIndependentColumns = independentResistance.Values.Any(s => row.Values.ContainsKey(s.CurrentColumn) || row.Values.ContainsKey(s.VoltageColumn) || row.Values.ContainsKey(s.ResistanceColumn));
            if (hasIndependentColumns != (row.IndependentResistance.Count == 1) || row.IndependentResistance.Count > 1)
                throw new ArgumentException("Independent resistance rows require exactly their declared acquisition evidence.");
            foreach (var evidence in row.IndependentResistance)
            {
                if (!independentResistance.TryGetValue(evidence.Step, out var step) || evidence.Source != step.Source || evidence.Meter != step.Meter || evidence.CurrentReference != step.CurrentReference ||
                    evidence.MinimumCurrent != step.MinimumCurrent || evidence.VoltageColumn != step.VoltageColumn || evidence.CurrentColumn != step.CurrentColumn || evidence.ResistanceColumn != step.ResistanceColumn ||
                    evidence.SourceConfiguration.Model != (plan!.CurrentSourcePreparations.Any(p => p.Device == step.Source) ? CurrentSourceModel.Keithley6221 : CurrentSourceModel.Keithley2450) ||
                    !Equals(evidence.Route, step.RequiredRoute) || evidence.SourceConfirmedAfter > row.Timestamp ||
                    !row.Values.TryGetValue(step.CurrentColumn, out var current) || current != evidence.Current || !row.Values.TryGetValue(step.VoltageColumn, out var voltage) || voltage != evidence.Voltage ||
                    !row.Values.TryGetValue(step.ResistanceColumn, out var resistance) || resistance != evidence.Resistance)
                    throw new ArgumentException("Independent resistance identity, configuration, times or V/I differs from its plan and values.");
                evidence.Retry?.Validate(step.Retry, evidence.SourceConfirmedBefore);
                var expectedExcitation = step.ExpectedProgrammedCurrent switch
                {
                    null => (Quantity?)null,
                    ConstantValue constant => constant.Value,
                    VariableValue variable when row.Values.TryGetValue(variable.Variable, out var axisValue) => axisValue,
                    _ => throw new ArgumentException("Expected programmed current requires its persisted axis value."),
                };
                if (expectedExcitation is { } requested && (requested.Unit != PhysicalUnit.Ampere || requested.Value != evidence.SourceConfiguration.Level)) throw new ArgumentException("Programmed current differs from its declared constant/axis.");
                var currentQuality = row.Quality.SingleOrDefault(q => q.Column == step.CurrentColumn);
                var voltageQuality = row.Quality.SingleOrDefault(q => q.Column == step.VoltageColumn);
                if (currentQuality is null || voltageQuality is null || currentQuality.CapturedAt != evidence.SourceCapturedAt || voltageQuality.CapturedAt != evidence.VoltageCapturedAt || voltageQuality.MeterConfiguration is null ||
                    step.CurrentReference == CurrentReference.ProgrammedSetpoint && (currentQuality.Status is not null || currentQuality.AcquisitionConfiguration is not null) ||
                    currentQuality.Status is { } status && (!status.SourceReadback || !status.OutputEnabled))
                    throw new ArgumentException("Independent resistance quality must preserve the actual source/meter observation kind and capture times.");
                if (evidence.Retry is { } retry && retry.MeterSettings != voltageQuality.MeterConfiguration) throw new ArgumentException("Retry must retain the unchanged observed meter configuration.");
                if (independentSamples.TryGetValue(evidence.Step, out var sampleContext) &&
                    (row.Routing is null || row.Routing.Binding != sampleContext.Binding || row.Routing.VoltageMeter != step.Meter || voltageQuality.MeterConfiguration.Channel != 1))
                    throw new ArgumentException("Independent sample must preserve its electrode selection, meter identity and wired input 1.");
            }
        }
        if (row.Analysis is null && row.MeasurementGroup is null && row.Quality.Count != 0)
        {
            var step = row.Quality[0].Step;
            if (!acquisitions.TryGetValue(step, out var columns) || row.Quality.Count != columns.Count ||
                row.Quality.Select(q => q.Column).Distinct().Count() != columns.Count ||
                row.Quality.Any(q => q.Step != step || !columns.TryGetValue(q.Column, out var device) || device != q.Device || !row.Values.ContainsKey(q.Column) || q.CapturedAt > row.Timestamp))
                throw new ArgumentException("Acquisition quality must match the declared complete step, devices, columns and capture times.");
            foreach (var quality in row.Quality)
            {
                var passiveVoltage = standaloneVoltageColumns.Contains((quality.Step, quality.Column));
                if (passiveVoltage && (quality.Status is not null || quality.AcquisitionConfiguration is not null) ||
                    quality.MeterConfiguration is { } meter && (!passiveVoltage || Math.Abs(row.Values[quality.Column].Value) > Keithley2182Settings.MaximumInputVoltage(meter.Channel)))
                    throw new ArgumentException("Standalone meter evidence requires its voltage input and cannot claim SMU buffer status.");
                if (quality.AcquisitionConfiguration is { AutorangeRebound: true } && nativeResistanceColumns.Contains((quality.Step, quality.Column)))
                    throw new ArgumentException("Native resistance evidence cannot claim voltage/current autorange rebound.");
            }
        }
        if (row.Routing is { } context)
            (plan?.Routing ?? throw new ArgumentException("Routed rows require the frozen plan catalog.")).ValidateContext(context);
        if (row.MeasurementGroup is not { } summary) { analysis.Validate(row); return; }
        if (!groups.TryGetValue(summary.Step, out var group) || summary.Inputs.Count != group.Members.Count ||
            summary.Inputs[0].FirstInputRow != count + 1 - group.InputCount || recent.Count < group.InputCount)
            throw new ArgumentException("Measurement summary must reference the declared complete immediately preceding group.");
        var inputs = recent.TakeLast(group.InputCount).ToArray(); var offset = 0;
        if (inputs.Any(r => r.MeasurementGroup is not null)) throw new ArgumentException("Measurement groups cannot summarize another point summary.");
        for (var i = 0; i < group.Members.Count; i++)
        {
            var member = group.Members[i]; var reference = summary.Inputs[i]; var last = inputs[offset + member.InputCount - 1];
            if (reference.Label != group.Labels[i] || reference.InputCount != member.InputCount || reference.FirstInputRow != summary.Inputs[0].FirstInputRow + offset)
                throw new ArgumentException("Measurement member identity or range differs from its plan.");
            if (member.Acquisition is SampleAnalysisStep sample)
            {
                if (last.Analysis?.Step != sample.Id) throw new ArgumentException("Sample group requires its validated analysis result.");
            }
            else if (last.Analysis is not null || member.AcquisitionColumns.Any(c => !last.Values.ContainsKey(c)) && !(last.AcquisitionFailures.Count == 1 && member.Acquisition.Id == last.AcquisitionFailures[0].Step))
                throw new ArgumentException("Measurement member requires its declared acquisition columns.");
            var allowed = member.OutputColumns.ToHashSet(); var dependencies = new Stack<ParameterId>(allowed);
            while (dependencies.TryPop(out var id))
                foreach (var dependency in schema.Columns.Single(c => c.Parameter.Id == id).DependsOn)
                    if (allowed.Add(dependency)) dependencies.Push(dependency);
            if (last.Values.Keys.Any(c => !allowed.Contains(c))) throw new ArgumentException("Measurement input contains another member's columns.");
            offset += member.InputCount;
        }
        var expected = MeasurementGroupSummary.BuildRow(group, summary.Inputs[0].FirstInputRow, inputs);
        if (expected.Timestamp != row.Timestamp || !expected.Environment.SequenceEqual(row.Environment) || !expected.Quality.SequenceEqual(row.Quality) || !expected.IndependentResistance.SequenceEqual(row.IndependentResistance) || !expected.AcquisitionFailures.SequenceEqual(row.AcquisitionFailures) || !expected.Values.Keys.ToHashSet().SetEquals(row.Values.Keys) || expected.Values.Any(p => row.Values[p.Key] != p.Value))
            throw new ArgumentException("Measurement summary values/time differ from the preserved acquisitions.");
    }
    private void ValidateEnvironment(MeasurementDataRow row)
    {
        if (environment.Count == 0 && row.Environment.Count == 0) return;
        var observed = row.Environment.ToDictionary(o => o.Column);
        if (row.Analysis is null && row.MeasurementGroup is null)
        {
            var required = row.Values.Keys.Concat(row.AcquisitionFailures.Select(f => f.VoltageColumn)).Where(environmentPolicies.ContainsKey).SelectMany(c => environmentPolicies[c]).Select(r => r.Column).ToHashSet();
            if (!required.SetEquals(observed.Keys)) throw new ArgumentException("Acquisition environment evidence differs from its declared policy.");
        }
        foreach (var observation in row.Environment)
        {
            if (!environment.TryGetValue(observation.Column, out var expected) || expected.Role != observation.Role || expected.Address != observation.Source)
                throw new ArgumentException("Environment role/source differs from the frozen plan.");
            var present = row.Values.TryGetValue(observation.Column, out var value);
            if (observation.Value is { } actual ? !present || value != actual : present)
                throw new ArgumentException("Environment values/errors differ from observation evidence.");
        }
        if (row.Values.Keys.Any(c => environment.ContainsKey(c) && !observed.ContainsKey(c))) throw new ArgumentException("Environment quantities require their acquisition evidence.");
    }
    public void Append(MeasurementDataRow row)
    {
        Validate(row); analysis.AppendValidated(row); count++;
        if (groups.Count == 0) return;
        recent.Enqueue(row); if (recent.Count > MeasurementGroupStep.MaximumInputs) recent.Dequeue();
    }
}
