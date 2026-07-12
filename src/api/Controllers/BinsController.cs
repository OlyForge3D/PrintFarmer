using System.Security.Claims;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for printed-part storage bins. Bin registration reuses
/// the shared barcode-scan diagnostic infrastructure via <see cref="IBarcodeScanLogService"/>
/// rather than duplicating spool-only barcode plumbing.
/// </summary>
/// <remarks>
/// <b>#725 rebase seam</b>: gate every endpoint on <c>printedPartsInventoryEnabled</c>
/// via <c>IOperatorFeatureGate</c> when that service lands (see #705 / #725).
/// Disabled responses must be HTTP 404 with ProblemDetails
/// <c>extensions.code = "featureDisabled"</c> and must not persist scan logs.
/// </remarks>
[ApiController]
[Route("api/bins")]
[Authorize]
[Tags("Printed Parts Inventory")]
public class BinsController(
    ILogger<BinsController> logger,
    IBinRepository binRepository,
    IBarcodeScanLogService barcodeScanLogService,
    IOperatorFeatureGate featureGate) : ControllerBase
{
    private readonly ILogger<BinsController> _logger = logger;

    /// <summary>Lists all bins.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BinResponse>), 200)]
    public async Task<ActionResult<IReadOnlyList<BinResponse>>> GetAllAsync(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        List<Bin> bins = await binRepository.GetAllAsync(includeInactive, ct);
        return Ok(bins.Select(ToDto).ToList());
    }

    /// <summary>Gets a bin by its barcode / label.</summary>
    [HttpGet("{code}")]
    [ProducesResponseType(typeof(BinResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BinResponse>> GetByCodeAsync(string code, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        Bin? bin = await binRepository.GetByCodeAsync(code, ct);
        return bin is null ? NotFound(new { message = $"Bin '{code}' not found." }) : Ok(ToDto(bin));
    }

    /// <summary>Resolves a bin by scanned barcode. Logs the scan (if diagnostics are enabled).</summary>
    [HttpGet("by-barcode/{code}")]
    [ProducesResponseType(typeof(BinResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BinResponse>> ResolveByBarcodeAsync(string code, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        string normalizedCode = PartInventoryIdentity.NormalizeBinCode(code);
        Bin? bin = await binRepository.GetByCodeAsync(normalizedCode, ct);
        BarcodeScanOutcome outcome = bin is null ? BarcodeScanOutcome.NotFound : BarcodeScanOutcome.Resolved;

        await barcodeScanLogService.LogAsync(
            new BarcodeScanLog
            {
                Barcode = normalizedCode,
                Action = BarcodeScanAction.BinScan,
                Outcome = outcome,
                HttpStatus = bin is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK,
                BinId = bin?.Id,
                UserId = GetActorId(),
                Message = bin is null ? "Bin not found." : "Bin resolved.",
            },
            ct);

        return bin is null ? NotFound(new { message = $"Bin '{normalizedCode}' not found." }) : Ok(ToDto(bin));
    }

    /// <summary>Creates a bin.</summary>
    [HttpPost]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(BinResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<BinResponse>> CreateAsync(
        [FromBody] CreateBinRequest request,
        CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        string code = PartInventoryIdentity.NormalizeBinCode(request.Code);
        Bin? existing = await binRepository.GetByCodeAsync(code, ct);
        if (existing is not null)
        {
            return Conflict(new { message = $"Bin '{code}' already exists." });
        }

        var entity = new Bin
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = request.Name.Trim(),
            Location = request.Location,
            Notes = request.Notes,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await binRepository.AddAsync(entity, ct);
        _ = await binRepository.SaveChangesAsync(ct);

        return Created($"/api/bins/{code}", ToDto(entity));
    }

    /// <summary>Updates bin metadata.</summary>
    [HttpPut("{code}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(BinResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BinResponse>> UpdateAsync(
        string code,
        [FromBody] UpdateBinRequest request,
        CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        Bin? bin = await binRepository.GetByCodeAsync(code, ct);
        if (bin is null)
        {
            return NotFound(new { message = $"Bin '{code}' not found." });
        }

        bin.Name = request.Name.Trim();
        bin.Location = request.Location;
        bin.Notes = request.Notes;
        bin.IsActive = request.IsActive;
        bin.UpdatedAt = DateTime.UtcNow;
        _ = await binRepository.SaveChangesAsync(ct);

        return Ok(ToDto(bin));
    }

    /// <summary>
    /// Registers a bin from a scanned barcode. If a bin with the code
    /// already exists it is returned as-is; otherwise a new bin is created.
    /// Every call is written to the shared barcode-scan diagnostic log.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(BinResponse), 200)]
    [ProducesResponseType(typeof(BinResponse), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<BinResponse>> RegisterBarcodeAsync(
        [FromBody] RegisterBinBarcodeRequest request,
        CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { message = "Barcode is required." });
        }

        string code = PartInventoryIdentity.NormalizeBinCode(request.Code);
        Bin? existing = await binRepository.GetByCodeAsync(code, ct);
        if (existing is not null)
        {
            await barcodeScanLogService.LogAsync(
                new BarcodeScanLog
                {
                    Barcode = code,
                    Action = BarcodeScanAction.BinRegister,
                    Outcome = BarcodeScanOutcome.Resolved,
                    HttpStatus = StatusCodes.Status200OK,
                    BinId = existing.Id,
                    UserId = GetActorId(),
                    Message = "Bin already registered.",
                },
                ct);
            return Ok(ToDto(existing));
        }

        var entity = new Bin
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = string.IsNullOrWhiteSpace(request.Name) ? code : request.Name.Trim(),
            Location = request.Location,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await binRepository.AddAsync(entity, ct);
        _ = await binRepository.SaveChangesAsync(ct);

        await barcodeScanLogService.LogAsync(
            new BarcodeScanLog
            {
                Barcode = code,
                Action = BarcodeScanAction.BinRegister,
                Outcome = BarcodeScanOutcome.Registered,
                HttpStatus = StatusCodes.Status201Created,
                BinId = entity.Id,
                UserId = GetActorId(),
                Message = "Bin registered from barcode.",
            },
            ct);

        _logger.LogInformation("Registered bin {BinId} from barcode {Barcode}.", entity.Id, code);

        return Created($"/api/bins/{code}", ToDto(entity));
    }

    /// <summary>Soft-deactivates a bin while retaining historical ledger and scan references.</summary>
    [HttpDelete("{code}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(string code, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        Bin? bin = await binRepository.GetByCodeAsync(code, ct);
        if (bin is null)
        {
            return NotFound(new { message = $"Bin '{code}' not found." });
        }

        bin.IsActive = false;
        bin.UpdatedAt = DateTime.UtcNow;
        _ = await binRepository.SaveChangesAsync(ct);
        return NoContent();
    }

    private static BinResponse ToDto(Bin b)
    {
        return new BinResponse(
            b.Id,
            b.Code,
            b.Name,
            b.Location,
            b.Notes,
            b.IsActive,
            b.CreatedAt,
            b.UpdatedAt);
    }

    private NotFoundObjectResult? FeatureDisabledResult()
        => featureGate.IsEnabled(OperatorFeature.PrintedPartsInventory)
            ? null
            : OperatorFeatureProblemDetails.NotFound(featureGate, OperatorFeature.PrintedPartsInventory);

    private string? GetActorId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("oid");
}
