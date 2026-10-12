using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanStepFormTests
{
    [Fact]
    public async Task EditorModeSwitchPreservesTheDraftAndProtectsFormAndJsonMutations()
    {
        var (shell, _) = Create(); await using var owner = shell; var vm = shell.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "delay").SelectCommand.Execute(null); var before = vm.DocumentText;
        Assert.True(vm.IsNodeFormVisible); vm.ToggleNodeEditorCommand.Execute(null); Assert.True(vm.IsNodeJsonVisible); Assert.False(vm.NodeFragmentDirty); Assert.Equal(before, vm.DocumentText);
        vm.ToggleNodeEditorCommand.Execute(null); vm.NodeForm!.Seconds = "2"; vm.ToggleNodeEditorCommand.Execute(null);
        Assert.True(vm.IsNodeJsonVisible); Assert.True(vm.IsNodeReadOnly); Assert.False(shell.StartPlanCommand.CanExecute(null)); vm.DiscardNodeCommand.Execute(null);
        Assert.True(vm.IsNodeFormVisible); Assert.False(vm.NodeFragmentDirty);
        shell.IsBusy = true; vm.RefreshCommands(); vm.ToggleNodeEditorCommand.Execute(null); Assert.True(vm.IsNodeJsonVisible); Assert.True(vm.IsNodeReadOnly);
        vm.IsDocumentBusy = true; var mode = vm.ShowAdvancedNode; vm.ToggleNodeEditorCommand.Execute(null); Assert.Equal(mode, vm.ShowAdvancedNode);
        vm.IsDocumentBusy = false; shell.IsBusy = false;
    }
    [Fact]
    public async Task UnsupportedNodeKeepsJsonAndCannotExposeAnEmptyForm()
    {
        var (shell, _) = Create(); await using var owner = shell; var vm = shell.Plans!;
        Assert.False(vm.SupportsNodeForm); Assert.True(vm.IsNodeJsonVisible); Assert.False(vm.ToggleNodeEditorCommand.CanExecute(null));
        vm.ToggleNodeEditorCommand.Execute(null); Assert.False(vm.ShowAdvancedNode);
    }
    [Fact]
    public void ShortDecimalAndScientificScanTextRoundTripsEveryOriginalBinaryValue()
    {
        double[] values = [.05, .1, 1e-200, double.Epsilon, .9999999999999999];
        var original = new ScanStep(new("precise"), new(new("source"), new("volts")), new("axis"), new(values.Select(v => new Quantity(v, PhysicalUnit.Volt))), TimeSpan.Zero, new SequenceStep(new("body"), []));
        var form = new PlanStepFormViewModel(original, Catalog(), () => { }); var built = (ScanStep)form.Build();
        Assert.Equal(values.Select(BitConverter.DoubleToInt64Bits), built.Values.Values.Select(v => BitConverter.DoubleToInt64Bits(v.Value)));
    }
    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("4294968")]
    [InlineData("1,5")]
    [InlineData("")]
    public async Task InvalidFormTimeLocksRunAndPreservesPlanUntilDiscard(string text)
    {
        var (shell, source) = Create(); await using var owner = shell; var vm = shell.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "delay").SelectCommand.Execute(null); var before = vm.DocumentText; var form = vm.NodeForm!;
        form.Seconds = text; Assert.True(vm.NodeFragmentDirty); Assert.True(vm.IsNodeReadOnly); Assert.False(shell.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null));
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(form.IsDirty);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError); Assert.False(vm.NodeFragmentDirty); Assert.Equal("1", vm.NodeForm!.Seconds); Assert.True(shell.StartPlanCommand.CanExecute(null));
    }
    [Fact]
    public async Task FormApplyAndAdvancedEditingAreExclusiveAndStaleFormsCannotChangeTheNewSelection()
    {
        var (shell, _) = Create(); await using var owner = shell; var vm = shell.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "delay").SelectCommand.Execute(null); var stale = vm.NodeForm!;
        stale.Seconds = "0.02"; vm.ApplyNodeCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(.02), ((DelayStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[0]).Duration);
        stale.Seconds = "20"; Assert.False(vm.NodeFragmentDirty);
        vm.NodeFragment = "{\"id\":\"delay\",\"kind\":\"delay\",\"seconds\":0.03}"; Assert.True(vm.NodeForm!.IsReadOnly); vm.ApplyNodeCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(.03), ((DelayStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[0]).Duration);
        var before = vm.DocumentText; vm.NodeForm!.Seconds = "2"; vm.NodeFragment = "{\"id\":\"delay\",\"kind\":\"delay\",\"seconds\":3}";
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        Assert.False(vm.NodeFragmentDirty); Assert.False(vm.HasError);
    }
    [Fact]
    public void ChangingAddressClearsNumericValuesAndUsesTheAuthoritativeUnitRangeAndAccess()
    {
        var devices = Catalog(); var step = new SetParameterStep(new("set"), new(new("source"), new("volts")), new ConstantValue(new(.2, PhysicalUnit.Volt)));
        var form = new PlanStepFormViewModel(step, devices, () => { });
        Assert.DoesNotContain(form.Address.Parameters, p => p.Descriptor.Id.Value == "readback");
        form.Address.SelectedParameter = form.Address.Parameters.Single(p => p.Descriptor.Id.Value == "amps"); Assert.Equal("", form.ValueText);
        Assert.Throws<FormatException>(() => form.Build()); form.ValueText = "0.0001";
        Assert.Equal(PhysicalUnit.Ampere, ((ConstantValue)((SetParameterStep)form.Build()).Value).Value.Unit);
        form.ValueText = "2"; Assert.Throws<ArgumentOutOfRangeException>(() => form.Build());
        form.Address.SelectedParameter = new(new(new("readback"), "forged writable", PhysicalUnit.Volt, ParameterAccess.Write));
        Assert.Throws<InvalidOperationException>(() => form.Build());
        form.Address.SelectedParameter = new(new(new("volts"), "forged unit", PhysicalUnit.Ampere, ParameterAccess.Write));
        form.ValueText = "0.2"; Assert.Contains("Volt", form.Address.Constraint); Assert.Equal(PhysicalUnit.Volt, ((ConstantValue)((SetParameterStep)form.Build()).Value).Value.Unit);
        form.Address.SelectedDevice = form.Address.Devices.Single(d => d.Descriptor.Id.Value == "other"); Assert.Equal("", form.ValueText); Assert.Null(form.Address.SelectedParameter);
    }
    [Theory]
    [InlineData("repeat")]
    [InlineData("timeout")]
    [InlineData("scan")]
    [InlineData("retry")]
    public void ContainerFormsPreserveBodiesAndDoNotConvertUnitsOrExecutionTiming(string kind)
    {
        var body = new DelayStep(new("body"), TimeSpan.Zero);
        ExperimentStep step = kind switch
        {
            "repeat" => new RepeatStep(new("container"), 2, body),
            "timeout" => new TimeoutStep(new("container"), TimeSpan.FromSeconds(3), body),
            "retry" => new RetryStep(new("container"), 2, TimeSpan.FromSeconds(.1), body),
            _ => new ScanStep(new("container"), new(new("source"), new("volts")), new("axis"), new([new(.1, PhysicalUnit.Volt)]), TimeSpan.FromSeconds(.2), body),
        };
        var form = new PlanStepFormViewModel(step, Catalog(), () => { });
        if (kind == "repeat") { form.Cycles = "3"; var built = (RepeatStep)form.Build(); Assert.Same(body, built.Body); Assert.Equal(3, built.Cycles); form.Cycles = "1.5"; Assert.Throws<FormatException>(() => form.Build()); }
        else if (kind == "timeout") { form.Seconds = "4"; var built = (TimeoutStep)form.Build(); Assert.Same(body, built.Body); Assert.Equal(TimeSpan.FromSeconds(4), built.Timeout); }
        else if (kind == "retry") { form.Cycles = "3"; form.Seconds = "0.2"; var built = (RetryStep)form.Build(); Assert.Same(body, built.Body); Assert.Equal(3, built.MaxAttempts); Assert.Equal(TimeSpan.FromSeconds(.2), built.Delay); }
        else
        {
            form.ValuesText = "0, 0.5, 1e-1"; form.Dwell = "0.3"; var built = (ScanStep)form.Build(); Assert.Same(body, built.Body);
            Assert.Equal(new[] { 0, .5, .1 }, built.Values.Values.Select(q => q.Value)); Assert.Equal(PhysicalUnit.Volt, built.Values.Unit); Assert.Equal(TimeSpan.FromSeconds(.3), built.Dwell);
            form.Address.SelectedParameter = form.Address.Parameters.Single(p => p.Descriptor.Id.Value == "amps"); Assert.Equal("", form.ValuesText); Assert.Throws<FormatException>(() => form.Build());
        }
    }
    [Fact]
    public void MultiChannelFormPreservesEnvironmentAndGuardsAndRejectsDuplicateColumns()
    {
        var route = new MatrixRouteExpectation(new("matrix"), [new(1, 1, 1)]);
        var step = new ReadStep(new("read"), [new(new("a"), new(new("source"), new("volts"))), new(new("b"), new(new("source"), new("readback")))],
            [new(EnvironmentRole.Temperature, new("temperature"))], route, new("excitation"));
        var form = new PlanStepFormViewModel(step, Catalog(), () => { }); form.Channels[0].Column = "voltage";
        var built = (ReadStep)form.Build(); Assert.Same(route, built.RequiredRoute); Assert.Equal(step.ExcitationSource, built.ExcitationSource); Assert.Equal(step.EnvironmentReads, built.EnvironmentReads); Assert.Equal(2, built.Channels.Count);
        form.Channels[1].Column = "voltage"; Assert.Throws<ArgumentException>(() => form.Build());
        form.Channels[1].Column = "current"; form.IsReadOnly = true; form.AddChannelCommand.Execute(null); form.RemoveChannelCommand.Execute(form.Channels[0]); Assert.Equal(2, form.Channels.Count);
        form.IsReadOnly = false; form.RemoveChannelCommand.Execute(form.Channels[0]); Assert.Single(form.Channels); Assert.False(form.RemoveChannelCommand.CanExecute(form.Channels[0]));
        form.AddChannelCommand.Execute(null); Assert.Equal(2, form.Channels.Count); Assert.ThrowsAny<Exception>(() => form.Build());
    }
    [Fact]
    public async Task DevicePaletteUsesRegisteredParametersAndTypedAppendWithoutConnecting()
    {
        var (shell, source) = Create(); await using var owner = shell; var vm = shell.Plans!; var before = vm.DocumentText;
        vm.PaletteKind = "读取仪器"; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        var form = vm.PaletteForm!; var channel = form.Channels[0]; channel.Column = "voltage_read";
        channel.Address.SelectedDevice = channel.Address.Devices[0]; channel.Address.SelectedParameter = channel.Address.Parameters.Single(p => p.Descriptor.Id.Value == "k2450.source_voltage");
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError); var added = (ReadStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[^1];
        Assert.Equal("voltage_read", added.Channels[0].Column.Value); Assert.NotSame(form, vm.PaletteForm); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        // A newly created palette keeps a new ID and requires fresh explicit selection.
        vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(2, ((SequenceStep)vm.ValidatedPlan!.Root).Children.Count);
        shell.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.PaletteForm!.IsReadOnly); Assert.True(vm.NodeForm!.IsReadOnly);
        var document = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.Equal(document, vm.DocumentText); shell.IsBusy = false;
    }
    [Fact]
    public void DynamicValueNodesKeepTheirExpressionInsteadOfOpeningAConstantForm()
    {
        var step = new SetParameterStep(new("set"), new(new("source"), new("volts")), new VariableValue(new("requested")));
        var form = new PlanStepFormViewModel(step, Catalog(), () => { }); Assert.False(form.IsSupported); Assert.Throws<NotSupportedException>(() => form.Build());
    }
    [Theory]
    [InlineData("tolerance")]
    [InlineData("poll")]
    [InlineData("timeout")]
    [InlineData("stable")]
    public void WaitStableFormRetainsItsVariableTargetAndRejectsInvalidObservationTiming(string damage)
    {
        var target = new VariableValue(new("target"));
        var step = new WaitStableStep(new("stable"), new(new("source"), new("readback")), target, new(.00001, PhysicalUnit.Ampere), TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.05), TimeSpan.FromSeconds(2));
        var form = new PlanStepFormViewModel(step, Catalog(), () => { }); Assert.True(form.IsSupported);
        form.Tolerance = "1e-6"; var built = (WaitStableStep)form.Build(); Assert.Same(target, built.Target); Assert.Equal(PhysicalUnit.Ampere, built.Tolerance.Unit);
        switch (damage) { case "tolerance": form.Tolerance = "-1"; break; case "poll": form.PollSeconds = "0"; break; case "timeout": form.Seconds = "0"; break; default: form.StableSeconds = "3"; break; }
        Assert.ThrowsAny<Exception>(() => form.Build());
    }
    [Theory]
    [InlineData("设置参数")]
    [InlineData("参数扫描")]
    [InlineData("源输出")]
    public async Task InstrumentPaletteRetainsPreparationAndValidatesBeforeAnyConnection(string kind)
    {
        var (shell, source) = Create(); await using var owner = shell; var vm = shell.Plans!; vm.PaletteKind = kind;
        var form = vm.PaletteForm!; form.Address.SelectedDevice = form.Address.Devices[0];
        if (kind != "源输出") form.Address.SelectedParameter = form.Address.Parameters.Single(p => p.Descriptor.Id.Value == "k2450.source_voltage");
        form.ValueText = "0.01"; form.ValuesText = "0, 0.01"; form.Column = "new_axis"; form.OutputEnabled = false;
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Equal(2, ((SequenceStep)vm.ValidatedPlan!.Root).Children.Count); Assert.Single(vm.ValidatedPlan.Preparations);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        var added = ((SequenceStep)vm.ValidatedPlan.Root).Children[^1];
        if (kind == "参数扫描") { var scan = (ScanStep)added; Assert.Equal(PhysicalUnit.Volt, scan.Values.Unit); Assert.Empty(((SequenceStep)scan.Body).Children); }
        else if (kind == "源输出") Assert.False(((SourceOutputStep)added).Enabled);
        else Assert.Equal(.01, ((ConstantValue)((SetParameterStep)added).Value).Value.Value);
    }
    private static IReadOnlyList<DeviceDescriptor> Catalog() => [
        new(new("source"), "test-source", "Test source", DriverKind.Simulation, DeviceStopPolicy.OutputOff, [
            new(new("volts"), "Voltage", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(-1, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt)),
            new(new("amps"), "Current", PhysicalUnit.Ampere, ParameterAccess.Write, new(-.001, PhysicalUnit.Ampere), new(.001, PhysicalUnit.Ampere)),
            new(new("readback"), "Readback", PhysicalUnit.Ampere, ParameterAccess.Read)]),
        new(new("other"), "axis", "Other device", DriverKind.Simulation, DeviceStopPolicy.HoldCurrentPosition, [new(new("angle"), "Angle", PhysicalUnit.Degree, ParameterAccess.ReadWrite)])];
    private static (MainWindowViewModel, MockKeithley2450SourceMeter) Create()
    {
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        var plan = new ExperimentPlan("forms", new SequenceStep(new("root"), [new DelayStep(new("delay"), TimeSpan.FromSeconds(1))]),
            [new(manager.InstrumentId, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.000105, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        return (new(manager, runs, hub, workspace, plan, "unused-forms.json"), source);
    }
}
