using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Models.Admin;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for exporting database data to JSON format for backup/restore functionality
/// </summary>
public class DataExportService : IDataExportService
{
    private readonly AppDbContext _context;
    private readonly IUnifiedLoggingService _logger;

    public DataExportService(
        AppDbContext context,
        IUnifiedLoggingService logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<CatalogExportDto> ExportCatalogAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[DataExport] Starting catalog export");

        try
        {
            CatalogExportDto catalog = new()
            {
                Manufacturers = await ExportManufacturersAsync(ct),
                FilamentTypes = await ExportFilamentTypesAsync(ct),
                PrinterModels = await ExportPrinterModelsAsync(ct),
                Hotends = await ExportHotendsAsync(ct),
                Extruders = await ExportExtrudersAsync(ct),
                Toolheads = await ExportToolheadsAsync(ct),
                Nozzles = await ExportNozzlesAsync(ct),
                ExportedAt = DateTime.UtcNow,
                Version = "1.0"
            };

            _logger.LogInformation("[DataExport] Catalog export complete");

            return catalog;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DataExport] Error exporting catalog: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<List<PrinterExportDto>> ExportPrintersAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[DataExport] Starting printers export");

        try
        {
            List<Printer> printers = await _context.Printers
                .Include(p => p.Model)
                .Include(p => p.Location)
                .ToListAsync(ct);

            List<PrinterExportDto> exportData = printers.Select(p => new PrinterExportDto
            {
                Id = p.Id,
                Name = p.Name,
                ServerUrl = p.ServerUrl,
                OriginalServerUrl = p.OriginalServerUrl,
                BackendPort = p.BackendPort,
                FrontendPort = p.FrontendPort,
                ModelName = p.Model?.Name,
                LocationName = p.Location?.Name,
                Backend = p.Backend,
                IsAvailable = p.IsAvailable
            }).ToList();

            _logger.LogInformation($"[DataExport] Printers export complete: {exportData.Count} printers");
            return exportData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DataExport] Error exporting printers: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<FullBackupExportDto> ExportFullBackupAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[DataExport] Starting full backup export");

        try
        {
            FullBackupExportDto backup = new()
            {
                Catalog = await ExportCatalogAsync(ct),
                Printers = await ExportPrintersAsync(ct),
                Locations = await ExportLocationsAsync(ct),
                ExportedAt = DateTime.UtcNow,
                Version = "1.0"
            };

            _logger.LogInformation("[DataExport] Full backup export complete");
            return backup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DataExport] Error exporting full backup: {Message}", ex.Message);
            throw;
        }
    }

    private async Task<List<ManufacturerExportDto>> ExportManufacturersAsync(CancellationToken ct)
    {
        List<Manufacturer> manufacturers = await _context.Manufacturers.ToListAsync(ct);

        return manufacturers.Select(m => new ManufacturerExportDto
        {
            Id = m.Id,
            Name = m.Name,
            Url = m.Url,
            Description = m.Description,
            IsActive = m.IsActive
        }).ToList();
    }

    private async Task<List<FilamentTypeExportDto>> ExportFilamentTypesAsync(CancellationToken ct)
    {
        List<FilamentType> filamentTypes = await _context.FilamentTypes.ToListAsync(ct);

        return filamentTypes.Select(f => new FilamentTypeExportDto
        {
            Id = f.Id,
            Name = f.Name,
            DefaultHotendTemp = f.DefaultHotendTemp.HasValue ? (int)Math.Round(f.DefaultHotendTemp.Value) : 0,
            DefaultBedTemp = f.DefaultBedTemp.HasValue ? (int)Math.Round(f.DefaultBedTemp.Value) : 0,
            IsAbrasive = f.IsAbrasive,
            NeedsEnclosure = f.NeedsEnclosure
        }).ToList();
    }

    private async Task<List<PrinterModelExportDto>> ExportPrinterModelsAsync(CancellationToken ct)
    {
        List<PrinterModel> printerModels = await _context.PrinterModels
            .Include(pm => pm.Manufacturer)
            .Include(pm => pm.SupportedFilamentTypes).ThenInclude(sft => sft.FilamentType)
            .ToListAsync(ct);

        return printerModels.Select(pm => new PrinterModelExportDto
        {
            Id = pm.Id,
            Name = pm.Name,
            ManufacturerName = pm.Manufacturer?.Name ?? "Unknown",
            MotionType = pm.MotionType,
            MaxX = pm.MaxX,
            MaxY = pm.MaxY,
            MaxZ = pm.MaxZ,
            DefaultBackend = pm.DefaultBackend,
            HasHeatedBed = pm.HasHeatedBed,
            HasEnclosure = pm.HasEnclosure,
            MultiMaterial = pm.MultiMaterial,
            NumberOfExtruders = pm.NumberOfExtruders,
            SupportsAutoLeveling = pm.SupportsAutoLeveling,
            MaxBedTemp = pm.MaxBedTemp,
            MaxPrintSpeed = pm.MaxPrintSpeed,
            SupportedFilamentTypes = pm.SupportedFilamentTypes
                .Select(sft => sft.FilamentType?.Name ?? "Unknown")
                .ToList(),
            IsActive = pm.IsActive
        }).ToList();
    }

    private async Task<List<HotendModelExportDto>> ExportHotendsAsync(CancellationToken ct)
    {
        List<HotendModelDefinition> hotends = await _context.HotendModelDefinitions
            .Include(h => h.Manufacturer)
            .ToListAsync(ct);

        return hotends.Select(h => new HotendModelExportDto
        {
            Id = h.Id,
            Name = h.Name,
            ManufacturerName = h.Manufacturer?.Name ?? "Unknown",
            MaxTemp = h.MaxTemp ?? 0,
            IsHighFlow = h.IsHighFlow,
            Description = h.Description,
            Url = h.Url
        }).ToList();
    }

    private async Task<List<ExtruderModelExportDto>> ExportExtrudersAsync(CancellationToken ct)
    {
        List<ExtruderModelDefinition> extruders = await _context.ExtruderModelDefinitions
            .Include(e => e.Manufacturer)
            .ToListAsync(ct);

        return extruders.Select(e => new ExtruderModelExportDto
        {
            Id = e.Id,
            Name = e.Name,
            ManufacturerName = e.Manufacturer?.Name ?? "Unknown",
            GearRatio = e.GearRatio,
            IsDirectDrive = e.IsDirectDrive,
            Description = e.Description
        }).ToList();
    }

    private async Task<List<ToolheadModelExportDto>> ExportToolheadsAsync(CancellationToken ct)
    {
        List<ToolheadModelDefinition> toolheads = await _context.ToolheadModelDefinitions
            .Include(t => t.Manufacturer)
            .ToListAsync(ct);

        return toolheads.Select(t => new ToolheadModelExportDto
        {
            Id = t.Id,
            Name = t.Name,
            ManufacturerName = t.Manufacturer?.Name ?? "Unknown",
            Description = t.Description
        }).ToList();
    }

    private async Task<List<NozzleModelExportDto>> ExportNozzlesAsync(CancellationToken ct)
    {
        List<NozzleModelDefinition> nozzles = await _context.NozzleModelDefinitions
            .Include(n => n.Manufacturer)
            .ToListAsync(ct);

        return nozzles.Select(n => new NozzleModelExportDto
        {
            Id = n.Id,
            Name = n.Name,
            ManufacturerName = n.Manufacturer?.Name ?? "Unknown",
            Diameter = n.Diameter,
            MaxTemp = n.MaxTemp,
            NozzleInterface = (int)n.NozzleInterface,
            Description = n.Description
        }).ToList();
    }

    private async Task<List<LocationExportDto>> ExportLocationsAsync(CancellationToken ct)
    {
        List<Location> locations = await _context.Locations.ToListAsync(ct);

        return locations.Select(l => new LocationExportDto
        {
            Id = l.Id,
            Name = l.Name,
            Description = l.Description
        }).ToList();
    }
}
