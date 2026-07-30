using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Infrastructure.Services.Mutations;

/// <summary>
/// Reads the latest committed global mutation watermark.
/// </summary>
public interface IMutationWatermarkReader
{
    /// <summary>
    /// Returns the latest committed task mutation sequence.
    /// </summary>
    Task<long> GetCurrentAsync(CancellationToken ct = default);
}

/// <summary>
/// Resolves a fresh database scope for each watermark capture.
/// </summary>
public sealed class MutationWatermarkReader(IServiceScopeFactory scopeFactory) : IMutationWatermarkReader
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public async Task<long> GetCurrentAsync(CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MutationCounters
            .AsNoTracking()
            .Where(counter => counter.Id == MutationCounter.GlobalId)
            .Select(counter => counter.Value)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }
}
