using System.Text.Json;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.ServerIdentity;

/// <summary>
/// EF-Core-backed <see cref="IServerIdentityService"/>. Persists the generated
/// <c>serverId</c> in the existing generic <see cref="AppSettingsEntity"/> table under
/// <see cref="SettingsKey"/> rather than introducing a dedicated table/migration — this
/// value is a generated identity, not a user-configurable setting, so it deliberately
/// bypasses <c>SettingsService</c>/<c>[AppSetting]</c> discovery.
/// </summary>
/// <remarks>
/// Registered as a singleton (matching <see cref="NativePush.NativePushDispatcher"/>,
/// which resolves it once per dispatch) and caches the resolved value in memory after
/// the first successful read/generate so later calls — including the dispatcher's
/// per-device send loop — never hit the database again. A first-run race between two
/// concurrent callers is resolved by retrying as an update when the unique index on
/// <c>Key</c> rejects a duplicate insert, so exactly one generated value wins.
/// </remarks>
public sealed class ServerIdentityService(IDbContextFactory<AppDbContext> dbContextFactory) : IServerIdentityService
{
    /// <summary>The <c>AppSettingsEntity.Key</c> this identity is persisted under.</summary>
    public const string SettingsKey = "ServerIdentity";

    private const int UpsertMaxAttempts = 3;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Guid? _cachedServerId;

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateServerIdAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedServerId is Guid cached)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedServerId is Guid cachedAfterWait)
            {
                return cachedAfterWait;
            }

            Guid resolved = await ResolveOrCreateAsync(cancellationToken).ConfigureAwait(false);
            _cachedServerId = resolved;
            return resolved;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    private async Task<Guid> ResolveOrCreateAsync(CancellationToken cancellationToken)
    {
        await using AppDbContext db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        for (int attempt = 0; attempt < UpsertMaxAttempts; attempt++)
        {
            AppSettingsEntity? existing = await db.AppSettingsEntities
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Key == SettingsKey, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null && TryParseServerId(existing.SettingsJson, out Guid persisted))
            {
                return persisted;
            }

            // Either no row yet, or a corrupt row we cannot trust as a stable identity —
            // generate once and attempt to persist it. Never regenerate a value once a
            // valid persisted identity has been read above.
            Guid generated = Guid.NewGuid();
            var payload = new ServerIdentityPayload(generated);
            string json = JsonSerializer.Serialize(payload);

            if (existing is null)
            {
                db.AppSettingsEntities.Add(new AppSettingsEntity
                {
                    Key = SettingsKey,
                    SettingsJson = json,
                    UpdatedAt = DateTime.UtcNow,
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return generated;
                }
                catch (DbUpdateException) when (attempt < UpsertMaxAttempts - 1)
                {
                    // Another process/request won the race and inserted first — detach
                    // our speculative row and re-read on the next attempt.
                    foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in db.ChangeTracker.Entries())
                    {
                        entry.State = EntityState.Detached;
                    }

                    continue;
                }
            }

            // A row exists but its JSON did not parse to a canonical serverId — repair it
            // in place using a fresh, single generated value.
            AppSettingsEntity toRepair = await db.AppSettingsEntities
                .FirstAsync(e => e.Key == SettingsKey, cancellationToken)
                .ConfigureAwait(false);

            if (TryParseServerId(toRepair.SettingsJson, out Guid repairedConcurrently))
            {
                // Another caller repaired this exact row between our first read and this
                // tracked re-fetch — trust its value instead of blindly overwriting it.
                return repairedConcurrently;
            }

            toRepair.SettingsJson = json;
            toRepair.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return generated;
            }
            catch (DbUpdateException) when (attempt < UpsertMaxAttempts - 1)
            {
                // Another process/request repaired or replaced this row concurrently
                // (RowVersion conflict) — detach and re-read on the next attempt rather
                // than forcing our speculative value.
                foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in db.ChangeTracker.Entries())
                {
                    entry.State = EntityState.Detached;
                }

                continue;
            }
        }

        throw new InvalidOperationException(
            $"Failed to resolve or create the server identity after {UpsertMaxAttempts} attempts.");
    }

    private static bool TryParseServerId(string? settingsJson, out Guid serverId)
    {
        serverId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return false;
        }

        try
        {
            ServerIdentityPayload? payload = JsonSerializer.Deserialize<ServerIdentityPayload>(settingsJson);
            if (payload is null || payload.ServerId == Guid.Empty)
            {
                return false;
            }

            serverId = payload.ServerId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ServerIdentityPayload(Guid ServerId);
}
