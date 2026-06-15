using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Mock;

public static class MockInstrumentCatalog
{
    public static IReadOnlyList<InstrumentConnection> CreateDefault()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new(
                "smu-2450",
                "Keithley 2450 SMU",
                "Source/measure",
                "TCP 127.0.0.1:5025",
                InstrumentConnectionState.Connected,
                "Mock output is safe",
                now),
            new(
                "nanovoltmeter-2182a",
                "Keithley 2182A",
                "Voltage sense",
                "GPIB0::7::INSTR",
                InstrumentConnectionState.Connected,
                "Mock noise: 12 nV RMS",
                now),
            new(
                "lockin-sr830",
                "SR830 Lock-in",
                "AC measurement",
                "GPIB0::8::INSTR",
                InstrumentConnectionState.Disconnected,
                "Ready to connect",
                now),
        ];
    }
}
