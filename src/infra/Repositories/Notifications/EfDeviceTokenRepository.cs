using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Farm.Infrastructure.Repositories.Notifications;

/// <summary>
/// EF Core implementation of <see cref="IDeviceTokenRepository"/>. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class EfDeviceTokenRepository(AppDbContext dbContext) : IDeviceTokenRepository
{
    private const int UpsertMaxAttempts = 3;
    private const long InitialRegistrationVersion = 1;

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <inheritdoc />
    public async Task<DeviceToken> UpsertAsync(
        Guid userId,
        string installationId,
        string token,
        string platform,
        string environment,
        string? appBundleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        DateTime nowUtc = DateTime.UtcNow;

        // Retry loop for concurrent-registration TOCTOU: a race between two
        // (userId, installationId) upserts can produce two "existing is null"
        // observations, and the second SaveChangesAsync trips the unique index.
        // The same loop also retries the ownership-transfer deactivation below
        // when two different accounts race to claim the same installation.
        for (int attempt = 0; attempt < UpsertMaxAttempts; attempt++)
        {
            DeviceToken? existing = await _dbContext.DeviceTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.InstallationId == installationId, cancellationToken);

            DeviceToken current;
            if (existing is null)
            {
                current = new DeviceToken
                {
                    UserId = userId,
                    RegistrationVersion = InitialRegistrationVersion,
                    InstallationId = installationId,
                    Token = token,
                    Platform = platform,
                    Environment = environment,
                    AppBundleId = appBundleId,
                    CreatedAt = nowUtc,
                    LastUsedAt = nowUtc,
                    ConsecutiveFailureCount = 0,
                    IsActive = true,
                };
                _dbContext.DeviceTokens.Add(current);
            }
            else
            {
                current = existing;
                current.Token = token;
                current.Platform = platform;
                current.Environment = environment;
                current.AppBundleId = appBundleId;
                current.RegistrationVersion = checked(current.RegistrationVersion + 1);
                current.IsActive = true;
                current.ConsecutiveFailureCount = 0;
                current.LastUsedAt = nowUtc;
                current.LastFailureAt = null;
            }

            // Ownership transfer (issue #705): the mobile installation id is
            // persisted in UserDefaults and survives logout, so a failed or
            // never-sent unregister call can leave a previous account's row
            // active for this exact installation. Any other account's active
            // row for the same installation is therefore stale — the physical
            // device/APNs token it references is now controlled by userId —
            // so it is deactivated atomically with this upsert. Without this,
            // both accounts would stay "active" for the same installation and
            // push content addressed to the previous account could still be
            // delivered to (and displayed on) this device.
            List<DeviceToken> priorOwnerRows = await _dbContext.DeviceTokens
                .Where(t => t.InstallationId == installationId && t.UserId != userId && t.IsActive)
                .ToListAsync(cancellationToken);
            foreach (DeviceToken priorOwnerRow in priorOwnerRows)
            {
                priorOwnerRow.IsActive = false;

                // Rotate the surrendered row's version too so an in-flight
                // provider outcome for the previous account's incarnation
                // cannot resurrect it (see RegistrationVersion remarks).
                priorOwnerRow.RegistrationVersion = checked(priorOwnerRow.RegistrationVersion + 1);
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return current;
            }
            catch (DbUpdateException ex) when (attempt < UpsertMaxAttempts - 1
                && (ex is DbUpdateConcurrencyException || IsUniqueDeviceTokenConflict(ex)))
            {
                // Concurrent registration (unique-index conflict) or a
                // concurrent ownership transfer (concurrency-token conflict on
                // a prior owner's row) won the race — detach everything this
                // attempt touched and retry from a fresh read.
                DetachTrackedDeviceTokens();
            }
        }

        throw new InvalidOperationException(
            $"DeviceToken upsert failed after retries for userId={userId} installationId={installationId}.");
    }

    private void DetachTrackedDeviceTokens()
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries<DeviceToken>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    internal static bool IsUniqueDeviceTokenConflict(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string indexName = "IX_DeviceTokens_UserId_InstallationId";
        return exception.InnerException switch
        {
            SqliteException sqlite =>
                sqlite.SqliteErrorCode == 19
                && sqlite.SqliteExtendedErrorCode == 2067
                && IsExactSqliteUpsertKey(sqlite.Message),
            PostgresException postgres =>
                postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal),
            SqlException sqlServer =>
                sqlServer.Number is 2601 or 2627
                && NamesDelimitedSqlServerIndex(sqlServer.Message, indexName),
            _ => false,
        };
    }

    private static bool IsExactSqliteUpsertKey(string message)
    {
        const string sqliteMarker = "UNIQUE constraint failed:";
        int markerIndex = message.IndexOf(sqliteMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        string columns = message[(markerIndex + sqliteMarker.Length)..]
            .Trim()
            .Trim('\'', '"', '.');
        return string.Equals(
            columns,
            "DeviceTokens.UserId, DeviceTokens.InstallationId",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesDelimitedSqlServerIndex(string message, string indexName)
    {
        return message.Contains($"'{indexName}'", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"\"{indexName}\"", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"[{indexName}]", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteByInstallationAsync(Guid userId, string installationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            return false;
        }

        DeviceToken? existing = await _dbContext.DeviceTokens
            .FirstOrDefaultAsync(t => t.UserId == userId && t.InstallationId == installationId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        _dbContext.DeviceTokens.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceToken>> GetActiveByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        List<DeviceToken> rows = await _dbContext.DeviceTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.IsActive)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetActiveTokenOwnersAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> owners = await _dbContext.DeviceTokens
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToListAsync(cancellationToken);
        return owners;
    }

    /// <inheritdoc />
    public async Task RecordSuccessAsync(
        Guid deviceTokenId,
        long registrationVersion,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        _ = await _dbContext.DeviceTokens
            .Where(token => token.Id == deviceTokenId && token.RegistrationVersion == registrationVersion)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.LastUsedAt, nowUtc)
                    .SetProperty(token => token.ConsecutiveFailureCount, 0)
                    .SetProperty(token => token.LastFailureAt, (DateTime?)null),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(
        Guid deviceTokenId,
        long registrationVersion,
        DateTime nowUtc,
        int failureThreshold,
        CancellationToken cancellationToken = default)
    {
        _ = await _dbContext.DeviceTokens
            .Where(token => token.Id == deviceTokenId && token.RegistrationVersion == registrationVersion)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.LastFailureAt, nowUtc)
                    .SetProperty(
                        token => token.IsActive,
                        token => failureThreshold > 0
                            && token.ConsecutiveFailureCount >= failureThreshold - 1
                                ? false
                                : token.IsActive)
                    .SetProperty(
                        token => token.ConsecutiveFailureCount,
                        token => token.ConsecutiveFailureCount + 1),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> InvalidateAsync(
        Guid deviceTokenId,
        long registrationVersion,
        CancellationToken cancellationToken = default)
    {
        int deleted = await _dbContext.DeviceTokens
            .Where(token => token.Id == deviceTokenId && token.RegistrationVersion == registrationVersion)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }
}
