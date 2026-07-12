using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Printers;

/// <summary>
/// EF Core implementation of <see cref="IFilamentSwapOverrideRepository"/>. Uses the shared
/// <see cref="AppDbContext"/> supplied by the unit of work so staged inserts commit in the
/// same transaction as the spool binding (issue #710, B6).
/// </summary>
public sealed class EfFilamentSwapOverrideRepository(AppDbContext db) : IFilamentSwapOverrideRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public void Add(FilamentSwapOverride auditRecord)
    {
        ArgumentNullException.ThrowIfNull(auditRecord);
        _ = _db.Set<FilamentSwapOverride>().Add(auditRecord);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilamentSwapOverride>> GetByPrinterAsync(Guid printerId, CancellationToken ct)
    {
        return await _db.Set<FilamentSwapOverride>()
            .AsNoTracking()
            .Where(o => o.PrinterId == printerId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
