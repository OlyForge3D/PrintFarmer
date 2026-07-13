using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Entity Framework implementation of <see cref="IToolheadStatisticsRepository"/> (issue #711).
/// </summary>
public class EfToolheadStatisticsRepository : IToolheadStatisticsRepository
{
    private readonly AppDbContext _context;

    public EfToolheadStatisticsRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyDictionary<Guid, double>> GetCumulativeHoursByPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        Dictionary<Guid, double> map = await _context.Toolheads
            .AsNoTracking()
            .Where(t => t.PrinterId == printerId)
            .ToDictionaryAsync(t => t.Id, t => t.CumulativePrintHours, ct);

        return map;
    }

    public async Task<IReadOnlyDictionary<Guid, double>> GetCumulativeHoursByPrintersAsync(IReadOnlyCollection<Guid> printerIds, CancellationToken ct = default)
    {
        if (printerIds.Count == 0)
        {
            return new Dictionary<Guid, double>();
        }

        Dictionary<Guid, double> map = await _context.Toolheads
            .AsNoTracking()
            .Where(t => printerIds.Contains(t.PrinterId))
            .ToDictionaryAsync(t => t.Id, t => t.CumulativePrintHours, ct);

        return map;
    }

    public async Task<double?> GetCumulativeHoursAsync(Guid toolheadId, CancellationToken ct = default)
    {
        Toolhead? toolhead = await _context.Toolheads
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == toolheadId, ct);

        return toolhead?.CumulativePrintHours;
    }

    public async Task<Guid?> IncrementActiveToolheadHoursAsync(Guid printerId, double deltaHours, CancellationToken ct = default)
    {
        // Load the printer's physical toolheads TRACKED so mutations are captured by the
        // shared scoped context's next SaveChanges. MMU/AMS gates are not eligible spool
        // sources for wear attribution.
        List<Toolhead> toolheads = await _context.Toolheads
            .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.Physical)
            .ToListAsync(ct);

        if (toolheads.Count == 0)
        {
            return null;
        }

        // Active toolhead = primary physical, else the lowest-index physical toolhead.
        Toolhead active = toolheads.FirstOrDefault(t => t.IsPrimary)
            ?? toolheads.OrderBy(t => t.Index).First();

        active.CumulativePrintHours += deltaHours;
        active.UpdatedAt = DateTime.UtcNow;

        return active.Id;
    }
}
