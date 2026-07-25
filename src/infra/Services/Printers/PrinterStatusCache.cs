using System;
using System.Collections.Generic;
using System.IO;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Shared interface for reading printer status cache.
/// Used by API layer to retrieve cached status without external API calls.
/// </summary>
public interface IPrinterStatusCacheReader
{
    /// <summary>
    /// Get the cached status for a specific printer, or null if not cached.
    /// </summary>
    PrinterStatusDto? GetStatus(Guid printerId);

    /// <summary>
    /// Get all cached printer statuses.
    /// </summary>
    IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses();
}

/// <summary>Reads freshness-aware status snapshots for safety-sensitive operations.</summary>
public interface IPrinterStatusSnapshotReader
{
    /// <summary>Gets the latest status observation and last-seen timestamp for a printer.</summary>
    PrinterStatusSnapshot? GetStatusSnapshot(Guid printerId);
}

/// <summary>Freshness metadata attached when a backend status update reaches the server.</summary>
public sealed record PrinterStatusSnapshot(
    PrinterStatusDto Status,
    DateTime? ObservedAtUtc,
    DateTime? LastSeenAtUtc,
    string Source);

/// <summary>
/// Thread-safe in-memory cache for printer status updates from SignalR.
/// Stores the latest status for each printer to enable fast list operations without external API calls.
/// This cache is shared between:
/// - Backend services (MoonrakerSubscriptionService, PrusaLinkPollingService) - write updates
/// - API layer (PrintersService) - read cached data for list endpoints
/// </summary>
public class PrinterStatusCache : IPrinterStatusCacheReader, IPrinterStatusSnapshotReader, IPrinterStatusCacheWriter
{
    private readonly Dictionary<Guid, PrinterStatusSnapshot> _cache = new();
    private readonly Lock _lockObj = new();
    private readonly ILogger<PrinterStatusCache> _logger;
    private readonly IDiagnosticChannelService _diagnostics;

    public PrinterStatusCache(ILogger<PrinterStatusCache> logger, IDiagnosticChannelService diagnostics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public PrinterStatusDto? GetStatus(Guid printerId)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusSnapshot? snapshot);
            return snapshot?.Status;
        }
    }

    public IReadOnlyDictionary<Guid, PrinterStatusDto> GetAllStatuses()
    {
        lock (_lockObj)
        {
            return _cache.ToDictionary(pair => pair.Key, pair => pair.Value.Status);
        }
    }

    public PrinterStatusSnapshot? GetStatusSnapshot(Guid printerId)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusSnapshot? snapshot);
            return snapshot;
        }
    }

    public void UpdateStatus(PrinterStatusDto status)
    {
        lock (_lockObj)
        {
            DateTime observedAtUtc = DateTime.UtcNow;
            _cache.TryGetValue(status.Id, out PrinterStatusSnapshot? existing);
            PrinterStatusDto normalized = status.WithNormalizedFileName();
            _cache[status.Id] = new PrinterStatusSnapshot(
                normalized,
                observedAtUtc,
                normalized.IsOnline ? observedAtUtc : existing?.LastSeenAtUtc,
                "backend");
            LogTransitionIfChanged(status.Id, existing?.Status, normalized);
        }
    }

    public void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses)
    {
        if (statuses == null)
        {
            return;
        }

        lock (_lockObj)
        {
            foreach (PrinterStatusDto status in statuses)
            {
                DateTime observedAtUtc = DateTime.UtcNow;
                _cache.TryGetValue(status.Id, out PrinterStatusSnapshot? existing);
                PrinterStatusDto normalized = status.WithNormalizedFileName();
                _cache[status.Id] = new PrinterStatusSnapshot(
                    normalized,
                    observedAtUtc,
                    normalized.IsOnline ? observedAtUtc : existing?.LastSeenAtUtc,
                    "backend");
                LogTransitionIfChanged(status.Id, existing?.Status, normalized);
            }
        }
    }

    public PrinterStatusDto UpdateSpoolInfo(Guid printerId, PrinterSpoolInfoDto? spoolInfo)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusSnapshot? existing);
            PrinterStatusDto updated = (existing?.Status ?? new PrinterStatusDto(Id: printerId, IsOnline: false, State: "Unknown"))
                with
            { SpoolInfo = spoolInfo };
            _cache[printerId] = existing is null
                ? new PrinterStatusSnapshot(updated, null, null, "unknown")
                : existing with { Status = updated };
            return updated;
        }
    }

    public void ClearStatus(Guid printerId)
    {
        lock (_lockObj)
        {
            _cache.Remove(printerId);
        }
    }

    public void ClearAllStatuses()
    {
        lock (_lockObj)
        {
            _cache.Clear();
        }
    }

    private void LogTransitionIfChanged(Guid printerId, PrinterStatusDto? previous, PrinterStatusDto current)
    {
        string? prevState = previous?.State;
        bool prevOnline = previous?.IsOnline ?? false;
        bool stateChanged = !string.Equals(prevState, current.State, StringComparison.OrdinalIgnoreCase);
        bool onlineChanged = prevOnline != current.IsOnline;

        if (!stateChanged && !onlineChanged)
        {
            return;
        }

        if (_diagnostics.IsEnabled(DiagnosticChannels.PrinterStateTransitions))
        {
            _logger.LogWarning(
                "[PrinterStateTransition] Printer {PrinterId}: State '{PreviousState}' -> '{NewState}', Online {PreviousOnline} -> {NewOnline}",
                printerId,
                prevState ?? "(none)",
                current.State ?? "(none)",
                prevOnline,
                current.IsOnline);
        }
        else
        {
            _logger.LogDebug(
                "[PrinterStateTransition] Printer {PrinterId}: State '{PreviousState}' -> '{NewState}', Online {PreviousOnline} -> {NewOnline}",
                printerId,
                prevState ?? "(none)",
                current.State ?? "(none)",
                prevOnline,
                current.IsOnline);
        }
    }
}
