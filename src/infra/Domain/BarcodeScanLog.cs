namespace Farm.Infrastructure.Domain;

/// <summary>
/// Barcode action recorded for backend-side scan diagnostics.
/// </summary>
public enum BarcodeScanAction
{
    Resolve = 0,
    Import = 1,
    Mapping = 2,
}

/// <summary>
/// Result recorded for a barcode scan diagnostic entry.
/// </summary>
public enum BarcodeScanOutcome
{
    Resolved = 0,
    NotFound = 1,
    Imported = 2,
    Mapped = 3,
    Error = 4,
}

/// <summary>
/// Optional diagnostic log entry for a backend barcode scan attempt.
/// </summary>
public class BarcodeScanLog
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Barcode { get; set; } = string.Empty;

    public BarcodeScanAction Action { get; set; }

    public BarcodeScanOutcome Outcome { get; set; }

    public int HttpStatus { get; set; }

    public int? MatchedFilamentId { get; set; }

    public int? CreatedSpoolId { get; set; }

    public string? UserId { get; set; }

    public string? Message { get; set; }
}
