namespace InstrumentControlWorkbench.Core.Instruments;

public enum InstrumentReadFailureKind { ReadTimeout, BufferTimeout, CommandRejected, Overrange, InvalidResponse, VisaIoRead }

// A port may report this only after its acquisition task has terminated. It is
// not proof of idle hardware, output-off or reusable transport state.
public sealed class InstrumentReadException : Exception
{
    public InstrumentReadException(InstrumentId device, InstrumentReadFailureKind kind, string message, int? commandCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(kind) || (kind == InstrumentReadFailureKind.CommandRejected) != (commandCode is not null) || message.Length > 4096)
            throw new ArgumentException("Read failures require a defined kind, bounded message and a code only for command rejection.");
        Device = device; Kind = kind; CommandCode = commandCode;
    }
    public InstrumentId Device { get; }
    public InstrumentReadFailureKind Kind { get; }
    public int? CommandCode { get; }
    public bool AllowsSingleRetry => IsSingleRetryEligible(Kind, CommandCode);
    public static bool IsSingleRetryEligible(InstrumentReadFailureKind kind, int? commandCode) => kind is InstrumentReadFailureKind.ReadTimeout or InstrumentReadFailureKind.BufferTimeout || kind == InstrumentReadFailureKind.CommandRejected && commandCode == -221;
}
