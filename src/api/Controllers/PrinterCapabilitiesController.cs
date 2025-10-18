using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages printer capabilities for job queue matching
/// </summary>
[ApiController]
[Route("api/printer-capabilities")]
[Tags("Printer Capabilities")]
public class PrinterCapabilitiesController(Farm.Web.Api.Services.PrinterCapabilities.IPrinterCapabilitiesService svc, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly Farm.Web.Api.Services.PrinterCapabilities.IPrinterCapabilitiesService _svc = svc;
    private readonly IUnifiedLoggingService _logger = logger;

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
            var list = await _svc.GetAllAsync();
            return Ok(list);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error retrieving printer capabilities", null, null, new { }, ex);
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
            var cap = await _svc.GetByPrinterIdAsync(printerId);
            if (cap == null)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            return Ok(cap);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error retrieving capabilities for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
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
            var created = await _svc.CreateAsync(request);
            if (created == null)
            {
                return NotFound($"Printer with ID {request.PrinterId} not found or capabilities already exist");
            }

            return CreatedAtAction(nameof(GetCapabilitiesAsync), new { printerId = request.PrinterId }, created);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error creating capabilities for printer", request.PrinterId.ToString(), null, new { PrinterId = request.PrinterId }, ex);
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
            var res = await _svc.CreateOrUpdateAsync(printerId, request);
            if (res == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error creating/updating capabilities for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
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
            var list = await _svc.GetCompatiblePrintersAsync(gcodeFileId);
            if (list == null)
            {
                return NotFound($"G-code file with ID {gcodeFileId} not found");
            }

            return Ok(list);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error finding compatible printers for G-code file", gcodeFileId.ToString(), null, new { GcodeId = gcodeFileId }, ex);
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
            var deleted = await _svc.DeleteAsync(printerId);
            if (!deleted)
            {
                return NotFound($"Capabilities for printer {printerId} not found");
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error deleting capabilities for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
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
            var (result, isNew) = await _svc.DiscoverAsync(printerId, cancellationToken);
            if (result == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            return isNew ? CreatedAtAction(nameof(GetCapabilitiesAsync), new { printerId }, result) : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error discovering capabilities for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
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
            var result = await _svc.ValidateAsync(printerId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error validating capabilities for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
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
            var res = await _svc.GetModelDefaultsAsync(printerId);
            if (res == null)
            {
                return NotFound($"No model defaults available for printer {printerId}");
            }

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Error, "PrinterCapabilities", "Error getting model defaults for printer", printerId.ToString(), null, new { PrinterId = printerId }, ex);
            return Problem("An error occurred while getting model defaults", statusCode: 500);
        }
    }
}
