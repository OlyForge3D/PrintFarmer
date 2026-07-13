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

    public async Task<IReadOnlyList<Guid>> IncrementActiveToolheadHoursAsync(Guid printerId, double deltaHours, CancellationToken ct = default)
    {
        // Load the printer's physical toolheads TRACKED so mutations are captured by the
        // shared scoped context's next SaveChanges. MMU/AMS gates are not eligible spool
        // sources for wear attribution.
        List<Toolhead> toolheads = await _context.Toolheads
            .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.Physical)
            .ToListAsync(ct);

        if (toolheads.Count == 0 || deltaHours <= 0)
        {
            return [];
        }

        // Until per-job tool telemetry is available, equal utilization is the conservative
        // estimate: secondary physical heads accrue wear instead of being permanently ignored,
        // while the printer-wide delta is not multiplied across the toolheads.
        double perToolheadDelta = deltaHours / toolheads.Count;
        DateTime updatedAt = DateTime.UtcNow;
        foreach (Toolhead toolhead in toolheads)
        {
            toolhead.CumulativePrintHours += perToolheadDelta;
            toolhead.UpdatedAt = updatedAt;
        }

        return [.. toolheads.Select(t => t.Id)];
    }
}
