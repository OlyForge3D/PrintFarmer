namespace Farm.Infrastructure;

/// <summary>
/// Result of resolving an NFC tag UID through Spoolman's persistent tag mapping.
/// </summary>
public sealed record SpoolmanTagScanResultDto(
    int? MatchedSpoolId,
    int? MatchedFilamentId,
    SpoolmanSpoolDto? Spool,
    SpoolmanFilamentDto? Filament,
    bool Created,
    System.Text.Json.JsonElement? Decoded = null);

/// <summary>
/// Request to associate an NFC tag UID with a Spoolman spool.
/// </summary>
public sealed record SpoolmanTagLinkRequest(string Uid);

/// <summary>
/// Request to resolve a tag UID and optionally decode its NDEF payload.
/// </summary>
public sealed record SpoolmanTagScanRequest(
    string Uid,
    string? Format = null,
    string? PayloadB64 = null);
