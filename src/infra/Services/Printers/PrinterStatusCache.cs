using System;
using System.Collections.Generic;
using System.IO;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
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
    /// Gets the cached status together with the UTC time at which the cache last received it.
    /// Consumers that make safety-sensitive decisions must validate freshness before trusting
    /// live-only fields such as MMU active tool/gate telemetry.
    /// </summary>
    PrinterStatusCacheSnapshot? GetSnapshot(Guid printerId);

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
/// Optional cache-reader capability for atomic fleet snapshots with provenance.
/// </summary>
public interface IPrinterStatusCacheProvenanceReader
{
    /// <summary>
    /// Gets all cached statuses with their original producer provenance.
    /// </summary>
    IReadOnlyDictionary<Guid, PrinterStatusCacheSnapshot> GetAllSnapshots();
}

/// <summary>
/// A printer status and the UTC time at which it entered the in-memory cache.
/// </summary>
public sealed record PrinterStatusCacheSnapshot(
    PrinterStatusDto Status,
    DateTime UpdatedAtUtc,
    long? OriginWatermark = null,
    DateTime? LastSeenAtUtc = null,
    string Source = "backend");

/// <summary>
/// Shared freshness policy for decisions that require live printer telemetry.
/// </summary>
public static class PrinterStatusFreshness
{
    /// <summary>
    /// Maximum accepted age for a cached live status. Backend polling normally refreshes in
    /// seconds; two minutes tolerates transient reconnects without treating old state as live.
    /// </summary>
    public static TimeSpan MaximumAge { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns whether a snapshot is online, not future-dated, and within
    /// <see cref="MaximumAge"/> of <paramref name="utcNow"/>.
    /// </summary>
    public static bool IsFreshOnline(PrinterStatusCacheSnapshot? snapshot, DateTime utcNow)
    {
        return snapshot is not null
            && snapshot.Status.IsOnline
            && snapshot.UpdatedAtUtc <= utcNow
            && utcNow - snapshot.UpdatedAtUtc <= MaximumAge;
    }
}

/// <summary>
/// Thread-safe in-memory cache for printer status updates from SignalR.
/// Stores the latest status for each printer to enable fast list operations without external API calls.
/// This cache is shared between:
/// - Backend services (MoonrakerSubscriptionService, PrusaLinkPollingService) - write updates
/// - API layer (PrintersService) - read cached data for list endpoints
/// </summary>
public class PrinterStatusCache :
    IPrinterStatusCacheReader,
    IPrinterStatusCacheProvenanceReader,
    IPrinterStatusSnapshotReader,
    IPrinterStatusCacheWriter
{
    private readonly Dictionary<Guid, PrinterStatusCacheSnapshot> _cache = new();
    private readonly Lock _lockObj = new();
    private readonly ILogger<PrinterStatusCache> _logger;
    private readonly IDiagnosticChannelService _diagnostics;

    // Attention feed invalidation for offline/online transitions (issue #707, review R3).
    // Optional so existing constructors/tests that predate the attention feed keep working.
    private readonly IAttentionBroadcaster? _attentionBroadcaster;

    public PrinterStatusCache(
        ILogger<PrinterStatusCache> logger,
        IDiagnosticChannelService diagnostics,
        IAttentionBroadcaster? attentionBroadcaster = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _attentionBroadcaster = attentionBroadcaster;
    }

    public PrinterStatusDto? GetStatus(Guid printerId)
    {
        lock (_lockObj)
        {
            return _cache.TryGetValue(printerId, out PrinterStatusCacheSnapshot? snapshot)
                ? snapshot.Status
                : null;
        }
    }

    public PrinterStatusCacheSnapshot? GetSnapshot(Guid printerId)
    {
        lock (_lockObj)
        {
            return _cache.TryGetValue(printerId, out PrinterStatusCacheSnapshot? snapshot)
                ? snapshot
                : null;
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
            return _cache.TryGetValue(printerId, out PrinterStatusCacheSnapshot? snapshot)
                ? new PrinterStatusSnapshot(
                    snapshot.Status,
                    snapshot.UpdatedAtUtc,
                    snapshot.LastSeenAtUtc,
                    snapshot.Source)
                : null;
        }
    }

    public IReadOnlyDictionary<Guid, PrinterStatusCacheSnapshot> GetAllSnapshots()
    {
        lock (_lockObj)
        {
            return new Dictionary<Guid, PrinterStatusCacheSnapshot>(_cache);
        }
    }

    public void UpdateStatus(PrinterStatusDto status, long? originWatermark = null)
    {
        AttentionChangeKind? transition;
        lock (_lockObj)
        {
            _cache.TryGetValue(status.Id, out PrinterStatusCacheSnapshot? existingSnapshot);
            PrinterStatusDto? existing = existingSnapshot?.Status;
            DateTime observedAtUtc = DateTime.UtcNow;
            PrinterStatusDto normalized = status.WithNormalizedFileName();
            _cache[status.Id] = new PrinterStatusCacheSnapshot(
                normalized,
                observedAtUtc,
                originWatermark,
                normalized.IsOnline ? observedAtUtc : existingSnapshot?.LastSeenAtUtc,
                "backend");
            LogTransitionIfChanged(status.Id, existing, normalized);
            transition = DetectOfflineTransition(existing, normalized);
        }

        EmitOfflineTransition(status.Id, transition);
    }

    public void UpdateStatuses(IEnumerable<PrinterStatusDto> statuses, long? originWatermark = null)
    {
        if (statuses == null)
        {
            return;
        }

        List<(Guid PrinterId, AttentionChangeKind Kind)>? transitions = null;
        lock (_lockObj)
        {
            foreach (PrinterStatusDto status in statuses)
            {
                _cache.TryGetValue(status.Id, out PrinterStatusCacheSnapshot? existingSnapshot);
                PrinterStatusDto? existing = existingSnapshot?.Status;
                DateTime observedAtUtc = DateTime.UtcNow;
                PrinterStatusDto normalized = status.WithNormalizedFileName();
                _cache[status.Id] = new PrinterStatusCacheSnapshot(
                    normalized,
                    observedAtUtc,
                    originWatermark,
                    normalized.IsOnline ? observedAtUtc : existingSnapshot?.LastSeenAtUtc,
                    "backend");
                LogTransitionIfChanged(status.Id, existing, normalized);
                if (DetectOfflineTransition(existing, normalized) is AttentionChangeKind kind)
                {
                    (transitions ??= new()).Add((status.Id, kind));
                }
            }
        }

        if (transitions is not null)
        {
            foreach ((Guid printerId, AttentionChangeKind kind) in transitions)
            {
                EmitOfflineTransition(printerId, kind);
            }
        }
    }

    public PrinterStatusDto UpdateSpoolInfo(Guid printerId, PrinterSpoolInfoDto? spoolInfo)
    {
        lock (_lockObj)
        {
            _cache.TryGetValue(printerId, out PrinterStatusCacheSnapshot? existingSnapshot);
            PrinterStatusDto updated = (existingSnapshot?.Status ?? new PrinterStatusDto(Id: printerId, IsOnline: false, State: "Unknown"))
                with
            { SpoolInfo = spoolInfo };
            _cache[printerId] = existingSnapshot is null
                ? new PrinterStatusCacheSnapshot(
                    updated,
                    DateTime.UtcNow,
                    OriginWatermark: null,
                    LastSeenAtUtc: null,
                    Source: "unknown")
                : existingSnapshot with { Status = updated };

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

    /// <summary>
    /// Determines whether a status update crosses the online/offline boundary that the
    /// unified attention feed cares about (issue #707, review R3). A previously-uncached
    /// printer is treated as offline (matching the OfflineAttentionSource's "no cache =
    /// offline" rule), so the first online frame resolves and the first offline frame is a
    /// no-op. Returns <c>Created</c> for online→offline (the offline item appears),
    /// <c>Resolved</c> for offline→online (the offline item clears), and <c>null</c> when
    /// the online state is unchanged.
    /// </summary>
    private static AttentionChangeKind? DetectOfflineTransition(PrinterStatusDto? previous, PrinterStatusDto current)
    {
        bool prevOnline = previous?.IsOnline ?? false;
        if (prevOnline == current.IsOnline)
        {
            return null;
        }

        return current.IsOnline ? AttentionChangeKind.Resolved : AttentionChangeKind.Created;
    }

    /// <summary>
    /// Fires a single attention invalidation for an offline/online transition. Item id matches
    /// <see cref="Farm.Infrastructure.Services.Attention.Sources.OfflineAttentionSource"/>
    /// (<c>offline:{printerId}</c>). This is an invalidation hint; the broadcaster is
    /// exception-safe and honours the #725 gate, so the call is fire-and-forget from this
    /// synchronous cache path.
    /// </summary>
    private void EmitOfflineTransition(Guid printerId, AttentionChangeKind? transition)
    {
        if (_attentionBroadcaster is null || transition is not AttentionChangeKind kind)
        {
            return;
        }

        AttentionChangedPayload payload = new(
            AttentionIdPrefixes.Build(AttentionIdPrefixes.Offline, printerId),
            kind,
            DateTime.UtcNow);
        _ = _attentionBroadcaster.NotifyChangedAsync(payload);
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
