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
        // installation claims can produce two "existing is null" observations,
        // and the second SaveChangesAsync trips the global owner index.
        for (int attempt = 0; attempt < UpsertMaxAttempts; attempt++)
        {
            DeviceToken? existing = await _dbContext.DeviceTokens
                .FirstOrDefaultAsync(
                    t => t.InstallationId == installationId && t.IsActive,
                    cancellationToken);

            if (existing is null)
            {
                var created = new DeviceToken
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
                _dbContext.DeviceTokens.Add(created);
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return created;
                }
                catch (DbUpdateException ex) when (attempt < UpsertMaxAttempts - 1 && IsUniqueDeviceTokenConflict(ex))
                {
                    // Concurrent registration won the race — detach the tracked
                    // ghost entity and retry as an update.
                    _dbContext.Entry(created).State = EntityState.Detached;
                    continue;
                }
            }

            existing.UserId = userId;
            existing.Token = token;
            existing.Platform = platform;
            existing.Environment = environment;
            existing.AppBundleId = appBundleId;
            existing.RegistrationVersion = checked(existing.RegistrationVersion + 1);
            existing.IsActive = true;
            existing.ConsecutiveFailureCount = 0;
            existing.LastUsedAt = nowUtc;
            existing.LastFailureAt = null;
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return existing;
            }
            catch (DbUpdateConcurrencyException) when (attempt < UpsertMaxAttempts - 1)
            {
                _dbContext.Entry(existing).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"DeviceToken upsert failed after retries for userId={userId} installationId={installationId}.");
    }

    internal static bool IsUniqueDeviceTokenConflict(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string indexName = "IX_DeviceTokens_InstallationId";
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
            "DeviceTokens.InstallationId",
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
            .FirstOrDefaultAsync(
                t => t.UserId == userId
                    && t.InstallationId == installationId
                    && t.IsActive,
                cancellationToken);
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
