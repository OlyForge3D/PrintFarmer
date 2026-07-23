using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Persists and reads optional barcode scan diagnostics.
/// </summary>
public interface IBarcodeScanLogService
{
    Task LogAsync(BarcodeScanLog log, CancellationToken ct = default);

    Task<IReadOnlyList<BarcodeScanLog>> GetRecentAsync(int limit, CancellationToken ct);
}
