using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
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
public class PrinterCapabilitiesController(AppDbContext db, ILogger<PrinterCapabilitiesController> logger) : ControllerBase
{
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
            var capabilities = await db.PrinterCapabilities
                .Include(c => c.Printer)
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
            logger.LogError(ex, "Error retrieving printer capabilities");
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
            var capabilities = await db.PrinterCapabilities
                .Include(c => c.Printer)
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
            logger.LogError(ex, "Error retrieving capabilities for printer {PrinterId}", printerId);
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
            var printer = await db.Printers.FindAsync(request.PrinterId);
            if (printer == null)
            {
                return NotFound($"Printer with ID {request.PrinterId} not found");
            }

            // Check if capabilities already exist
            var existingCapabilities = await db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == request.PrinterId);
            if (existingCapabilities != null)
            {
                return Conflict($"Capabilities already exist for printer {request.PrinterId}");
            }

            var capabilities = new PrinterCapabilities
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

            db.PrinterCapabilities.Add(capabilities);
            await db.SaveChangesAsync();

            // Reload to get printer name
            await db.Entry(capabilities).Reference(c => c.Printer).LoadAsync();

            var result = new PrinterCapabilitiesDto(
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
            logger.LogError(ex, "Error creating capabilities for printer {PrinterId}", request.PrinterId);
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
            var printer = await db.Printers.FindAsync(printerId);
            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            var capabilities = await db.PrinterCapabilities
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

                db.PrinterCapabilities.Add(capabilities);
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

            await db.SaveChangesAsync();

            // Reload to get printer name
            await db.Entry(capabilities).Reference(c => c.Printer).LoadAsync();

            var result = new PrinterCapabilitiesDto(
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
            logger.LogError(ex, "Error creating/updating capabilities for printer {PrinterId}", printerId);
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
            var gcodeFile = await db.GcodeFiles.FindAsync(gcodeFileId);
            if (gcodeFile == null)
            {
                return NotFound($"G-code file with ID {gcodeFileId} not found");
            }

            var allPrinters = await db.PrinterCapabilities
                .Include(c => c.Printer)
                .Where(c => c.IsAvailable)
                .ToListAsync();

            var compatiblePrinters = new List<PrinterDto>();

            foreach (var cap in allPrinters)
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
                    var printerDto = new PrinterDto(
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
            logger.LogError(ex, "Error finding compatible printers for G-code file {FileId}", gcodeFileId);
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
            var capabilities = await db.PrinterCapabilities
                .FirstOrDefaultAsync(c => c.PrinterId == printerId);

            if (capabilities == null)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            db.PrinterCapabilities.Remove(capabilities);
            await db.SaveChangesAsync();

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting capabilities for printer {PrinterId}", printerId);
            return Problem("An error occurred while deleting capabilities", statusCode: 500);
        }
    }
}
