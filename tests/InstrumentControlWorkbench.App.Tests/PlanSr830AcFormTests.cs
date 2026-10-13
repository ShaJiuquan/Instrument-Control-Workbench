using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSr830AcFormTests
{
    [Theory]
    [InlineData(Sr830InputMode.VoltageA)]
    [InlineData(Sr830InputMode.VoltageAB)]
    [InlineData(Sr830InputMode.Current1M)]
    [InlineData(Sr830InputMode.Current100M)]
    public void UneditedFormPreservesFullCodecPrecisionAndOriginalConnectionOrder(Sr830InputMode input)
    {
        var step = Step(input); var changes = 0; var generator = Generator(); var matrix = Matrix(connections: step.Connections.Reverse());
        var outer = new PlanStepFormViewModel(step, [generator], () => changes++, sr830AcIsolations: [matrix]);
        Assert.True(outer.HasSr830Ac); Assert.True(outer.IsSupported); Assert.False(outer.IsDirty); Assert.Equal(0, changes);
        var rebuilt = Assert.IsType<ReadSr830AcFrameStep>(outer.Build()); Assert.Equal(step.Connections, rebuilt.Connections); Assert.Equal(step.Settle.Ticks, rebuilt.Settle.Ticks);
        var codec = new JsonExperimentPlanDocument(); Assert.Equal(codec.Serialize(new("AC", step)), codec.Serialize(new("AC", rebuilt)));
    }

    [Fact]
    public void TemplateNeedsBothExplicitSelectionsInputAndPrefix()
    {
        var form = Form(template: true); Assert.Null(form.SelectedGenerator); Assert.Null(form.SelectedIsolator); Assert.Null(form.SelectedInput); Assert.Empty(form.Prefix); Assert.Empty(form.Connections);
        Assert.Throws<InvalidOperationException>(() => form.Build()); form.SelectedGenerator = Assert.Single(form.Generators);
        Assert.Null(form.SelectedIsolator); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedIsolator = Assert.Single(form.Isolators); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedInput = Sr830InputMode.VoltageAB; Assert.Throws<ArgumentException>(() => form.Build());
        form.Prefix = "raw_ac"; var built = form.Build(); Assert.Equal(Matrix().AcIsolation!.Connections, built.Connections); Assert.True(form.IsTemplate);
    }

    [Fact]
    public void NullCapabilityMetadataDoesNotInferAuthorityFromDescriptors()
    {
        var outer = new PlanStepFormViewModel(Step(), [Generator()], () => { }); var form = outer.Sr830Ac!;
        Assert.Empty(form.Generators); Assert.Empty(form.Isolators); Assert.Equal(Step().Connections, form.Connections);
        Assert.Throws<InvalidOperationException>(() => outer.Build());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("generator")]
    [InlineData("connections")]
    public void IncompatibleSavedMetadataRetainsValuesAndRefusesBuild(string damage)
    {
        var step = Step(); var matrix = damage switch
        {
            "generator" => Matrix(generator: "other"),
            "connections" => Matrix(connections: [new(1, 2, 2)]),
            _ => Matrix(),
        };
        var form = new PlanSr830AcFormViewModel(step, [Generator()], damage == "missing" ? [] : [matrix], () => { });
        Assert.Equal(step.Connections, form.Connections); Assert.Equal("17.1234567890123", form.Frequency); Assert.Equal("raw_ac", form.Prefix);
        Assert.Equal("0.0123457", form.SettleSeconds); Assert.Contains("保存的", form.ConnectionsSummary);
        Assert.Throws<InvalidOperationException>(() => form.Build());
    }

    [Fact]
    public void ChangingGeneratorNeverResetsOtherInputsOrChoosesItsIsolationPath()
    {
        var first = Generator(); var second = Generator("second"); var firstMatrix = Matrix(); var secondMatrix = Matrix("second-isolator", "second", [new(1, 2, 2)]);
        var form = new PlanSr830AcFormViewModel(Step(), [first, second], [firstMatrix, secondMatrix], () => { });
        var frequency = form.Frequency; var amplitude = form.SineAmplitude; var harmonic = form.Harmonic; var input = form.SelectedInput; var prefix = form.Prefix; var settle = form.SettleSeconds;
        form.SelectedGenerator = form.Generators.Single(choice => choice.Descriptor.Id == second.Id);
        Assert.Same(firstMatrix, form.SelectedIsolator!.Definition); Assert.Throws<InvalidOperationException>(() => form.Build());
        Assert.Equal(frequency, form.Frequency); Assert.Equal(amplitude, form.SineAmplitude); Assert.Equal(harmonic, form.Harmonic); Assert.Equal(input, form.SelectedInput); Assert.Equal(prefix, form.Prefix); Assert.Equal(settle, form.SettleSeconds);
        form.SelectedIsolator = form.Isolators.Single(choice => choice.Definition.Device.Id == secondMatrix.Device.Id);
        var built = form.Build(); Assert.Equal(second.Id, built.Device); Assert.Equal(secondMatrix.Device.Id, built.Isolator); Assert.Equal(secondMatrix.AcIsolation!.Connections, built.Connections);
    }

    [Fact]
    public void BindingResolvesRegisteredMetadataRatherThanCallerSuppliedChoice()
    {
        var form = Form(); form.SelectedIsolator = new(Matrix(connections: [new(1, 2, 2)]));
        Assert.Equal(Matrix().AcIsolation!.Connections, form.Build().Connections);
        form.SelectedIsolator = new(Matrix("unknown")); Assert.Throws<InvalidOperationException>(() => form.Build());
    }

    [Fact]
    public void ReadonlyPropagationIsNotAnEditAndChangesReachOuterDirtyState()
    {
        var outer = new PlanStepFormViewModel(Step(), [Generator()], () => { }, sr830AcIsolations: [Matrix()]);
        outer.IsReadOnly = true; Assert.True(outer.Sr830Ac!.IsReadOnly); Assert.False(outer.IsDirty);
        outer.IsReadOnly = false; Assert.False(outer.Sr830Ac.IsReadOnly); Assert.False(outer.IsDirty);
        outer.Sr830Ac.Frequency = "19"; Assert.True(outer.IsDirty); Assert.Equal(new(19, PhysicalUnit.Hertz), Assert.IsType<ReadSr830AcFrameStep>(outer.Build()).Excitation.Frequency);
    }

    [Theory]
    [InlineData("frequency", "NaN")]
    [InlineData("frequency", "Infinity")]
    [InlineData("amplitude", "")]
    [InlineData("amplitude", "-Infinity")]
    [InlineData("harmonic", "1.5")]
    [InlineData("settle", "NaN")]
    [InlineData("settle", "1,000")]
    [InlineData("settle", "1e-7")]
    [InlineData("settle", "-1")]
    [InlineData("settle", "0.00000001")]
    [InlineData("settle", "0.000000000000000000000000000001")]
    [InlineData("settle", "4294967.295")]
    public void InvalidNumbersAndSubtickSettlingFailBeforeAPlanIsBuilt(string field, string value)
    {
        var form = Form(); Set(form, field, value); Assert.Throws<FormatException>(() => form.Build());
    }

    [Theory]
    [InlineData("frequency", "0")]
    [InlineData("frequency", "102001")]
    [InlineData("amplitude", "0")]
    [InlineData("amplitude", ".0039")]
    [InlineData("amplitude", "5.001")]
    [InlineData("harmonic", "0")]
    [InlineData("harmonic", "20000")]
    [InlineData("harmonic", "6000")]
    [InlineData("prefix", "")]
    [InlineData("prefix", "bad id")]
    public void DomainSrsAndIdentityLimitsAreRespected(string field, string value)
    {
        var form = Form(); Set(form, field, value); Assert.ThrowsAny<ArgumentException>(() => form.Build());
    }

    [Fact]
    public void ModeValidationAndDecimalTicksRemainExplicit()
    {
        var form = Form(); form.SelectedInput = (Sr830InputMode)999; Assert.Throws<ArgumentOutOfRangeException>(() => form.Build());
        form.SelectedInput = Sr830InputMode.VoltageAB; form.SettleSeconds = "0.0000001000000000"; Assert.Equal(1, form.Build().Settle.Ticks);
        form.SettleSeconds = "0"; Assert.Equal(TimeSpan.Zero, form.Build().Settle);
        Assert.Contains("最小振幅非零", form.SafetySummary); Assert.Contains("不证明", form.SafetySummary); Assert.Contains("原始电压域", form.InputSummary); Assert.Contains("不配置输入", form.InputSummary);
    }

    [Fact]
    public void DriverKindComesFromChosenGenerator()
    {
        var simulation = Form(); Assert.Contains("Simulation", simulation.SelectionSummary);
        var hardware = new PlanSr830AcFormViewModel(Step(), [Generator(kind: DriverKind.Hardware)], [Matrix()], () => { });
        Assert.Contains("Hardware", hardware.SelectionSummary); Assert.DoesNotContain("Simulation", hardware.SelectionSummary);
    }

    private static PlanSr830AcFormViewModel Form(bool template = false) => new(Step(), [Generator()], [Matrix()], () => { }, template);
    private static void Set(PlanSr830AcFormViewModel form, string field, string value)
    {
        switch (field)
        {
            case "frequency": form.Frequency = value; break;
            case "amplitude": form.SineAmplitude = value; break;
            case "harmonic": form.Harmonic = value; break;
            case "settle": form.SettleSeconds = value; break;
            case "prefix": form.Prefix = value; break;
        }
    }
    private static DeviceDescriptor Generator(string id = "generator", DriverKind kind = DriverKind.Simulation)
        => new(new(id), "SR830", id, kind, DeviceStopPolicy.AbortAcquisition, Sr830Parameters.WithReference);
    private static MatrixSwitchDefinition Matrix(string id = "isolator", string generator = "generator", IEnumerable<MatrixCrossPoint>? connections = null)
        => new(new(new(id), "K3706", id, DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 2, 2)], [],
            new(new(generator), connections ?? [new(1, 1, 1), new(1, 1, 2)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));
    private static ReadSr830AcFrameStep Step(Sr830InputMode input = Sr830InputMode.VoltageAB)
        => new(new("ac-frame"), new("generator"), new("isolator"), [new(1, 1, 1), new(1, 1, 2)],
            new(new(17.1234567890123, PhysicalUnit.Hertz), new(.01234567890123, PhysicalUnit.Volt), 3), input, new("raw_ac"), TimeSpan.FromTicks(123457));
}
