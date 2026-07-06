using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Admin-facing barcode scan diagnostic log entry.
/// </summary>
public record BarcodeScanLogDto(
    int Id,
    DateTime Timestamp,
    string Barcode,
    BarcodeScanAction Action,
    BarcodeScanOutcome Outcome,
    int HttpStatus,
    int? MatchedFilamentId,
    int? CreatedSpoolId,
    string? UserId,
    string? Message);
