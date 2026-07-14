using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Notifications;

/// <summary>
/// EF Core implementation of <see cref="IDeviceTokenRepository"/>. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class EfDeviceTokenRepository(AppDbContext dbContext) : IDeviceTokenRepository
{
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
        for (int attempt = 0; attempt < 3; attempt++)
        {
            DeviceToken? existing = await _dbContext.DeviceTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.InstallationId == installationId, cancellationToken);

            if (existing is null)
            {
                var created = new DeviceToken
                {
                    UserId = userId,
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
                catch (DbUpdateException) when (attempt < 2)
                {
                    // Concurrent registration won the race — detach the tracked
                    // ghost entity and retry as an update.
                    _dbContext.Entry(created).State = EntityState.Detached;
                    continue;
                }
            }

            existing.Token = token;
            existing.Platform = platform;
            existing.Environment = environment;
            existing.AppBundleId = appBundleId;
            existing.IsActive = true;
            existing.ConsecutiveFailureCount = 0;
            existing.LastUsedAt = nowUtc;
            existing.LastFailureAt = null;
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return existing;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                _dbContext.Entry(existing).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"DeviceToken upsert failed after retries for userId={userId} installationId={installationId}.");
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
            .ToListAsync(cancellationToken);
        return owners;
    }

    /// <inheritdoc />
    public async Task RecordSuccessAsync(Guid deviceTokenId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        DeviceToken? row = await _dbContext.DeviceTokens.FirstOrDefaultAsync(t => t.Id == deviceTokenId, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.LastUsedAt = nowUtc;
        row.ConsecutiveFailureCount = 0;
        row.LastFailureAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(Guid deviceTokenId, DateTime nowUtc, int failureThreshold, CancellationToken cancellationToken = default)
    {
        DeviceToken? row = await _dbContext.DeviceTokens.FirstOrDefaultAsync(t => t.Id == deviceTokenId, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.LastFailureAt = nowUtc;
        row.ConsecutiveFailureCount = checked(row.ConsecutiveFailureCount + 1);
        if (failureThreshold > 0 && row.ConsecutiveFailureCount >= failureThreshold)
        {
            row.IsActive = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> InvalidateByTokenAsync(string providerToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerToken))
        {
            return 0;
        }

        List<DeviceToken> matches = await _dbContext.DeviceTokens
            .Where(t => t.Token == providerToken)
            .ToListAsync(cancellationToken);
        if (matches.Count == 0)
        {
            return 0;
        }

        _dbContext.DeviceTokens.RemoveRange(matches);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return matches.Count;
    }
}
