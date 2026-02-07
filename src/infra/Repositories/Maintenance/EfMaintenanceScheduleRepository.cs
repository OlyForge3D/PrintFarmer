using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of maintenance schedule repository.
/// </summary>
public class EfMaintenanceScheduleRepository(AppDbContext context) : IMaintenanceScheduleRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    private static string BuildOverrideKey(MaintenanceSchedule schedule)
    {
        return $"{schedule.TaskName}||{schedule.Component ?? string.Empty}";
    }

    public async Task<List<MaintenanceSchedule>> GetActivePrinterSchedulesAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        (Printer printer, PrinterModel model)? printerContext = await GetPrinterContextAsync(printerId, cancellationToken);
        if (printerContext == null)
        {
            return [];
        }

        // Get printer-specific schedules (highest precedence)
        List<MaintenanceSchedule> printerSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == printerId)
            .Include(s => s.Printer)
            .Include(s => s.PrinterModel)
            .ToListAsync(cancellationToken);

        // Get defaults/templates applicable to this printer
        List<MaintenanceSchedule> templateSchedules = await GetTemplateSchedulesForPrinterAsync(printerId, cancellationToken);

        // Merge templates + printer-specific with override behavior
        var byKey = new Dictionary<string, MaintenanceSchedule>(StringComparer.OrdinalIgnoreCase);
        foreach (MaintenanceSchedule schedule in templateSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        foreach (MaintenanceSchedule schedule in printerSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        return byKey.Values
            .OrderBy(s => s.Component)
            .ThenBy(s => s.TaskName)
            .ToList();
    }

    public async Task<List<MaintenanceSchedule>> GetTemplateSchedulesForPrinterAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        (Printer printer, PrinterModel model)? printerContext = await GetPrinterContextAsync(printerId, cancellationToken);
        if (printerContext == null)
        {
            return [];
        }

        Printer printer = printerContext.Value.printer;
        PrinterModel model = printerContext.Value.model;

        int? motionType = model.MotionType;
        Guid manufacturerId = model.ManufacturerId;

        // Model-wide defaults
        List<MaintenanceSchedule> modelSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == printer.ModelId)
            .Include(s => s.PrinterModel)
            .ToListAsync(cancellationToken);

        // Motion-type-wide defaults
        List<MaintenanceSchedule> motionTypeSchedules = motionType == null
            ? []
            : await _context.MaintenanceSchedules
                .AsNoTracking()
                .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.MotionType == motionType)
                .ToListAsync(cancellationToken);

        // Manufacturer-wide defaults
        List<MaintenanceSchedule> manufacturerSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.ManufacturerId == manufacturerId)
            .ToListAsync(cancellationToken);

        // Global defaults
        List<MaintenanceSchedule> globalSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.ManufacturerId == null && s.MotionType == null)
            .ToListAsync(cancellationToken);

        // Merge with increasing precedence: global -> manufacturer -> motionType -> model
        var byKey = new Dictionary<string, MaintenanceSchedule>(StringComparer.OrdinalIgnoreCase);
        foreach (MaintenanceSchedule schedule in globalSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        foreach (MaintenanceSchedule schedule in manufacturerSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        foreach (MaintenanceSchedule schedule in motionTypeSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        foreach (MaintenanceSchedule schedule in modelSchedules)
        {
            byKey[BuildOverrideKey(schedule)] = schedule;
        }

        return byKey.Values
            .OrderBy(s => s.Component)
            .ThenBy(s => s.TaskName)
            .ToList();
    }

    public async Task<Dictionary<Guid, List<MaintenanceSchedule>>> GetTemplateSchedulesForPrintersAsync(
        IReadOnlyCollection<Guid> printerIds,
        CancellationToken cancellationToken = default)
    {
        if (printerIds.Count == 0)
        {
            return new Dictionary<Guid, List<MaintenanceSchedule>>();
        }

        var printers = await _context.Printers
            .AsNoTracking()
            .Where(p => printerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ModelId })
            .ToListAsync(cancellationToken);

        if (printers.Count == 0)
        {
            return new Dictionary<Guid, List<MaintenanceSchedule>>();
        }

        var modelIds = printers.Select(p => p.ModelId).Distinct().ToList();
        var models = await _context.PrinterModels
            .AsNoTracking()
            .Where(m => modelIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ManufacturerId, m.MotionType })
            .ToListAsync(cancellationToken);

        var modelsById = models.ToDictionary(m => m.Id);

        var manufacturerIds = models.Select(m => m.ManufacturerId).Distinct().ToList();
        var motionTypes = models.Where(m => m.MotionType != null).Select(m => m.MotionType!.Value).Distinct().ToList();

        List<MaintenanceSchedule> globalSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.ManufacturerId == null && s.MotionType == null)
            .ToListAsync(cancellationToken);

        List<MaintenanceSchedule> manufacturerSchedules = manufacturerIds.Count == 0
            ? []
            : await _context.MaintenanceSchedules
                .AsNoTracking()
                .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.MotionType == null && s.ManufacturerId != null && manufacturerIds.Contains(s.ManufacturerId.Value))
                .ToListAsync(cancellationToken);

        List<MaintenanceSchedule> motionTypeSchedules = motionTypes.Count == 0
            ? []
            : await _context.MaintenanceSchedules
                .AsNoTracking()
                .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId == null && s.ManufacturerId == null && s.MotionType != null && motionTypes.Contains(s.MotionType.Value))
                .ToListAsync(cancellationToken);

        List<MaintenanceSchedule> modelSchedules = modelIds.Count == 0
            ? []
            : await _context.MaintenanceSchedules
                .AsNoTracking()
                .Where(s => s.IsActive && s.PrinterId == null && s.PrinterModelId != null && modelIds.Contains(s.PrinterModelId.Value))
                .Include(s => s.PrinterModel)
                .ToListAsync(cancellationToken);

        var manufacturerSchedulesById = manufacturerSchedules
            .Where(s => s.ManufacturerId != null)
            .GroupBy(s => s.ManufacturerId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var motionTypeSchedulesByValue = motionTypeSchedules
            .Where(s => s.MotionType != null)
            .GroupBy(s => s.MotionType!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var modelSchedulesById = modelSchedules
            .Where(s => s.PrinterModelId != null)
            .GroupBy(s => s.PrinterModelId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<Guid, List<MaintenanceSchedule>>();
        foreach (var printer in printers)
        {
            if (!modelsById.TryGetValue(printer.ModelId, out var model))
            {
                result[printer.Id] = [];
                continue;
            }

            var byKey = new Dictionary<string, MaintenanceSchedule>(StringComparer.OrdinalIgnoreCase);
            foreach (MaintenanceSchedule schedule in globalSchedules)
            {
                byKey[BuildOverrideKey(schedule)] = schedule;
            }

            if (manufacturerSchedulesById.TryGetValue(model.ManufacturerId, out var manufacturerDefaults))
            {
                foreach (MaintenanceSchedule schedule in manufacturerDefaults)
                {
                    byKey[BuildOverrideKey(schedule)] = schedule;
                }
            }

            if (model.MotionType != null && motionTypeSchedulesByValue.TryGetValue(model.MotionType.Value, out var motionDefaults))
            {
                foreach (MaintenanceSchedule schedule in motionDefaults)
                {
                    byKey[BuildOverrideKey(schedule)] = schedule;
                }
            }

            if (modelSchedulesById.TryGetValue(printer.ModelId, out var modelDefaults))
            {
                foreach (MaintenanceSchedule schedule in modelDefaults)
                {
                    byKey[BuildOverrideKey(schedule)] = schedule;
                }
            }

            result[printer.Id] = byKey.Values
                .OrderBy(s => s.Component)
                .ThenBy(s => s.TaskName)
                .ToList();
        }

        return result;
    }

    public async Task<List<MaintenanceSchedule>> GetPrinterSpecificSchedulesForPrintersAsync(
        IReadOnlyCollection<Guid> printerIds,
        CancellationToken cancellationToken = default)
    {
        if (printerIds.Count == 0)
        {
            return [];
        }

        return await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.PrinterId != null && printerIds.Contains(s.PrinterId.Value))
            .ToListAsync(cancellationToken);
    }

    private async Task<(Printer printer, PrinterModel model)?> GetPrinterContextAsync(Guid printerId, CancellationToken cancellationToken)
    {
        Printer? printer = await _context.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken);

        if (printer == null)
        {
            return null;
        }

        PrinterModel? model = await _context.PrinterModels
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == printer.ModelId, cancellationToken);

        if (model == null)
        {
            return null;
        }

        return (printer, model);
    }

    public async Task<List<MaintenanceSchedule>> GetActiveModelSchedulesAsync(
        Guid printerModelId,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterModelId == printerModelId && s.PrinterId == null)
            .Include(s => s.PrinterModel)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceSchedule?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceSchedules
            .AsNoTracking()
            .Include(s => s.Printer)
            .Include(s => s.PrinterModel)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<List<MaintenanceSchedule>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _context.MaintenanceSchedules
            .AsNoTracking()
            .Include(s => s.Printer)
            .Include(s => s.PrinterModel)
            .OrderBy(s => s.PrinterModelId)
            .ThenBy(s => s.PrinterId)
            .ThenBy(s => s.TaskName)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(MaintenanceSchedule schedule, CancellationToken cancellationToken = default)
    {
        schedule.CreatedAt = DateTime.UtcNow;
        schedule.UpdatedAt = DateTime.UtcNow;
        await _context.MaintenanceSchedules.AddAsync(schedule, cancellationToken);
    }

    public Task UpdateAsync(MaintenanceSchedule schedule, CancellationToken cancellationToken = default)
    {
        schedule.UpdatedAt = DateTime.UtcNow;
        _context.MaintenanceSchedules.Update(schedule);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        MaintenanceSchedule? schedule = await _context.MaintenanceSchedules
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (schedule != null)
        {
            _context.MaintenanceSchedules.Remove(schedule);
        }
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
