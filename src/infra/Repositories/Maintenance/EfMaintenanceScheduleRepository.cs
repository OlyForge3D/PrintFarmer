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

    public async Task<List<MaintenanceSchedule>> GetActivePrinterSchedulesAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        // Get the printer to determine its model
        Printer? printer = await _context.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken);

        if (printer == null)
        {
            return [];
        }

        // Get printer-specific schedules
        List<MaintenanceSchedule> printerSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterId == printerId)
            .Include(s => s.Printer)
            .Include(s => s.PrinterModel)
            .ToListAsync(cancellationToken);

        // Get model-wide default schedules
        List<MaintenanceSchedule> modelSchedules = await _context.MaintenanceSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && s.PrinterModelId == printer.ModelId && s.PrinterId == null)
            .Include(s => s.PrinterModel)
            .ToListAsync(cancellationToken);

        // Combine and return (printer-specific overrides model-wide)
        return printerSchedules.Concat(modelSchedules).ToList();
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
