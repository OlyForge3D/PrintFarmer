namespace Farm.Infrastructure;

/// <summary>
/// Minimal printer projection used by dashboard statistics and alert widgets.
/// </summary>
public sealed record PrinterSummaryDto(
    Guid Id,
    string Name,
    bool IsOnline,
    string? State,
    bool InMaintenance,
    bool IsEnabled,
    bool HasCatalogUpdate);
