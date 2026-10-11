using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record BlackboardValueDto(BlackboardValueKind Kind, QuantityDto? Quantity = null, bool? Boolean = null, string? Text = null)
    {
        public BlackboardValue ToDomain() => Kind switch
        {
            BlackboardValueKind.Quantity when Quantity is not null && Boolean is null && Text is null => new BlackboardQuantity(Quantity.ToDomain()),
            BlackboardValueKind.Boolean when Boolean is { } value && Quantity is null && Text is null => new BlackboardBoolean(value),
            BlackboardValueKind.Text when Text is { } value && Quantity is null && Boolean is null => new BlackboardText(value),
            _ => throw new FormatException("Blackboard value requires exactly one matching typed payload."),
        };
        public static BlackboardValueDto From(BlackboardValue value) => value switch
        {
            BlackboardQuantity quantity => new(BlackboardValueKind.Quantity, QuantityDto.From(quantity.Value)),
            BlackboardBoolean boolean => new(BlackboardValueKind.Boolean, Boolean: boolean.Value),
            BlackboardText text => new(BlackboardValueKind.Text, Text: text.Value),
            _ => throw new NotSupportedException("Unknown blackboard value kind."),
        };
    }
    internal sealed record BlackboardEntryDto(string Variable, BlackboardValueDto Value, string Source, DateTimeOffset UpdatedAt, long ElapsedTicks, BlackboardWriteKind Kind)
    {
        public KeyValuePair<ParameterId, BlackboardEntry> ToDomain() => new(new(Variable), new(Required(Value).ToDomain(), new(Source), UpdatedAt, TimeSpan.FromTicks(ElapsedTicks), Kind));
        public static BlackboardEntryDto From(KeyValuePair<ParameterId, BlackboardEntry> entry) => new(entry.Key.Value, BlackboardValueDto.From(entry.Value.Value), entry.Value.Source.Value,
            entry.Value.UpdatedAt, entry.Value.Elapsed.Ticks, entry.Value.Kind);
    }
    internal sealed record BlackboardDto(Guid Id, DateTimeOffset CapturedAt, long ElapsedTicks, BlackboardEntryDto[] Entries)
    {
        public RunBlackboardSnapshot ToDomain() => new(new(Id), CapturedAt, TimeSpan.FromTicks(ElapsedTicks), Required(Entries).Select(entry => Required(entry).ToDomain()));
        public static BlackboardDto From(RunBlackboardSnapshot snapshot) => new(snapshot.Id.Value, snapshot.CapturedAt, snapshot.Elapsed.Ticks,
            snapshot.Entries.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal).Select(BlackboardEntryDto.From).ToArray());
    }
}
