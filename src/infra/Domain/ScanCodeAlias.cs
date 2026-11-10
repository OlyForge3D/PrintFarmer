namespace Farm.Infrastructure.Domain;

/// <summary>
/// Remembers a scanned code that is not a retail GTIN (for example an Amazon FNSKU such as
/// <c>X0022Z40I1</c>) and the Spoolman filament the user chose for it. Codes are stored and
/// matched exactly; they never pass through <c>GtinNormalizer</c> and never touch the
/// Spoolman <c>filament.gtin</c> mapping.
/// </summary>
public class ScanCodeAlias
{
    public int Id { get; set; }

    /// <summary>Trimmed scanned value, compared ordinally (case-sensitive).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Spoolman filament ID the code resolves to.</summary>
    public int FilamentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedByUserId { get; set; }
}
