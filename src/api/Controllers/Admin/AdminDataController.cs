using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin controller for data export/import and backup/restore operations
/// </summary>
[ApiController]
[Route("api/admin/data")]
[Tags("Admin - Data Management")]
public class AdminDataController : ControllerBase
{
    private readonly IDataExportService _exportService;
    private readonly IDataImportService _importService;
    private readonly IDataSeedService _seedService;
    private readonly IUnifiedLoggingService _logger;

    public AdminDataController(
        IDataExportService exportService,
        IDataImportService importService,
        IDataSeedService seedService,
        IUnifiedLoggingService logger)
    {
        _exportService = exportService;
        _importService = importService;
        _seedService = seedService;
        _logger = logger;
    }

    /// <summary>
    /// Export catalog data (manufacturers, models, components) as JSON
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Catalog export data as JSON</returns>
    /// <response code="200">Returns the catalog export data</response>
    /// <response code="500">If there was an error during export</response>
    [HttpGet("export/catalog")]
    [ProducesResponseType(typeof(CatalogExportDto), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CatalogExportDto>> ExportCatalogAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[AdminData] Catalog export requested");
            CatalogExportDto catalog = await _exportService.ExportCatalogAsync(ct);

            // Set filename header for download
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"catalog-{DateTime.UtcNow:yyyyMMddHHmmss}.json\"";

            return Ok(catalog);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error exporting catalog: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to export catalog", details = ex.Message });
        }
    }

    /// <summary>
    /// Import catalog data (manufacturers, models, components) from JSON
    /// </summary>
    /// <param name="request">Import request containing catalog data and import mode</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import response with statistics and any errors</returns>
    /// <response code="200">Returns the import response with statistics</response>
    /// <response code="400">If the request is invalid</response>
    /// <response code="500">If there was an error during import</response>
    [HttpPost("import/catalog")]
    [ProducesResponseType(typeof(ImportResponseDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ImportResponseDto>> ImportCatalogAsync([FromBody] CatalogImportRequest request, CancellationToken ct)
    {
        try
        {
            if (request?.Catalog == null)
            {
                return BadRequest("Catalog data is required");
            }

            _logger.LogInformation("[AdminData] Catalog import requested in mode: {Mode}", request.Mode.ToString());
            ImportResponseDto response = await _importService.ImportCatalogAsync(request.Catalog, request.Mode, ct);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error importing catalog: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to import catalog", details = ex.Message });
        }
    }

    /// <summary>
    /// Export printer configurations only
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of printer export data</returns>
    /// <response code="200">Returns the printer export data</response>
    /// <response code="500">If there was an error during export</response>
    [HttpGet("export/printers")]
    [ProducesResponseType(typeof(List<PrinterExportDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<List<PrinterExportDto>>> ExportPrintersAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[AdminData] Printers export requested");
            List<PrinterExportDto> printers = await _exportService.ExportPrintersAsync(ct);

            // Set filename header for download
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"printers-{DateTime.UtcNow:yyyyMMddHHmmss}.json\"";

            return Ok(printers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error exporting printers: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to export printers", details = ex.Message });
        }
    }

    /// <summary>
    /// Export full backup (catalog + printers + locations) as JSON
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Full backup export data as JSON</returns>
    /// <response code="200">Returns the full backup export data</response>
    /// <response code="500">If there was an error during export</response>
    [HttpGet("export/full")]
    [ProducesResponseType(typeof(FullBackupExportDto), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<FullBackupExportDto>> ExportFullBackupAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[AdminData] Full backup export requested");
            FullBackupExportDto backup = await _exportService.ExportFullBackupAsync(ct);

            // Set filename header for download
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"backup-full-{DateTime.UtcNow:yyyyMMddHHmmss}.json\"";

            return Ok(backup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error exporting full backup: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to export full backup", details = ex.Message });
        }
    }

    /// <summary>
    /// Import full backup (catalog + printers + locations) from JSON
    /// </summary>
    /// <param name="request">Import request containing full backup data and import mode</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import response with statistics and any errors</returns>
    /// <response code="200">Returns the import response with statistics</response>
    /// <response code="400">If the request is invalid</response>
    /// <response code="500">If there was an error during import</response>
    [HttpPost("import/full")]
    [ProducesResponseType(typeof(ImportResponseDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ImportResponseDto>> ImportFullBackupAsync([FromBody] FullBackupImportRequest request, CancellationToken ct)
    {
        try
        {
            if (request?.Backup == null)
            {
                return BadRequest("Backup data is required");
            }

            _logger.LogInformation("[AdminData] Full backup import requested in mode: {Mode}", request.Mode.ToString());
            ImportResponseDto response = await _importService.ImportFullBackupAsync(request.Backup, request.Mode, ct);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error importing full backup: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to import full backup", details = ex.Message });
        }
    }

    /// <summary>
    /// Reload seed data from YAML files (re-run seeding process)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Success status</returns>
    /// <response code="200">Seed data reload completed successfully</response>
    /// <response code="500">If there was an error during seed reload</response>
    [HttpPost("seed/reload")]
    [ProducesResponseType(200)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> ReloadSeedDataAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[AdminData] Seed data reload requested");
            await _seedService.SeedAllAsync();

            return Ok(new
            {
                success = true,
                message = "Seed data reloaded successfully from YAML files",
                completedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AdminData] Error reloading seed data: {Message}", ex.Message);
            return StatusCode(500, new { error = "Failed to reload seed data", details = ex.Message });
        }
    }
}
