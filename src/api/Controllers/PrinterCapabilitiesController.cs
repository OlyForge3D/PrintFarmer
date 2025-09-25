using Farm.Infrastructure;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages printer capabilities for job queue matching
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Printer Capabilities")]
public class PrinterCapabilitiesController(AppDbContext db, IUnifiedLoggingService logger, IPrinterCapabilityDiscoveryService discoveryService) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IPrinterCapabilityDiscoveryService _discoveryService = discoveryService;

    /// <summary>
    /// Get capabilities for all printers
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrinterCapabilitiesDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterCapabilitiesDto>>> GetAllCapabilitiesAsync()
    {
        try
        {
            List<PrinterCapabilities> capabilities = await _db.PrinterCapabilities
                .Include(c => c.Printer)
                .ThenInclude(p => p.Model)
                .ToListAsync();

            return Ok(capabilities.Select(cap => new PrinterCapabilitiesDto(
                Id: cap.Id,
                PrinterId: cap.PrinterId,
                PrinterName: cap.Printer.Name,
                NozzleDiameter: cap.NozzleDiameter,
                SupportedMaterials: cap.SupportedMaterials,
                MaxBuildVolumeX: cap.MaxBuildVolumeX,
                MaxBuildVolumeY: cap.MaxBuildVolumeY,
                MaxBuildVolumeZ: cap.MaxBuildVolumeZ,
                HasHeatedBed: cap.HasHeatedBed,
                HasEnclosure: cap.HasEnclosure,
                MultiMaterial: cap.MultiMaterial,
                NumberOfExtruders: cap.NumberOfExtruders,
                MinHotendTemp: cap.MinHotendTemp,
                MaxHotendTemp: cap.MaxHotendTemp,
                MinBedTemp: cap.MinBedTemp,
                MaxBedTemp: cap.MaxBedTemp,
                CurrentMaterial: cap.CurrentMaterial,
                CurrentSpoolId: cap.CurrentSpoolId,
                IsAvailable: cap.IsAvailable,
                LastUpdated: cap.LastUpdated
            )));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error retrieving printer capabilities: {ex.Message}");
            return Problem("An error occurred while retrieving capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Get capabilities for a specific printer
    /// </summary>
    [HttpGet("printer/{printerId}")]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> GetCapabilitiesAsync(Guid printerId)
    {
        try
        {
            PrinterCapabilities? capabilities = await _db.PrinterCapabilities
                .Include(c => c.Printer)
                .ThenInclude(p => p.Model)
                .FirstOrDefaultAsync(c => c.PrinterId == printerId);

            if (capabilities == null)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            return Ok(new PrinterCapabilitiesDto(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer.Name,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error retrieving capabilities for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while retrieving capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Create new capabilities for a printer
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> CreateCapabilitiesAsync([FromBody] CreatePrinterCapabilitiesDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            // Check if printer exists
            Printer? printer = await _db.Printers.FindAsync(request.PrinterId);
            if (printer == null)
            {
                return NotFound($"Printer with ID {request.PrinterId} not found");
            }

            // Check if capabilities already exist
            PrinterCapabilities? existingCapabilities = await _db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == request.PrinterId);
            if (existingCapabilities != null)
            {
                return Conflict($"Capabilities already exist for printer {request.PrinterId}");
            }

            PrinterCapabilities capabilities = new()
            {
                Id = Guid.NewGuid(),
                PrinterId = request.PrinterId,
                NozzleDiameter = request.NozzleDiameter,
                SupportedMaterials = request.SupportedMaterials,
                MaxBuildVolumeX = request.MaxBuildVolumeX,
                MaxBuildVolumeY = request.MaxBuildVolumeY,
                MaxBuildVolumeZ = request.MaxBuildVolumeZ,
                HasHeatedBed = request.HasHeatedBed,
                HasEnclosure = request.HasEnclosure,
                MultiMaterial = request.MultiMaterial,
                NumberOfExtruders = request.NumberOfExtruders,
                MinHotendTemp = request.MinHotendTemp,
                MaxHotendTemp = request.MaxHotendTemp,
                MinBedTemp = request.MinBedTemp,
                MaxBedTemp = request.MaxBedTemp,
                IsAvailable = true,
                LastUpdated = DateTime.UtcNow
            };

            // Try to fill in missing values with auto-discovered defaults
            if (!request.MaxBuildVolumeX.HasValue || !request.MaxBuildVolumeY.HasValue || !request.MaxBuildVolumeZ.HasValue ||
                !request.NozzleDiameter.HasValue || !request.MaxHotendTemp.HasValue)
            {
                try
                {
                    PrinterCapabilities? defaults = await _discoveryService.GetModelDefaultCapabilitiesAsync(printer);
                    if (defaults != null)
                    {
                        capabilities.MaxBuildVolumeX ??= defaults.MaxBuildVolumeX;
                        capabilities.MaxBuildVolumeY ??= defaults.MaxBuildVolumeY;
                        capabilities.MaxBuildVolumeZ ??= defaults.MaxBuildVolumeZ;
                        capabilities.NozzleDiameter ??= defaults.NozzleDiameter;
                        capabilities.MaxHotendTemp ??= defaults.MaxHotendTemp;
                        capabilities.MaxBedTemp ??= defaults.MaxBedTemp;
                        capabilities.MinHotendTemp ??= defaults.MinHotendTemp;
                        capabilities.MinBedTemp ??= defaults.MinBedTemp;
                        capabilities.SupportedMaterials ??= defaults.SupportedMaterials;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to apply model defaults for printer {request.PrinterId}: {ex.Message}");
                }
            }

            _db.PrinterCapabilities.Add(capabilities);
            await _db.SaveChangesAsync();

            // Reload to get printer name
            await _db.Entry(capabilities).Reference(c => c.Printer).LoadAsync();

            PrinterCapabilitiesDto result = new(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer.Name,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            );

            return CreatedAtAction(nameof(GetCapabilitiesAsync), new { printerId = request.PrinterId }, result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error creating capabilities for printer {request.PrinterId}: {ex.Message}");
            return Problem("An error occurred while creating capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Create or update capabilities for a printer
    /// </summary>
    [HttpPut("printer/{printerId}")]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> CreateOrUpdateCapabilitiesAsync(Guid printerId, [FromBody] UpdatePrinterCapabilitiesDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            // Check if printer exists
            Printer? printer = await _db.Printers.FindAsync(printerId);
            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            PrinterCapabilities? capabilities = await _db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == printerId);

            if (capabilities == null)
            {
                // Create new capabilities
                capabilities = new PrinterCapabilities
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    NozzleDiameter = request.NozzleDiameter,
                    SupportedMaterials = request.SupportedMaterials,
                    MaxBuildVolumeX = request.MaxBuildVolumeX,
                    MaxBuildVolumeY = request.MaxBuildVolumeY,
                    MaxBuildVolumeZ = request.MaxBuildVolumeZ,
                    HasHeatedBed = request.HasHeatedBed,
                    HasEnclosure = request.HasEnclosure,
                    MultiMaterial = request.MultiMaterial,
                    NumberOfExtruders = request.NumberOfExtruders,
                    MinHotendTemp = request.MinHotendTemp,
                    MaxHotendTemp = request.MaxHotendTemp,
                    MinBedTemp = request.MinBedTemp,
                    MaxBedTemp = request.MaxBedTemp,
                    IsAvailable = true,
                    LastUpdated = DateTime.UtcNow
                };

                _db.PrinterCapabilities.Add(capabilities);
            }
            else
            {
                // Update existing capabilities
                capabilities.NozzleDiameter = request.NozzleDiameter;
                capabilities.SupportedMaterials = request.SupportedMaterials;
                capabilities.MaxBuildVolumeX = request.MaxBuildVolumeX;
                capabilities.MaxBuildVolumeY = request.MaxBuildVolumeY;
                capabilities.MaxBuildVolumeZ = request.MaxBuildVolumeZ;
                capabilities.HasHeatedBed = request.HasHeatedBed;
                capabilities.HasEnclosure = request.HasEnclosure;
                capabilities.MultiMaterial = request.MultiMaterial;
                capabilities.NumberOfExtruders = request.NumberOfExtruders;
                capabilities.MinHotendTemp = request.MinHotendTemp;
                capabilities.MaxHotendTemp = request.MaxHotendTemp;
                capabilities.MinBedTemp = request.MinBedTemp;
                capabilities.MaxBedTemp = request.MaxBedTemp;
                capabilities.LastUpdated = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            // Reload to get printer name
            await _db.Entry(capabilities).Reference(c => c.Printer).LoadAsync();

            PrinterCapabilitiesDto result = new(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer.Name,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            );

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error creating/updating capabilities for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while creating/updating capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Get printers that match G-code file requirements
    /// </summary>
    [HttpGet("compatible/{gcodeFileId}")]
    [ProducesResponseType(typeof(IEnumerable<PrinterDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterDto>>> GetCompatiblePrintersAsync(Guid gcodeFileId)
    {
        try
        {
            GcodeFile? gcodeFile = await _db.GcodeFiles.FindAsync(gcodeFileId);
            if (gcodeFile == null)
            {
                return NotFound($"G-code file with ID {gcodeFileId} not found");
            }

            List<PrinterCapabilities> allPrinters = await _db.PrinterCapabilities
                .Include(c => c.Printer)
                .Where(c => c.IsAvailable)
                .ToListAsync();

            List<PrinterDto> compatiblePrinters = new();

            foreach (PrinterCapabilities? cap in allPrinters)
            {
                bool isCompatible = true;

                // Check nozzle diameter
                if (gcodeFile.RequiredNozzleDiameter.HasValue && cap.NozzleDiameter.HasValue &&
                    Math.Abs(cap.NozzleDiameter.Value - gcodeFile.RequiredNozzleDiameter.Value) > 0.001)
                {
                    isCompatible = false;
                }

                // Check material compatibility
                if (!string.IsNullOrEmpty(gcodeFile.RequiredMaterial) && cap.SupportedMaterials != null &&
                    !cap.SupportedMaterials.Contains(gcodeFile.RequiredMaterial))
                {
                    isCompatible = false;
                }

                // Check build volume
                if (gcodeFile.RequiredBuildVolumeX.HasValue && cap.MaxBuildVolumeX.HasValue &&
                    gcodeFile.RequiredBuildVolumeX.Value > cap.MaxBuildVolumeX.Value)
                {
                    isCompatible = false;
                }

                if (gcodeFile.RequiredBuildVolumeY.HasValue && cap.MaxBuildVolumeY.HasValue &&
                    gcodeFile.RequiredBuildVolumeY.Value > cap.MaxBuildVolumeY.Value)
                {
                    isCompatible = false;
                }

                if (gcodeFile.RequiredBuildVolumeZ.HasValue && cap.MaxBuildVolumeZ.HasValue &&
                    gcodeFile.RequiredBuildVolumeZ.Value > cap.MaxBuildVolumeZ.Value)
                {
                    isCompatible = false;
                }

                if (isCompatible)
                {
                    PrinterDto printerDto = new(
                        Id: cap.Printer.Id,
                        Name: cap.Printer.Name,
                        ServerUrl: cap.Printer.ServerUrl,
                        Notes: cap.Printer.Notes,
                        IsOnline: false, // We don't have live status in this context
                        State: null, // We don't have live status in this context
                        ManufacturerName: cap.Printer.Manufacturer?.Name,
                        ModelName: cap.Printer.Model?.Name,
                        Progress: null,
                        JobName: null,
                        ThumbnailUrl: null,
                        CameraStreamUrl: null,
                        CameraSnapshotUrl: null,
                        X: null,
                        Y: null,
                        Z: null,
                        HotendTemp: null,
                        BedTemp: null,
                        HotendTarget: null,
                        BedTarget: null,
                        Backend: (Farm.Web.Shared.PrinterBackend)cap.Printer.Backend,
                        ApiKey: cap.Printer.ApiKey,
                        OriginalServerUrl: cap.Printer.OriginalServerUrl,
                        IpAddress: cap.Printer.IpAddress,
                        SpoolInfo: null
                    );
                    compatiblePrinters.Add(printerDto);
                }
            }

            return Ok(compatiblePrinters);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error finding compatible printers for G-code file {gcodeFileId}: {ex.Message}");
            return Problem("An error occurred while finding compatible printers", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete capabilities for a printer
    /// </summary>
    [HttpDelete("printer/{printerId}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteCapabilitiesAsync(Guid printerId)
    {
        try
        {
            PrinterCapabilities? capabilities = await _db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == printerId);

            if (capabilities == null)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            _db.PrinterCapabilities.Remove(capabilities);
            await _db.SaveChangesAsync();

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error deleting capabilities for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while deleting capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Auto-discover capabilities for a printer from its API and model defaults
    /// </summary>
    [HttpPost("discover/{printerId}")]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 201)]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> DiscoverCapabilitiesAsync(Guid printerId, CancellationToken cancellationToken = default)
    {
        try
        {
            Printer? printer = await _db.Printers
                .Include(p => p.Model)
                .Include(p => p.Manufacturer)
                .FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken);

            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            // Check if capabilities already exist
            PrinterCapabilities? existingCapabilities = await _db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == printerId, cancellationToken);

            PrinterCapabilities capabilities;
            bool isNewCapabilities = false;

            if (existingCapabilities != null)
            {
                // Refresh existing capabilities
                capabilities = await _discoveryService.RefreshCapabilitiesAsync(existingCapabilities, printer, cancellationToken);
            }
            else
            {
                // Discover new capabilities
                PrinterCapabilities? discoveredCapabilities = await _discoveryService.DiscoverCapabilitiesAsync(printer, cancellationToken);
                if (discoveredCapabilities == null)
                {
                    return Problem("Failed to discover capabilities for the printer", statusCode: 500);
                }
                capabilities = discoveredCapabilities;
                _db.PrinterCapabilities.Add(capabilities);
                isNewCapabilities = true;
            }

            await _db.SaveChangesAsync(cancellationToken);

            // Load printer name for response
            await _db.Entry(capabilities).Reference(c => c.Printer).LoadAsync(cancellationToken);

            PrinterCapabilitiesDto result = new(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer.Name,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            );

            if (isNewCapabilities)
            {
                return CreatedAtAction(nameof(GetCapabilitiesAsync), new { printerId }, result);
            }
            else
            {
                return Ok(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error discovering capabilities for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while discovering capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Validate capabilities against printer model specifications
    /// </summary>
    [HttpPost("validate/{printerId}")]
    [ProducesResponseType(typeof(CapabilityValidationResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CapabilityValidationResult>> ValidateCapabilitiesAsync(Guid printerId)
    {
        try
        {
            Printer? printer = await _db.Printers
                .Include(p => p.Model)
                .Include(p => p.Manufacturer)
                .FirstOrDefaultAsync(p => p.Id == printerId);

            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            PrinterCapabilities? capabilities = await _db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == printerId);

            if (capabilities == null)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            CapabilityValidationResult validationResult = await _discoveryService.ValidateCapabilitiesAsync(capabilities, printer);
            return Ok(validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error validating capabilities for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while validating capabilities", statusCode: 500);
        }
    }

    /// <summary>
    /// Get model default capabilities for a printer
    /// </summary>
    [HttpGet("defaults/{printerId}")]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> GetModelDefaultsAsync(Guid printerId)
    {
        try
        {
            Printer? printer = await _db.Printers
                .Include(p => p.Model)
                .Include(p => p.Manufacturer)
                .FirstOrDefaultAsync(p => p.Id == printerId);

            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            PrinterCapabilities? defaults = await _discoveryService.GetModelDefaultCapabilitiesAsync(printer);
            if (defaults == null)
            {
                return NotFound($"No model defaults available for printer {printerId}");
            }

            PrinterCapabilitiesDto result = new(
                Id: defaults.Id,
                PrinterId: defaults.PrinterId,
                PrinterName: printer.Name,
                NozzleDiameter: defaults.NozzleDiameter,
                SupportedMaterials: defaults.SupportedMaterials,
                MaxBuildVolumeX: defaults.MaxBuildVolumeX,
                MaxBuildVolumeY: defaults.MaxBuildVolumeY,
                MaxBuildVolumeZ: defaults.MaxBuildVolumeZ,
                HasHeatedBed: defaults.HasHeatedBed,
                HasEnclosure: defaults.HasEnclosure,
                MultiMaterial: defaults.MultiMaterial,
                NumberOfExtruders: defaults.NumberOfExtruders,
                MinHotendTemp: defaults.MinHotendTemp,
                MaxHotendTemp: defaults.MaxHotendTemp,
                MinBedTemp: defaults.MinBedTemp,
                MaxBedTemp: defaults.MaxBedTemp,
                CurrentMaterial: defaults.CurrentMaterial,
                CurrentSpoolId: defaults.CurrentSpoolId,
                IsAvailable: defaults.IsAvailable,
                LastUpdated: defaults.LastUpdated
            );

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting model defaults for printer {printerId}: {ex.Message}");
            return Problem("An error occurred while getting model defaults", statusCode: 500);
        }
    }
}
