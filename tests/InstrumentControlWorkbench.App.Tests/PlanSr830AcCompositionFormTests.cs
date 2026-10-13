using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSr830AcCompositionFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculationBodyReceivesRegisteredAcMetadataAndSharesLocks(bool metadata)
    {
        var generator = new DeviceDescriptor(new("generator"), "SR830", "Active", DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Sr830Parameters.WithReference);
        var matrix = new MatrixSwitchDefinition(new(new("isolator"), "MATRIX", "Isolation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 2, 2)], [],
            new(generator.Id, [new(1, 1, 1)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));
        var active = new ReadSr830AcFrameStep(new("frame"), generator.Id, matrix.Device.Id, matrix.AcIsolation!.Connections,
            new(new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt)), Sr830InputMode.VoltageAB, new("raw"), TimeSpan.FromTicks(123));
        var calculation = new CalculatedMeasurementStep(new("calculation"), active, [new(new("x_copy"), new VariableValue(active.Frame.Channels[0].Column))]);
        var form = new PlanStepFormViewModel(calculation, [generator, matrix.Device], () => { }, sr830AcIsolations: metadata ? [matrix] : []);
        var body = form.CalculationForm!.BodyForm; Assert.True(body.HasSr830Ac); Assert.False(form.CalculationForm.HasSr830Body);
        Assert.False(form.CalculationForm.GenerateSr830NominalCommand.CanExecute(null));
        body.Sr830Ac!.Frequency = "19.5"; Assert.True(form.IsDirty);
        if (metadata)
        {
            var result = Assert.IsType<CalculatedMeasurementStep>(form.Build()); var built = Assert.IsType<ReadSr830AcFrameStep>(result.Body);
            Assert.Equal(19.5, built.Excitation.Frequency.Value); Assert.Equal(active.Id, built.Id); Assert.Equal(active.Connections, built.Connections); Assert.Equal(active.Settle, built.Settle);
        }
        else Assert.Throws<InvalidOperationException>(() => form.Build());
        form.IsReadOnly = true; Assert.True(body.Sr830Ac.IsReadOnly);
    }
}
