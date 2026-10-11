using System.Globalization;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Tests;

// Independent native-SCPI loopback instrument: actual mutable registers, error
// replies and output state; no source values are returned by the driver fixture.
internal sealed class Keithley2450ScpiPeer : IAsyncDisposable
{
    private readonly Dictionary<SourceFunction, SourceSetpoint> sources = new() { [SourceFunction.Voltage] = new(SourceFunction.Voltage, 0, 105e-6), [SourceFunction.Current] = new(SourceFunction.Current, 0, 21) };
    private SourceFunction function;
    private readonly Dictionary<SenseFunction, Keithley2450AcquisitionSettings> acquisition = Enum.GetValues<SenseFunction>().ToDictionary(f => f, _ => new Keithley2450AcquisitionSettings(true, false, false, 10, AveragingMode.Repeat));
    private readonly Dictionary<SenseFunction, SenseConfiguration> senses = Enum.GetValues<SenseFunction>().ToDictionary(f => f, f => new SenseConfiguration(f, SensingMode.TwoWire, 1, true));
    private readonly Dictionary<SenseFunction, double> senseRanges = new() { [SenseFunction.Current] = 1e-4, [SenseFunction.Voltage] = 20, [SenseFunction.Resistance] = 200000 };
    private SenseFunction senseFunction = SenseFunction.Current;
    private SenseConfiguration sense { get => senses[senseFunction]; set { senses[value.Function] = value; senseFunction = value.Function; } }
    private readonly Dictionary<string, bool> readback = new() { ["VOLT"] = false, ["CURR"] = false };
    public Keithley2450ScpiPeer() => Server = new(RespondAsync);
    public LoopbackScpiServer Server { get; }
    public string Identifier { get; set; } = "KEITHLEY INSTRUMENTS INC.,MODEL 2450,TEST-ONLY,1.7.0";
    public string Language { get; set; } = "SCPI";
    public bool Output { get; set; }
    public bool RefuseOff { get; set; }
    public bool IgnoreLevelWrite { get; set; }
    public bool IgnoreRangeWrite { get; set; }
    public bool IgnoreSenseRangeWrite { get; set; }
    public double? SenseWriteChangesCompliance { get; set; }
    public int Aborts { get; private set; }
    public double ActualSource { get; set; } = 0.0197;
    public double ActualSense { get; set; } = 0.000021;
    public bool IgnoreAcquisitionWrites { get; set; }
    public int ZeroRefreshes { get; private set; }
    public void SetAcquisition(SenseFunction function, Keithley2450AcquisitionSettings settings) => acquisition[function] = settings;
    public ushort ExtraSourceStatus { get; set; }
    public ushort MeasurementStatus { get; set; }
    public Queue<string> Errors { get; } = new();
    public Func<string, CancellationToken, Task<byte[]?>?>? Override { get; set; }
    private Task<byte[]?> RespondAsync(string command, CancellationToken token)
    {
        var replacement = Override?.Invoke(command, token); if (replacement is not null) return replacement;
        var source = sources[function]; var sourcePrefix = function == SourceFunction.Voltage ? "VOLT" : "CURR";
        var sensePrefix = sense.Function switch { SenseFunction.Current => "CURR", SenseFunction.Voltage => "VOLT", _ => "RES" };
        var status = ExtraSourceStatus | (Output ? 128 : 0) | (readback[sourcePrefix] ? 8 : 0) | (sense.Mode == SensingMode.FourWire ? 64 : 0);
        string? reply = command switch
        {
            "*IDN?" => Identifier,
            "*LANG?" => Language,
            "*OPC?" => "1",
            ":OUTP?" => Output ? "1" : "0",
            ":SOUR:FUNC?" => sourcePrefix,
            ":SENS:FUNC?" => "\"" + sensePrefix + "\"",
            ":ROUT:TERM?" => "REAR",
            ":FORM:DATA?" => "ASC",
            ":SENS:COUN?" => "1",
            ":SYST:ERR?" => Errors.Count == 0 ? "0,\"No error\"" : Errors.Dequeue(),
            ":MEAS? \"defbuffer1\", READ, SOURSTAT, STAT" => ActualSense.ToString("G17", CultureInfo.InvariantCulture) + $",{status},{MeasurementStatus}",
            ":MEAS? \"defbuffer1\", SOUR, READ, SOURSTAT, STAT" => ActualSource.ToString("G17", CultureInfo.InvariantCulture) + "," + ActualSense.ToString("G17", CultureInfo.InvariantCulture) + $",{status},{MeasurementStatus}",
            _ => null,
        };
        if (reply is not null) return Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(reply));
        var parts = command.Split(' ', 2); var path = parts[0].Split(':', StringSplitOptions.RemoveEmptyEntries); var value = parts.Length == 2 ? parts[1] : "";
        if (command == ":SENS:AZER:ONCE") { ZeroRefreshes++; return Task.FromResult<byte[]?>(null); }
        if (command == ":ABOR") { Aborts++; return Task.FromResult<byte[]?>(null); }
        if (parts[0] == ":OUTP") { if (value == "ON") Output = true; else if (!RefuseOff) Output = false; return Task.FromResult<byte[]?>(null); }
        if (command is ":FORM:DATA ASC" or ":SENS:COUN 1") return Task.FromResult<byte[]?>(null);
        if (parts[0] == ":SOUR:FUNC") { function = value == "VOLT" ? SourceFunction.Voltage : SourceFunction.Current; return Task.FromResult<byte[]?>(null); }
        if (parts[0] == ":SENS:FUNC") { sense = senses[value.Trim('"') switch { "VOLT" => SenseFunction.Voltage, "CURR" => SenseFunction.Current, _ => SenseFunction.Resistance }]; return Task.FromResult<byte[]?>(null); }
        if (path.Length >= 3 && path[0] == "SOUR")
        {
            var selected = path[1] == "VOLT" ? SourceFunction.Voltage : SourceFunction.Current; var old = sources[selected];
            if (path[2] == "LEV?") reply = old.Level.ToString("G17", CultureInfo.InvariantCulture);
            else if (path[2] is "ILIM?" or "VLIM?") reply = old.Compliance.ToString("G17", CultureInfo.InvariantCulture);
            else if (path[2] == "READ" && path[^1] == "BACK?") reply = readback[path[1]] ? "1" : "0";
            else if (path[2] == "READ") readback[path[1]] = value == "ON";
            else if (path[2] == "RANG?") reply = (old.ManualRange?.Value ?? InstrumentControlWorkbench.Core.Instruments.Keithley2450SourceRanges.For(selected).First(q => Math.Abs(old.Level) <= q.Value * 1.05).Value).ToString("G17", CultureInfo.InvariantCulture);
            else if (path[2] == "RANG" && path[^1] == "AUTO?") reply = old.ManualRange is null ? "1" : "0";
            else if (path[2] == "RANG" && path[^1] == "AUTO") sources[selected] = old with { ManualRange = value == "ON" ? null : old.ManualRange ?? Keithley2450SourceRanges.For(selected).First(q => Math.Abs(old.Level) <= q.Value * 1.05) };
            else if (path[2] == "RANG")
            {
                var range = new InstrumentControlWorkbench.Core.Parameters.Quantity(double.Parse(value, CultureInfo.InvariantCulture), selected == SourceFunction.Voltage ? InstrumentControlWorkbench.Core.Parameters.PhysicalUnit.Volt : InstrumentControlWorkbench.Core.Parameters.PhysicalUnit.Ampere);
                try { Keithley2450Limits.Validate(old with { ManualRange = range }); if (!IgnoreRangeWrite) sources[selected] = old with { ManualRange = range }; }
                catch (ArgumentOutOfRangeException) { Errors.Enqueue("-222,\"Source range cannot accommodate level\""); }
            }
            else if (path[2] is "ILIM" or "VLIM" or "LEV")
            {
                var candidate = path[2] == "LEV" ? old with { Level = double.Parse(value, CultureInfo.InvariantCulture) } : old with { Compliance = double.Parse(value, CultureInfo.InvariantCulture) };
                try { Keithley2450Limits.Validate(candidate); if (path[2] != "LEV" || !IgnoreLevelWrite) sources[selected] = candidate; }
                catch (ArgumentOutOfRangeException) { Errors.Enqueue("-222,\"Forbidden intermediate source quadrant\""); }
            }
            else throw new InvalidOperationException("Unexpected source command: " + command);
        }
        else if (path.Length >= 3 && path[0] == "SENS")
        {
            var selected = path[1] == "VOLT" ? SenseFunction.Voltage : path[1] == "CURR" ? SenseFunction.Current : SenseFunction.Resistance;
            var settings = acquisition[selected];
            if (path[2] == "AZER?") reply = settings.AutoZero ? "1" : "0";
            else if (path[2] == "AVER" && path[^1] == "STAT?") reply = settings.AveragingEnabled ? "1" : "0";
            else if (path[2] == "AVER" && path[^1] == "COUN?") reply = settings.AveragingCount.ToString(CultureInfo.InvariantCulture);
            else if (path[2] == "AVER" && path[^1] == "TCON?") reply = settings.AveragingMode == AveragingMode.Repeat ? "REP" : "MOV";
            else if (path[2] == "RANG" && path[^1] == "REB?") reply = settings.AutorangeRebound ? "1" : "0";
            else if (path[2] == "AZER" || path[2] == "AVER" || path[2] == "RANG" && path[^1] == "REB")
            {
                if (!IgnoreAcquisitionWrites)
                    acquisition[selected] = new(path[2] == "AZER" ? value == "ON" : settings.AutoZero,
                        path[^1] == "REB" ? value == "ON" : settings.AutorangeRebound,
                        path[^1] == "STAT" ? value == "ON" : settings.AveragingEnabled,
                        path[^1] == "COUN" ? int.Parse(value, CultureInfo.InvariantCulture) : settings.AveragingCount,
                        path[^1] == "TCON" ? value == "MOV" ? AveragingMode.Moving : AveragingMode.Repeat : settings.AveragingMode);
            }
            else if (path[2] == "RANG?") reply = senseRanges[selected].ToString("G17", CultureInfo.InvariantCulture);
            else if (path[2] == "RSEN?") reply = sense.Mode == SensingMode.FourWire ? "1" : "0";
            else if (path[2] == "NPLC?") reply = sense.Nplc.ToString("G17", CultureInfo.InvariantCulture);
            else if (path[2] == "RANG" && path[^1] == "AUTO?") reply = sense.AutoRange ? "1" : "0";
            else if (path[2] == "RSEN") sense = sense with { Mode = value == "ON" ? SensingMode.FourWire : SensingMode.TwoWire };
            else if (path[2] == "NPLC") sense = sense with { Nplc = double.Parse(value, CultureInfo.InvariantCulture) };
            else if (path[2] == "RANG" && path[^1] == "AUTO") sense = sense with { AutoRange = value == "ON", ManualRange = value == "ON" ? null : new(senseRanges[selected], Keithley2450SenseRanges.For(selected)[0].Unit) };
            else if (path[2] == "RANG")
            {
                var range = double.Parse(value, CultureInfo.InvariantCulture);
                if (!IgnoreSenseRangeWrite)
                {
                    senseRanges[selected] = range; sense = sense with { AutoRange = false, ManualRange = new(range, Keithley2450SenseRanges.For(selected)[0].Unit) };
                    if (SenseWriteChangesCompliance is { } changed) sources[function] = sources[function] with { Compliance = changed };
                }
            }
            else throw new InvalidOperationException("Unexpected sense command: " + command);
        }
        else throw new InvalidOperationException("Unexpected SCPI command: " + command);
        return Task.FromResult<byte[]?>(reply is null ? null : LoopbackScpiServer.Reply(reply));
    }
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
