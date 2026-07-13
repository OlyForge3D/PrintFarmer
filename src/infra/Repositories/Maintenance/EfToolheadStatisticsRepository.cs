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

    public async Task<IReadOnlyDictionary<int, Guid>> GetPhysicalToolheadIdsByIndexAsync(
        Guid printerId,
        CancellationToken ct = default)
    {
        return await _context.Toolheads
            .AsNoTracking()
            .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.Physical)
            .ToDictionaryAsync(t => t.Index, t => t.Id, ct);
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
        ToolheadHourAttribution attribution = ToolheadHourAttribution.EqualSplit(
            [.. toolheads.Select(t => t.Id)],
            deltaHours);
        return ApplyToAll(toolheads, attribution);
    }

    public async Task<IReadOnlyList<Guid>> ApplyToolheadHoursAsync(Guid printerId, ToolheadHourAttribution attribution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);

        // Only physical toolheads with a positive attributed weight are eligible; MMU/AMS gates are
        // never wear sources (issue #711, round-7 Finding 3).
        HashSet<Guid> wanted = [.. attribution.Hours.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key)];
        if (wanted.Count == 0)
        {
            return [];
        }

        List<Toolhead> toolheads = await _context.Toolheads
            .Where(t => t.PrinterId == printerId
                && t.ToolheadType == ToolheadType.Physical
                && wanted.Contains(t.Id))
            .ToListAsync(ct);

        return toolheads.Count == 0 ? [] : ApplyToAll(toolheads, attribution);
    }

    private static List<Guid> ApplyToAll(List<Toolhead> toolheads, ToolheadHourAttribution attribution)
    {
        DateTime updatedAt = DateTime.UtcNow;
        List<Guid> credited = [];
        foreach (Toolhead toolhead in toolheads)
        {
            if (!attribution.Hours.TryGetValue(toolhead.Id, out double hours) || hours <= 0)
            {
                continue;
            }

            toolhead.CumulativePrintHours += hours;
            toolhead.UpdatedAt = updatedAt;
            credited.Add(toolhead.Id);
        }

        return credited;
    }
}
