using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for integrating with Spoolman filament management system.
/// </summary>
[ApiController]
[Route("api/spoolman")]
[Tags("Spoolman Integration")]
[Authorize]
public class SpoolmanController(
    ISpoolmanService spoolman,
    ISettingsService settingsService,
    ILogger<SpoolmanController> logger) : ControllerBase
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<SpoolmanController> _logger = logger;

    /// <summary>
    /// Tests connectivity to an arbitrary Spoolman base URL without persisting configuration.
    /// Used by the setup wizard before saving settings. Always returns 200 with success flag.
    /// </summary>
    /// <param name="request">Request containing the candidate BaseUrl.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON object { success, normalizedUrl?, endpointTried?, statusCode?, version?, message? }</returns>
    /// <response code="200">Returns probe result (success may be true/false)</response>
    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> TestAsync([FromBody] SpoolmanConfigDto? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Ok(new { success = false, message = "BaseUrl is required" });
        }

        SpoolmanProbeResult probe = await spoolman.ProbeAsync(request.BaseUrl, ct);
        return Ok(new { success = probe.Success, normalizedUrl = probe.NormalizedUrl, endpointTried = probe.EndpointTried, statusCode = probe.StatusCode, version = probe.Version, message = probe.Message, errorCategory = probe.ErrorCategory });
    }

    // Note: Exception categorization was moved into the SpoolmanService Probe implementation.

    /// <summary>
    /// Gets the current Spoolman integration configuration.
    /// </summary>
    /// <returns>Current Spoolman configuration including server URL and connection settings</returns>
    /// <response code="200">Returns the current Spoolman configuration</response>
    [HttpGet("config")]
    [ProducesResponseType(typeof(SpoolmanConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<SpoolmanConfigDto?> GetConfig() => spoolman.GetConfig();

    /// <summary>
    /// Updates the Spoolman integration configuration.
    /// </summary>
    /// <param name="config">New Spoolman configuration settings</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the configuration was successfully updated</response>
    /// <response code="400">If the configuration data is invalid</response>
    [HttpPost("config")]
    [Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SetConfig([FromBody] SpoolmanConfigDto? config)
    {
        // Extra logging for 401 diagnostics
        System.Security.Claims.ClaimsPrincipal user = HttpContext.User;
        if (user.Identity == null || !user.Identity.IsAuthenticated)
        {
            _logger.LogWarning("[SpoolmanController] SetConfig: User is not authenticated. Claims: {Claims}", string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")));
        }
        else
        {
            string? name = user.Identity != null ? user.Identity.Name : "(null)";
            _logger.LogInformation("[SpoolmanController] SetConfig: Authenticated user: {Name}. Claims: {Claims}", name, string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")));
        }

        if (config is null)
        {
            return BadRequest("Config body is required.");
        }

        spoolman.SetConfig(config);
        return NoContent();
    }

    /// <summary>
    /// Gets a paginated, filtered, and sorted list of spools from the connected Spoolman server.
    /// </summary>
    /// <param name="limit">Maximum number of spools per page.</param>
    /// <param name="offset">Offset into the full result set.</param>
    /// <param name="sort">Sort expression, e.g. "filament.name:asc".</param>
    /// <param name="search">Partial search term applied to filament name.</param>
    /// <param name="material">Filter by filament material.</param>
    /// <param name="vendor">Filter by vendor name.</param>
    /// <param name="location">Filter by spool location.</param>
    /// <param name="allowArchived">Whether to include archived spools.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Paginated result containing spools and total count.</returns>
    /// <response code="200">Returns the paginated list of spools from Spoolman</response>
    /// <response code="503">If Spoolman is not configured or unavailable</response>
    [HttpGet("spools")]
    [ProducesResponseType(typeof(SpoolmanPagedResult<SpoolmanSpoolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SpoolmanPagedResult<SpoolmanSpoolDto>>> GetSpoolsAsync(
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromQuery] string? sort,
        [FromQuery] string? search,
        [FromQuery] string? material,
        [FromQuery] string? vendor,
        [FromQuery] string? location,
        [FromQuery] bool? allowArchived,
        CancellationToken ct)
    {
        if (limit.HasValue && (limit.Value < 1 || limit.Value > 500))
        {
            return BadRequest(new { message = "limit must be between 1 and 500." });
        }

        if (offset.HasValue && offset.Value < 0)
        {
            return BadRequest(new { message = "offset must be non-negative." });
        }

        SpoolmanSpoolQueryParams queryParams = new()
        {
            Limit = limit,
            Offset = offset,
            Sort = sort,
            Search = search,
            Material = material,
            Vendor = vendor,
            Location = location,
            AllowArchived = allowArchived,
        };

        return Ok(await spoolman.ListSpoolsAsync(queryParams, ct));
    }

    /// <summary>
    /// Returns distinct material, vendor, and location values across all spools.
    /// Used to populate filter dropdowns without relying on paginated data.
    /// </summary>
    [HttpGet("filter-options")]
    [ProducesResponseType(typeof(SpoolFilterOptionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SpoolFilterOptionsDto>> GetFilterOptionsAsync(CancellationToken ct)
    {
        return Ok(await spoolman.GetFilterOptionsAsync(ct));
    }

    /// <summary>
    /// Creates a new spool in Spoolman.
    /// </summary>
    /// <param name="request">Spool data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The created spool</returns>
    /// <response code="201">Returns the created spool</response>
    /// <response code="400">If the creation fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("spools")]
    [ProducesResponseType(typeof(SpoolmanSpoolDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanSpoolDto>> CreateSpoolAsync(
        [FromBody] SpoolmanSpoolRequest request,
        CancellationToken ct)
    {
        if (request?.FilamentId is null or <= 0)
        {
            return BadRequest(new { message = "FilamentId is required" });
        }

        try
        {
            SpoolmanSpoolDto result = await spoolman.CreateSpoolInSpoolmanAsync(request, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating spool: {Message}", ex.Message);
            return BadRequest(new { message = "Create failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// Updates a single spool in Spoolman by its ID.
    /// Only non-null fields in the request body are applied (PATCH semantics).
    /// </summary>
    /// <param name="id">Spool ID in Spoolman</param>
    /// <param name="request">Fields to update</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated spool</returns>
    /// <response code="200">Returns the updated spool</response>
    /// <response code="400">If the update fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPatch("spools/{id:int}")]
    [ProducesResponseType(typeof(SpoolmanSpoolDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanSpoolDto>> UpdateSpoolAsync(
        int id,
        [FromBody] SpoolmanSpoolRequest request,
        CancellationToken ct)
    {
        try
        {
            SpoolmanSpoolDto result = await spoolman.UpdateSpoolInSpoolmanAsync(id, request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating spool {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = "Update failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// Deletes a single spool from Spoolman by its ID.
    /// </summary>
    /// <param name="id">Spool ID in Spoolman</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="204">Spool deleted successfully</response>
    /// <response code="400">If the delete fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("spools/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteSpoolAsync(int id, CancellationToken ct)
    {
        try
        {
            await spoolman.DeleteSpoolFromSpoolmanAsync(id, ct);
            return NoContent();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Spool {Id} not found in Spoolman", id);
            return NotFound(new { message = $"Spool {id} not found." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting spool {Id}", id);
            return BadRequest(new { message = "Delete failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// Bulk-updates multiple spools in Spoolman.
    /// Only non-null fields in the request are applied to all specified spools.
    /// </summary>
    /// <param name="request">Bulk update request with spool IDs and fields to update</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Bulk update result with success/error counts</returns>
    /// <response code="200">Returns the bulk update result</response>
    /// <response code="400">If the request is invalid</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPatch("spools/bulk")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> BulkUpdateSpoolsAsync(
        [FromBody] SpoolmanBulkUpdateSpoolsRequest request,
        CancellationToken ct)
    {
        if (request?.SpoolIds is not { Length: > 0 })
        {
            return BadRequest(new { message = "No spool IDs provided" });
        }

        if (request.SpoolIds.Length > 100)
        {
            return BadRequest(new { message = "Cannot process more than 100 spools at once." });
        }

        try
        {
            SpoolmanBulkUpdateResult result = await spoolman.BulkUpdateSpoolsAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-updating spools");
            return BadRequest(new { message = "Bulk update failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// Bulk-deletes multiple spools from Spoolman.
    /// Accepts a JSON body with an array of spool IDs.
    /// </summary>
    /// <param name="request">Object containing spoolIds array</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Returns the bulk delete result</response>
    /// <response code="400">If the request is invalid</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("spools/bulk")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> BulkDeleteSpoolsAsync(
        [FromBody] SpoolmanBulkDeleteSpoolsRequest request,
        CancellationToken ct)
    {
        if (request?.SpoolIds is not { Length: > 0 })
        {
            return BadRequest(new { message = "No spool IDs provided" });
        }

        if (request.SpoolIds.Length > 100)
        {
            return BadRequest(new { message = "Cannot process more than 100 spools at once." });
        }

        try
        {
            SpoolmanBulkUpdateResult result = await spoolman.BulkDeleteSpoolsAsync(request.SpoolIds, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-deleting spools");
            return BadRequest(new { message = "Bulk delete failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// Imports spools from an uploaded CSV file into Spoolman.
    /// Creates or updates spools based on ID matching. Requires a FilamentId column for new spools.
    /// </summary>
    /// <param name="file">CSV file with spool data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import result with counts</returns>
    /// <response code="200">Returns import result</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("spools/import")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> ImportSpoolsCsvAsync(
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided" });
        }

        try
        {
            using StreamReader reader = new(file.OpenReadStream(), Encoding.UTF8);
            string? headerLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return Ok(new SpoolmanBulkUpdateResult(0, 1, ["CSV file is empty or missing header row"]));
            }

            string[] headers = ParseCsvLine(headerLine);
            Dictionary<string, int> headerMap = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                headerMap[headers[i].Trim()] = i;
            }

            bool hasId = headerMap.ContainsKey("Id");
            bool hasFilamentId = headerMap.ContainsKey("FilamentId") || headerMap.ContainsKey("filament_id");
            if (!hasId && !hasFilamentId)
            {
                return Ok(new SpoolmanBulkUpdateResult(0, 1, ["CSV must contain at least an 'Id' or 'FilamentId' column"]));
            }

            int imported = 0;
            int errorCount = 0;
            List<string> errors = [];
            int rowNum = 0;

            string remaining = await reader.ReadToEndAsync(ct);
            List<string> records = SplitCsvRecords(remaining);

            foreach (string record in records)
            {
                if (string.IsNullOrWhiteSpace(record))
                {
                    continue;
                }

                rowNum++;

                try
                {
                    string[] values = ParseCsvLine(record);

                    SpoolmanSpoolRequest req = new()
                    {
                        FilamentId = ParseIntOrNull(GetCsvValue(values, headerMap, "FilamentId"))
                                  ?? ParseIntOrNull(GetCsvValue(values, headerMap, "filament_id")),
                        RemainingWeight = ParseDoubleOrNull(GetCsvValue(values, headerMap, "RemainingWeightG"))
                                       ?? ParseDoubleOrNull(GetCsvValue(values, headerMap, "RemainingWeight")),
                        InitialWeight = ParseDoubleOrNull(GetCsvValue(values, headerMap, "InitialWeightG"))
                                     ?? ParseDoubleOrNull(GetCsvValue(values, headerMap, "InitialWeight")),
                        SpoolWeight = ParseDoubleOrNull(GetCsvValue(values, headerMap, "SpoolWeightG"))
                                   ?? ParseDoubleOrNull(GetCsvValue(values, headerMap, "SpoolWeight")),
                        Price = ParseDoubleOrNull(GetCsvValue(values, headerMap, "Price")),
                        Location = NullIfEmpty(GetCsvValue(values, headerMap, "Location")),
                        LotNumber = NullIfEmpty(GetCsvValue(values, headerMap, "LotNumber"))
                                 ?? NullIfEmpty(GetCsvValue(values, headerMap, "lot_number")),
                        Comment = NullIfEmpty(GetCsvValue(values, headerMap, "Comment")),
                        Archived = ParseBoolOrNull(GetCsvValue(values, headerMap, "Archived")),
                    };

                    string idStr = GetCsvValue(values, headerMap, "Id");
                    if (int.TryParse(idStr, out int existingId) && existingId > 0)
                    {
                        await spoolman.UpdateSpoolInSpoolmanAsync(existingId, req, ct);
                    }
                    else
                    {
                        if (req.FilamentId is null or <= 0)
                        {
                            errors.Add($"Row {rowNum}: FilamentId is required for new spools");
                            errorCount++;
                            continue;
                        }

                        await spoolman.CreateSpoolInSpoolmanAsync(req, ct);
                    }

                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Row {rowNum}: {ex.Message}");
                    errorCount++;
                }
            }

            return Ok(new SpoolmanBulkUpdateResult(imported, errorCount, [.. errors]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing Spoolman spools from CSV: {Message}", ex.Message);
            return BadRequest(new { message = $"Import failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets all filament types (product definitions) from the connected Spoolman server.
    /// Filaments represent the product class (e.g., "PolyTerra PLA Charcoal Black"),
    /// while spools represent physical instances.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament types from Spoolman</returns>
    /// <response code="200">Returns the list of filament types</response>
    [HttpGet("filaments")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanFilamentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SpoolmanFilamentDto>>> GetFilamentsAsync(CancellationToken ct)
        => Ok(await spoolman.ListFilamentsAsync(ct));

    /// <summary>
    /// Gets all vendors from the connected Spoolman server.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all vendors from Spoolman</returns>
    /// <response code="200">Returns the list of vendors</response>
    [HttpGet("vendors")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanVendorDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SpoolmanVendorDto>>> GetVendorsAsync(CancellationToken ct)
        => Ok(await spoolman.ListVendorsAsync(ct));

    /// <summary>
    /// Gets all material types from the connected Spoolman server (e.g. PLA, PETG, ASA).
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all material definitions from Spoolman</returns>
    /// <response code="200">Returns the list of materials</response>
    [HttpGet("materials")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanMaterialDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SpoolmanMaterialDto>>> GetMaterialsAsync(CancellationToken ct)
        => Ok(await spoolman.ListMaterialsAsync(ct));

    /// <summary>
    /// Gets material names that have at least one non-archived spool with remaining filament.
    /// Used by the spool picker to show only materials the user can actually select from.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Sorted list of material name strings</returns>
    /// <response code="200">Returns distinct material names with available spools</response>
    [HttpGet("materials/available")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<string>>> GetAvailableMaterialsAsync(CancellationToken ct)
    {
        IReadOnlyList<string> materials = await spoolman.GetAvailableMaterialsAsync(ct);
        return Ok(materials);
    }

    /// <summary>
    /// Performs a lightweight health probe against the configured Spoolman instance.
    /// Returns basic status information (success flag and optional message) without enumerating all spools.
    /// </summary>
    /// <returns>Health status for Spoolman integration</returns>
    /// <response code="200">Health probe executed (Success may be true/false)</response>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> HealthAsync(CancellationToken ct)
    {
        SpoolmanProbeResult probe = await spoolman.HealthProbeAsync(ct);
        if (!probe.Success)
        {
            return Ok(new { configured = true, success = false, message = probe.Message });
        }

        return Ok(new { configured = true, success = true, endpoint = probe.EndpointTried, statusCode = probe.StatusCode });
    }

    /// <summary>
    /// Clears the Spoolman configuration.
    /// </summary>
    /// <returns>No content</returns>
    [HttpDelete("config")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearConfig()
    {
        try
        {
            spoolman.ClearConfig();
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/config (DELETE): {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a new filament in Spoolman.
    /// </summary>
    /// <param name="request">Filament data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The created filament</returns>
    /// <response code="201">Returns the created filament</response>
    /// <response code="400">If the creation fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("filaments")]
    [ProducesResponseType(typeof(SpoolmanFilamentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanFilamentDto>> CreateFilamentAsync(
        [FromBody] SpoolmanCreateFilamentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { message = "Name is required" });
        }

        try
        {
            SpoolmanFilamentDto result = await spoolman.CreateFilamentInSpoolmanAsync(request, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating filament: {Message}", ex.Message);
            return BadRequest(new { message = $"Create failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Bulk-updates multiple filaments in Spoolman.
    /// Only non-null fields in the request are applied to all specified filaments.
    /// Useful for batch-setting vendor, price, material, or temperatures.
    /// </summary>
    /// <param name="request">Bulk update request with filament IDs and fields to update</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Bulk update result with success/error counts</returns>
    /// <response code="200">Returns the bulk update result</response>
    /// <response code="400">If the request is invalid</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPatch("filaments/bulk")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> BulkUpdateFilamentsAsync(
        [FromBody] SpoolmanBulkUpdateFilamentsRequest request,
        CancellationToken ct)
    {
        if (request?.FilamentIds is not { Length: > 0 })
        {
            return BadRequest(new { message = "No filament IDs provided" });
        }

        try
        {
            SpoolmanBulkUpdateResult result = await spoolman.BulkUpdateFilamentsAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-updating filaments: {Message}", ex.Message);
            return BadRequest(new { message = $"Bulk update failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates a single filament in Spoolman by its ID.
    /// Only non-null fields in the request body are applied (PATCH semantics).
    /// </summary>
    /// <param name="id">Filament ID in Spoolman</param>
    /// <param name="request">Fields to update</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated filament</returns>
    /// <response code="200">Returns the updated filament</response>
    /// <response code="400">If the update fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPatch("filaments/{id:int}")]
    [ProducesResponseType(typeof(SpoolmanFilamentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanFilamentDto>> UpdateFilamentAsync(
        int id,
        [FromBody] SpoolmanCreateFilamentRequest request,
        CancellationToken ct)
    {
        try
        {
            SpoolmanFilamentDto result = await spoolman.UpdateFilamentInSpoolmanAsync(id, request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating filament {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = $"Update failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Deletes a single filament from Spoolman by its ID.
    /// </summary>
    /// <param name="id">Filament ID in Spoolman</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="204">Filament deleted successfully</response>
    /// <response code="400">If the delete fails</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("filaments/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteFilamentAsync(int id, CancellationToken ct)
    {
        try
        {
            await spoolman.DeleteFilamentFromSpoolmanAsync(id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting filament {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = $"Delete failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Bulk-deletes multiple filaments from Spoolman.
    /// Accepts a JSON body with an array of filament IDs.
    /// </summary>
    /// <param name="request">Object containing filamentIds array</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Returns the bulk delete result</response>
    /// <response code="400">If the request is invalid</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("filaments/bulk")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> BulkDeleteFilamentsAsync(
        [FromBody] SpoolmanBulkDeleteRequest request,
        CancellationToken ct)
    {
        if (request?.FilamentIds is not { Length: > 0 })
        {
            return BadRequest(new { message = "No filament IDs provided" });
        }

        try
        {
            SpoolmanBulkUpdateResult result = await spoolman.BulkDeleteFilamentsAsync(request.FilamentIds, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-deleting filaments: {Message}", ex.Message);
            return BadRequest(new { message = $"Bulk delete failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Exports all Spoolman filaments as a CSV file download.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>CSV file with all Spoolman filaments</returns>
    /// <response code="200">Returns CSV file</response>
    [HttpGet("filaments/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportFilamentsCsvAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<SpoolmanFilamentDto> filaments = await spoolman.ListFilamentsAsync(ct);

            StringBuilder sb = new();
            sb.AppendLine("Id,Name,Vendor,Material,ColorHex,Density,Diameter,Weight,SpoolWeight,Price,ExtruderTemp,BedTemp,ArticleNumber,Comment");

            foreach (SpoolmanFilamentDto f in filaments.OrderBy(f => f.Vendor).ThenBy(f => f.Name))
            {
                sb.Append(CsvEscape(f.Id.ToString(CultureInfo.InvariantCulture)));
                sb.Append(',');
                sb.Append(CsvEscape(f.Name));
                sb.Append(',');
                sb.Append(CsvEscape(f.Vendor));
                sb.Append(',');
                sb.Append(CsvEscape(f.Material));
                sb.Append(',');
                sb.Append(CsvEscape(f.ColorHex));
                sb.Append(',');
                sb.Append(f.Density?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.Diameter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.Weight?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.SpoolWeight?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.Price?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.SettingsExtruderTemp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(f.SettingsBedTemp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(',');
                sb.Append(CsvEscape(f.ArticleNumber));
                sb.Append(',');
                sb.AppendLine(CsvEscape(f.Comment));
            }

            byte[] csv = Encoding.UTF8.GetBytes(sb.ToString());
            return File(csv, "text/csv", "spoolman-filaments.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting Spoolman filaments to CSV: {Message}", ex.Message);
            return BadRequest(new { message = $"Export failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Imports filaments from an uploaded CSV file into Spoolman.
    /// Creates or updates filaments based on ID matching. Resolves vendors by name, creating new ones as needed.
    /// </summary>
    /// <param name="file">CSV file with filament data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import result with counts</returns>
    /// <response code="200">Returns import result</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("filaments/import")]
    [ProducesResponseType(typeof(SpoolmanBulkUpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SpoolmanBulkUpdateResult>> ImportFilamentsCsvAsync(
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided" });
        }

        try
        {
            // Load existing vendors for name resolution
            IReadOnlyList<SpoolmanVendorDto> existingVendors = await spoolman.ListVendorsAsync(ct);
            Dictionary<string, SpoolmanVendorDto> vendorByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (SpoolmanVendorDto v in existingVendors)
            {
                vendorByName.TryAdd(v.Name, v);
            }

            using StreamReader reader = new(file.OpenReadStream(), Encoding.UTF8);
            string? headerLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return Ok(new SpoolmanBulkUpdateResult(0, 1, ["CSV file is empty or missing header row"]));
            }

            string[] headers = ParseCsvLine(headerLine);
            Dictionary<string, int> headerMap = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                headerMap[headers[i].Trim()] = i;
            }

            if (!headerMap.ContainsKey("Name"))
            {
                return Ok(new SpoolmanBulkUpdateResult(0, 1, ["CSV must contain a 'Name' column"]));
            }

            int created = 0;
            int errorCount = 0;
            List<string> errors = [];
            int rowNum = 0;

            // Read all remaining content and split into logical CSV records.
            // We can't use ReadLineAsync per-record because quoted fields may
            // contain embedded newlines (e.g. a Comment with line breaks).
            string remaining = await reader.ReadToEndAsync(ct);
            List<string> records = SplitCsvRecords(remaining);

            foreach (string record in records)
            {
                if (string.IsNullOrWhiteSpace(record))
                {
                    continue;
                }

                rowNum++;

                try
                {
                    string[] values = ParseCsvLine(record);
                    string name = GetCsvValue(values, headerMap, "Name").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"Row {rowNum}: Name is required");
                        errorCount++;
                        continue;
                    }

                    string vendorName = GetCsvValue(values, headerMap, "Vendor").Trim();
                    int? vendorId = null;
                    if (!string.IsNullOrWhiteSpace(vendorName))
                    {
                        if (vendorByName.TryGetValue(vendorName, out SpoolmanVendorDto? existingVendor))
                        {
                            vendorId = existingVendor.Id;
                        }
                        else
                        {
                            // Create the vendor in Spoolman
                            SpoolmanVendorDto newVendor = await spoolman.CreateVendorAsync(vendorName, null, ct);
                            vendorByName[vendorName] = newVendor;
                            vendorId = newVendor.Id;
                        }
                    }

                    SpoolmanCreateFilamentRequest req = new()
                    {
                        Name = name,
                        VendorId = vendorId,
                        Material = NullIfEmpty(GetCsvValue(values, headerMap, "Material")),
                        ColorHex = NullIfEmpty(GetCsvValue(values, headerMap, "ColorHex")),
                        Density = ParseDoubleOrNull(GetCsvValue(values, headerMap, "Density")),
                        Diameter = ParseDoubleOrNull(GetCsvValue(values, headerMap, "Diameter")),
                        Weight = ParseDoubleOrNull(GetCsvValue(values, headerMap, "Weight")),
                        SpoolWeight = ParseDoubleOrNull(GetCsvValue(values, headerMap, "SpoolWeight")),
                        Price = ParseDoubleOrNull(GetCsvValue(values, headerMap, "Price")),
                        SettingsExtruderTemp = ParseIntOrNull(GetCsvValue(values, headerMap, "ExtruderTemp")),
                        SettingsBedTemp = ParseIntOrNull(GetCsvValue(values, headerMap, "BedTemp")),
                        ArticleNumber = NullIfEmpty(GetCsvValue(values, headerMap, "ArticleNumber")),
                        Comment = NullIfEmpty(GetCsvValue(values, headerMap, "Comment")),
                    };

                    // Check if this is an update (has an Id that matches existing) or a create
                    string idStr = GetCsvValue(values, headerMap, "Id");
                    if (int.TryParse(idStr, out int existingId) && existingId > 0)
                    {
                        await spoolman.UpdateFilamentInSpoolmanAsync(existingId, req, ct);
                    }
                    else
                    {
                        await spoolman.CreateFilamentInSpoolmanAsync(req, ct);
                    }

                    created++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Row {rowNum}: {ex.Message}");
                    errorCount++;
                }
            }

            return Ok(new SpoolmanBulkUpdateResult(created, errorCount, [.. errors]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing Spoolman filaments from CSV: {Message}", ex.Message);
            return BadRequest(new { message = $"Import failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Scans the configured network ranges for Spoolman instances.
    /// Uses the discovery settings to determine which IP ranges to scan.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered Spoolman instances</returns>
    /// <response code="200">Returns list of discovered Spoolman instances</response>
    [HttpPost("scan-network")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanDiscoveryResult>), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> ScanNetworkAsync(CancellationToken ct)
    {
        try
        {
            NetworkDiscoverySettings settings = _settingsService.Get<NetworkDiscoverySettings>();
            List<string> ranges = settings?.DiscoverySubnets?.ToList() ?? new List<string>();
            IEnumerable<SpoolmanDiscoveryResult> results = await spoolman.ScanNetworkForSpoolmanAsync(ranges, ct);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in /api/spoolman/scan-network: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new[]
            {
                new SpoolmanDiscoveryResult(
                    Url: string.Empty,
                    IsAvailable: false,
                    Error: $"Network scan failed: {ex.Message}")
            });
        }
    }

    #region CSV Helpers

    /// <summary>
    /// Splits raw CSV text into logical records, correctly handling quoted fields
    /// that contain embedded newlines.
    /// </summary>
#pragma warning disable S127 // Loop counter updated in body is intentional for CSV character-level parsing
    private static List<string> SplitCsvRecords(string text)
    {
        List<string> records = [];
        StringBuilder current = new();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        // Escaped quote
                        current.Append('"');
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        current.Append(c);
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
                current.Append(c);
            }
            else if (c == '\r')
            {
                // End of record (consume optional \n)
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                records.Add(current.ToString());
                current.Clear();
            }
            else if (c == '\n')
            {
                // End of record
                records.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        // Add trailing record if any content remains
        if (current.Length > 0)
        {
            records.Add(current.ToString());
        }

        return records;
    }
#pragma warning restore S127

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Replace newlines with "; " so CSV fields stay on a single line
        string sanitized = value
            .Replace("\r\n", "; ")
            .Replace("\n", "; ")
            .Replace("\r", "; ");

        if (sanitized.Contains('"') || sanitized.Contains(','))
        {
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";
        }

        return sanitized;
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = [];
        bool inQuotes = false;
        StringBuilder current = new();
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }

            i++;
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    private static string GetCsvValue(string[] values, Dictionary<string, int> headerMap, string column)
    {
        return headerMap.TryGetValue(column, out int idx) && idx < values.Length
            ? values[idx].Trim()
            : string.Empty;
    }

    private static double? ParseDoubleOrNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null
            : double.TryParse(value, CultureInfo.InvariantCulture, out double result) ? result : null;
    }

    private static int? ParseIntOrNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null
            : int.TryParse(value, CultureInfo.InvariantCulture, out int result) ? result : null;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ParseBoolOrNull(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "1" or "YES" => true,
            "FALSE" or "0" or "NO" => false,
            _ => null,
        };
    }

    #endregion
}
