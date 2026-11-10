using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>Outcome of saving a remembered scan code.</summary>
public enum ScanCodeAliasSaveStatus
{
    Created = 0,
    AlreadyMapped = 1,
    Replaced = 2,
    Conflict = 3,
    FilamentNotFound = 4,
    Invalid = 5,
}

/// <summary>Result of <see cref="IScanCodeAliasService.SaveAsync"/>.</summary>
public sealed record ScanCodeAliasSaveResult(
    ScanCodeAliasSaveStatus Status,
    ScanCodeAlias? Alias,
    SpoolmanFilamentDto? Filament,
    string? Error);

/// <summary>Outcome of looking up a remembered scan code.</summary>
public enum ScanCodeAliasLookupStatus
{
    Found = 0,
    NotFound = 1,
    FilamentMissing = 2,
    Invalid = 3,
}

/// <summary>Result of <see cref="IScanCodeAliasService.LookupAsync"/>.</summary>
public sealed record ScanCodeAliasLookupResult(
    ScanCodeAliasLookupStatus Status,
    ScanCodeAlias? Alias,
    SpoolmanFilamentDto? Filament,
    string? Error);

/// <summary>
/// Stores exact-match aliases from non-GTIN scan codes (such as Amazon FNSKUs) to Spoolman
/// filaments, in PrintFarmer's database and separate from the Spoolman GTIN mapping.
/// </summary>
public interface IScanCodeAliasService
{
    /// <summary>
    /// Resolves a scanned code to its remembered filament. Spoolman failures propagate as
    /// exceptions so callers never treat an outage as an unknown code.
    /// </summary>
    Task<ScanCodeAliasLookupResult> LookupAsync(string? code, CancellationToken ct);

    /// <summary>
    /// Remembers a code for a filament. Saving the same pair again is a no-op; a code already
    /// remembered for a different, still-existing filament is a conflict and is not changed.
    /// </summary>
    Task<ScanCodeAliasSaveResult> SaveAsync(string? code, int filamentId, string? userId, CancellationToken ct);
}
