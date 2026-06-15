namespace InstrumentControlWorkbench.Core.Instruments;

public readonly record struct InstrumentId(string Value)
{
    public override string ToString() => Value;
}
