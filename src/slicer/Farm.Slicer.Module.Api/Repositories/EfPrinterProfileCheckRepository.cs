using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Repositories;

/// <summary>
/// Entity Framework implementation of <see cref="IPrinterProfileCheckRepository"/>.
/// Reads printers directly from the shared <see cref="AppDbContext"/>, mirroring the pattern
/// already used by <see cref="Farm.Slicer.Module.Api.Authorization.PrinterAccessValidator"/>
/// for slicer-host-scoped printer lookups that don't need the full main-API service graph.
/// </summary>
public sealed class EfPrinterProfileCheckRepository(AppDbContext dbContext) : IPrinterProfileCheckRepository
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <inheritdoc />
    public Task<List<Printer>> GetAllAsync(CancellationToken ct) =>
        _dbContext.Printers.AsNoTracking().ToListAsync(ct);

    /// <inheritdoc />
    public async Task<Printer?> FindByTemplateMachineProfileIdsAsync(
        IReadOnlyCollection<Guid> machineProfileIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machineProfileIds);
        if (machineProfileIds.Count == 0)
        {
            return null;
        }

        HashSet<Guid> ids = [.. machineProfileIds];
        return await _dbContext.Printers
            .AsNoTracking()
            .Where(printer =>
                printer.TemplateMachineProfileId != null
                && ids.Contains(printer.TemplateMachineProfileId.Value))
            .OrderBy(printer => printer.Name)
            .FirstOrDefaultAsync(ct);
    }
}
