using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

[JsonConverter(typeof(PrinterControlKindJsonConverter))]
public enum PrinterControlKind
{
    HomeAll,
    HomeXY,
    HomeZ,
    Jog,
    MoveTo
}

[JsonConverter(typeof(JsonStringEnumConverter<PrinterControlState>))]
public enum PrinterControlState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Unknown,
    Recovering,
    Recovered
}

[JsonConverter(typeof(JsonStringEnumConverter<PrinterControlEvidence>))]
public enum PrinterControlEvidence
{
    None,
    NotSent,
    BackendRejected,
    MotionQueueDrained,
    OperatorVerifiedRecovery
}

[JsonConverter(typeof(JsonStringEnumConverter<PrinterSenderIsolation>))]
public enum PrinterSenderIsolation
{
    NotRequested,
    Pending,
    Confirmed,
    ExternalVerificationRequired
}

public sealed class PrinterControlKindJsonConverter() : JsonStringEnumConverter<PrinterControlKind>(namingPolicy: null, allowIntegerValues: false);

public sealed record PrinterControlRequest([property: JsonRequired] PrinterControlKind Kind, double? X = null, double? Y = null, double? Z = null, double? F = null);

public sealed record PrinterControlFailure(string Code, string Message);

public sealed record PrinterControlOperationDto(
    Guid OperationId, Guid PrinterId, PrinterControlKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? X,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Z,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? F,
    PrinterControlState State, string RowVersion, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? StartedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CompletedAtUtc,
    bool BarrierHeld, bool RequiresRecovery,
    PrinterControlEvidence CompletionEvidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PrinterControlFailure? Failure,
    PrinterSenderIsolation SenderIsolation);

public sealed record PrinterPhysicalControlDto(
    PrinterControlKind[] SupportedOperations, bool BarrierHeld,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? OperationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PrinterControlState? State, bool RequiresRecovery);

public sealed record PrinterControlCurrentDto(PrinterPhysicalControlDto PhysicalControl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PrinterControlOperationDto? Operation);

public sealed record PrinterControlRecoveryRequest(
    string Reason, string SenderIsolation, string SenderIsolationEvidence,
    bool ControllerQueueCleared, bool PhysicallyStationary, string PhysicalEvidence);

public sealed record PrinterControlInvalidation(Guid PrinterId, Guid OperationId, string RowVersion);
