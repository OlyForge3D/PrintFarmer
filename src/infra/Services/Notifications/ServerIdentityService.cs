using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Resolves the stable, opaque server-instance identity (<c>serverId</c>) used to bind
/// native-push payloads and registration/metadata responses to the PrintFarmer server that
/// generated them. See issue #1407.
/// </summary>
public interface IServerIdentityService
{
    /// <summary>
    /// Returns the persisted server identity, generating and durably storing one on first
    /// call for a fresh install. The value never changes for the lifetime of the underlying
    /// database — restarts, config reloads, token rotation, and individual sends all observe
    /// the same identity.
    /// </summary>
    Task<string> GetServerIdAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists the server identity in the generic <see cref="AppSettingsEntity"/> key-value
/// store (key <see cref="SettingsKey"/>) so no dedicated migration is required. Generation is
/// idempotent and race-safe: a fresh install with no row present generates and inserts a new
/// canonical UUID; if two concurrent callers race to insert (e.g. two requests immediately
/// after a fresh deploy), the loser observes a unique-index violation and re-reads the
/// winner's committed row rather than overwriting it. The resolved value is then cached
/// in-process so subsequent calls never hit the database again for the life of the process.
/// </summary>
public sealed class ServerIdentityService : IServerIdentityService, IDisposable
{
    /// <summary>Key under which the server identity is stored in <see cref="AppSettingsEntity"/>.</summary>
    public const string SettingsKey = "ServerIdentity";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServerIdentityService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedServerId;
    private bool _disposed;

    /// <summary>Constructs the service.</summary>
    public ServerIdentityService(IServiceScopeFactory scopeFactory, ILogger<ServerIdentityService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> GetServerIdAsync(CancellationToken cancellationToken = default)
    {
        string? fastPathId = Volatile.Read(ref _cachedServerId);
        if (fastPathId is not null)
        {
            return fastPathId;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-checked locking: re-read via Volatile.Read rather than a plain field
            // access. Another caller may have populated the cache while this call waited on
            // the semaphore; a plain re-read of the same field here is otherwise (correctly)
            // provable as still-null from this thread's own sequential view and gets flagged
            // as a dead/constant condition by static analysis that doesn't model the
            // concurrent writer — Volatile.Read makes the "value may have changed underneath
            // us" intent explicit for both readers and analyzers.
            string? lockedPathId = Volatile.Read(ref _cachedServerId);
            if (lockedPathId is not null)
            {
                return lockedPathId;
            }

            using IServiceScope scope = _scopeFactory.CreateScope();
            IAppSettingsRepository settings = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

            AppSettingsEntity? existing = await settings.GetReadOnlyAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null && TryParseServerId(existing.SettingsJson, out string existingId))
            {
                Volatile.Write(ref _cachedServerId, existingId);
                return existingId;
            }

            string newServerId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            string json = JsonSerializer.Serialize(new ServerIdentityRecord(newServerId));

            // Unconditional atomic insert-only attempt: no existence check precedes this
            // write, so the unique index on AppSettingsEntity.Key is the sole arbiter of
            // "who generated the identity first." This avoids the check-then-act race that
            // SetAsync's internal read-then-upsert would otherwise introduce, where a second
            // concurrent caller could observe "no row yet" and later silently overwrite the
            // winner's already-committed identity via an update instead of an insert.
            bool inserted = await settings.TryInsertIfAbsentAsync(SettingsKey, json, cancellationToken).ConfigureAwait(false);
            if (inserted)
            {
                Volatile.Write(ref _cachedServerId, newServerId);
                return newServerId;
            }

            // Lost the race with another concurrent caller inserting the same key. Re-read
            // the now-committed row rather than retry the write — the identity must never be
            // regenerated once any instance has durably recorded one.
            AppSettingsEntity? afterRace = await settings.GetReadOnlyAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
            if (afterRace is not null && TryParseServerId(afterRace.SettingsJson, out string raceWinnerId))
            {
                Volatile.Write(ref _cachedServerId, raceWinnerId);
                return raceWinnerId;
            }

            _logger.LogError("[ServerIdentity] Concurrent identity generation failed and no committed row could be re-read.");
            throw new InvalidOperationException("Server identity generation lost a concurrent race but no committed row could be re-read.");
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    private static bool TryParseServerId(string json, out string serverId)
    {
        serverId = string.Empty;
        try
        {
            ServerIdentityRecord? record = JsonSerializer.Deserialize<ServerIdentityRecord>(json);
            if (record is not null && NativePushRegistrationContract.IsCanonicalOriginServerId(record.ServerId))
            {
                serverId = record.ServerId;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ServerIdentityRecord(string ServerId);
}
