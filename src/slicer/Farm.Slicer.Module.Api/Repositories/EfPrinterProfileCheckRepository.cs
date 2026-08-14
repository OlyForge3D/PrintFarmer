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
}
