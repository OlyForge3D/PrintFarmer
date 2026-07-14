using System.Security.Claims;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Infrastructure.Idempotency;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for the printed-part SKU catalog, adjustment ledger,
/// job-output mappings, and reorder evaluation. This is distinct from
/// <c>MaintenanceComponentController</c>, which manages replacement parts
/// used to service printers rather than parts produced by prints.
/// </summary>
[ApiController]
[Route("api/parts-inventory")]
[Authorize]
[Tags("Printed Parts Inventory")]
public class PartsInventoryController(
    ILogger<PartsInventoryController> logger,
    IPartInventoryRepository partRepository,
    IBinRepository binRepository,
    IPartInventoryAdjustmentRepository adjustmentRepository,
    IPartOutputMappingRepository mappingRepository,
    IPartInventoryService partInventoryService,
    IReorderEvaluationService reorderService,
    IBarcodeScanLogService barcodeScanLogService,
    IOperatorFeatureGate featureGate) : ControllerBase
{
    /// <summary>Lists all printed-part SKUs.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PartInventoryResponse>), 200)]
    public async Task<ActionResult<IReadOnlyList<PartInventoryResponse>>> GetAllAsync(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        List<PartInventory> parts = await partRepository.GetAllAsync(includeInactive, ct);
        List<PartInventoryResponse> dtos = parts.Select(ToDto).ToList();
        return Ok(dtos);
    }

    /// <summary>Gets a printed-part SKU by its SKU string.</summary>
    [HttpGet("{sku}")]
    [ProducesResponseType(typeof(PartInventoryResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PartInventoryResponse>> GetBySkuAsync(string sku, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        PartInventory? part = await partRepository.GetBySkuAsync(sku, ct);
        if (part is null)
        {
            return NotFound(new { message = $"SKU '{sku}' not found." });
        }

        return Ok(ToDto(part));
    }

    /// <summary>Resolves a printed-part SKU from its normalized barcode and records scan history.</summary>
    [HttpGet("by-barcode/{sku}")]
    [ProducesResponseType(typeof(PartInventoryResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PartInventoryResponse>> ResolveByBarcodeAsync(string sku, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        string normalizedSku = PartInventoryIdentity.NormalizeSku(sku);
        PartInventory? part = await partRepository.GetBySkuAsync(normalizedSku, ct);
        await barcodeScanLogService.LogAsync(
            new BarcodeScanLog
            {
                Barcode = normalizedSku,
                Action = BarcodeScanAction.PartScan,
                Outcome = part is null ? BarcodeScanOutcome.NotFound : BarcodeScanOutcome.Resolved,
                HttpStatus = part is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK,
                PartInventoryId = part?.Id,
                UserId = GetActorId(),
                Message = part is null ? "Printed-part SKU not found." : "Printed-part SKU resolved.",
            },
            ct);

        return part is null
            ? NotFound(new { message = $"SKU '{normalizedSku}' not found." })
            : Ok(ToDto(part));
    }

    /// <summary>Creates a new printed-part SKU. Records a manual ledger entry when InitialOnHand > 0.</summary>
    [HttpPost]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(PartInventoryResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<PartInventoryResponse>> CreateAsync(
        [FromBody] CreatePartInventoryRequest request,
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

        CreatePartResult result = await partInventoryService.CreatePartAsync(
            new CreatePartCommand(
                request.Sku,
                request.Name,
                request.Description,
                request.ModelFileRef,
                request.DefaultBinCode,
                request.InitialOnHand,
                request.ReorderPoint,
                GetActorId()),
            ct);

        switch (result.Outcome)
        {
            case PartInventoryOutcome.Ok when result.Part is not null:
                return Created($"/api/parts-inventory/{result.Part.Sku}", ToDto(result.Part));
            case PartInventoryOutcome.SkuAlreadyExists:
                return Conflict(new { message = result.Message ?? "SKU already exists." });
            case PartInventoryOutcome.BinNotFound:
            case PartInventoryOutcome.InvalidRequest:
                return BadRequest(new { message = result.Message ?? "Invalid request." });
            case PartInventoryOutcome.FeatureDisabled:
                return OperatorFeatureProblemDetails.NotFound(featureGate, OperatorFeature.PrintedPartsInventory);
            default:
                logger.LogError("Unexpected outcome {Outcome} creating SKU {Sku}: {Msg}", result.Outcome, request.Sku, result.Message);
                return StatusCode(500, new { message = result.Message ?? "Unexpected error." });
        }
    }

    /// <summary>Updates a printed-part SKU's metadata and reorder threshold. Does not alter on-hand.</summary>
    [HttpPut("{sku}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(PartInventoryResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<PartInventoryResponse>> UpdateAsync(
        string sku,
        [FromBody] UpdatePartInventoryRequest request,
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

        PartInventory? part = await partRepository.GetBySkuAsync(sku, ct);
        if (part is null)
        {
            return NotFound(new { message = $"SKU '{sku}' not found." });
        }

        Guid? defaultBinId = null;
        if (!string.IsNullOrWhiteSpace(request.DefaultBinCode))
        {
            Bin? bin = await binRepository.GetByCodeAsync(request.DefaultBinCode, ct);
            if (bin is null || !bin.IsActive)
            {
                return BadRequest(new { message = $"Default bin '{request.DefaultBinCode}' not found." });
            }

            defaultBinId = bin.Id;
        }

        part.Name = request.Name.Trim();
        part.Description = request.Description;
        part.ModelFileRef = request.ModelFileRef;
        part.DefaultBinId = defaultBinId;
        part.ReorderPoint = request.ReorderPoint;
        part.IsActive = request.IsActive;
        part.UpdatedAt = DateTime.UtcNow;
        _ = await partRepository.SaveChangesAsync(ct);

        PartInventory? refreshed = await partRepository.GetBySkuAsync(sku, ct);
        return Ok(ToDto(refreshed ?? part));
    }

    /// <summary>
    /// Applies a signed adjustment to a SKU's stock. Reasons are one of
    /// harvest, qc-reject, or manual. An idempotency
    /// key on the request avoids double-application under client retries.
    /// </summary>
    /// <remarks>
    /// The persistent <c>Idempotency-Key</c> header (see #715) is layered on top
    /// of the existing <c>operationKey</c> body field. The header guarantees an
    /// identical response replay for retries; the body field prevents
    /// double-application at the ledger level even when the general 7-day
    /// window has elapsed.
    /// </remarks>
    [HttpPost("{sku:minlength(1):maxlength(64)}/adjust")]
    [Idempotent(IdempotencyRouteKeys.PartsInventoryAdjust)]
    [ProducesResponseType(typeof(PartAdjustmentResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<PartAdjustmentResponse>> AdjustAsync(
        string sku,
        [FromBody] AdjustPartInventoryRequest request,
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

        // Idempotency backstop (issue #715, Hicks r2 blocker 2 + r3 blocker 2): when the client
        // omits the body operationKey, the idempotency filter stashes a deterministic synthesized
        // key for this route in HttpContext.Items. We forward it on the DEDICATED
        // SynthesizedOperationKey channel — never merged into the client OperationKey field — so
        // the service can (a) reject any client value in the reserved "idem:" namespace while
        // (b) still honoring the trusted synthesized backstop, guaranteeing the domain's natural
        // (PartInventoryId, OperationKey) dedup applies even if a post-mutation flush failure
        // leaves the Processing row to be reclaimed and the same Idempotency-Key is retried.
        string? synthesizedOperationKey = null;
        if (HttpContext.Items.TryGetValue(IdempotencyFilter.SynthesizedOperationKeyItemKey, out object? synthesized)
            && synthesized is string synthesizedKey
            && !string.IsNullOrWhiteSpace(synthesizedKey))
        {
            synthesizedOperationKey = synthesizedKey;
        }

        AdjustResult result = await partInventoryService.AdjustAsync(
            sku,
            new AdjustCommand(
                request.Delta,
                request.Reason,
                request.JobId,
                request.BinCode,
                request.Notes,
                request.OperationKey,
                GetActorId(),
                synthesizedOperationKey),
            ct);

        return result.Outcome switch
        {
            PartInventoryOutcome.Ok => Ok(result.Adjustment),
            PartInventoryOutcome.IdempotentReplay => Ok(result.Adjustment),
            PartInventoryOutcome.PartNotFound => NotFound(new { message = result.Message }),
            PartInventoryOutcome.JobNotFound => NotFound(new { message = result.Message }),
            PartInventoryOutcome.BinNotFound => BadRequest(new { message = result.Message }),
            PartInventoryOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            PartInventoryOutcome.Conflict => Conflict(new { message = result.Message }),
            PartInventoryOutcome.FeatureDisabled => OperatorFeatureProblemDetails.NotFound(featureGate, OperatorFeature.PrintedPartsInventory),
            _ => Problem(result.Message, statusCode: 500),
        };
    }

    /// <summary>Returns recent adjustments (immutable ledger) for a SKU.</summary>
    [HttpGet("{sku}/adjustments")]
    [ProducesResponseType(typeof(IReadOnlyList<PartAdjustmentResponse>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<PartAdjustmentResponse>>> GetAdjustmentsAsync(
        string sku,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        PartInventory? part = await partRepository.GetBySkuAsync(sku, ct);
        if (part is null)
        {
            return NotFound(new { message = $"SKU '{sku}' not found." });
        }

        List<PartInventoryAdjustment> entries = await adjustmentRepository.GetForPartAsync(part.Id, limit, ct);
        return Ok(entries.Select(a => PartInventoryService.ToDto(a, part.Sku)).ToList());
    }

    /// <summary>
    /// Lists SKUs whose on-hand is below their reorder point. Consumed by
    /// the F8 shift compiler (#713) to schedule restock tasks.
    /// </summary>
    [HttpGet("reorder")]
    [ProducesResponseType(typeof(IReadOnlyList<ReorderCandidateResponse>), 200)]
    public async Task<ActionResult<IReadOnlyList<ReorderCandidateResponse>>> GetReorderCandidatesAsync(
        CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        IReadOnlyList<ReorderCandidateResponse> candidates = await reorderService.GetReorderCandidatesAsync(ct);
        return Ok(candidates);
    }

    /// <summary>Lists job-output → SKU mappings, optionally filtered by SKU.</summary>
    [HttpGet("mappings")]
    [ProducesResponseType(typeof(IReadOnlyList<PartOutputMappingResponse>), 200)]
    public async Task<ActionResult<IReadOnlyList<PartOutputMappingResponse>>> GetMappingsAsync(
        [FromQuery] string? sku = null,
        CancellationToken ct = default)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (!string.IsNullOrWhiteSpace(sku))
        {
            PartInventory? part = await partRepository.GetBySkuAsync(sku, ct);
            if (part is null)
            {
                return NotFound(new { message = $"SKU '{sku}' not found." });
            }

            List<PartOutputMapping> mappings = await mappingRepository.GetForPartAsync(part.Id, ct);
            return Ok(mappings.Select(m => ToMappingDto(m, part)).ToList());
        }

        List<PartOutputMapping> allMappings = await mappingRepository.GetAllAsync(ct);
        return Ok(allMappings.Select(mapping => ToMappingDto(mapping)).ToList());
    }

    /// <summary>Creates a job-output → SKU mapping.</summary>
    [HttpPost("mappings")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(PartOutputMappingResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PartOutputMappingResponse>> CreateMappingAsync(
        [FromBody] CreatePartOutputMappingRequest request,
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

        if (request.GcodeFileId is null && request.PrintProjectFileId is null)
        {
            return BadRequest(new { message = "One of gcodeFileId or printProjectFileId must be set." });
        }

        if (request.GcodeFileId is not null && request.PrintProjectFileId is not null)
        {
            return BadRequest(new { message = "Only one of gcodeFileId or printProjectFileId may be set." });
        }

        string normalizedSku = PartInventoryIdentity.NormalizeSku(request.Sku);
        PartInventory? part = await partRepository.GetBySkuAsync(normalizedSku, ct);
        if (part is null)
        {
            return NotFound(new { message = $"SKU '{normalizedSku}' not found." });
        }

        if (!part.IsActive)
        {
            return BadRequest(new { message = $"SKU '{normalizedSku}' is inactive." });
        }

        if (!await mappingRepository.SourceExistsAsync(request.GcodeFileId, request.PrintProjectFileId, ct))
        {
            return NotFound(new { message = "The referenced G-code or project file does not exist." });
        }

        if (await mappingRepository.MappingExistsAsync(
            part.Id,
            request.GcodeFileId,
            request.PrintProjectFileId,
            ct))
        {
            return Conflict(new { message = "This output is already mapped to the requested SKU." });
        }

        var entity = new PartOutputMapping
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = request.GcodeFileId,
            PrintProjectFileId = request.PrintProjectFileId,
            Quantity = Math.Max(1, request.Quantity),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await mappingRepository.AddAsync(entity, ct);
        try
        {
            _ = await mappingRepository.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PartInventoryService.IsUniqueViolation(ex))
        {
            return Conflict(new { message = "This output is already mapped to the requested SKU." });
        }

        return Created($"/api/parts-inventory/mappings/{entity.Id}", ToMappingDto(entity, part));
    }

    /// <summary>Deletes a job-output → SKU mapping.</summary>
    [HttpDelete("mappings/{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteMappingAsync(Guid id, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        PartOutputMapping? entity = await mappingRepository.GetByIdAsync(id, ct);
        if (entity is null)
        {
            return NotFound(new { message = $"Mapping '{id}' not found." });
        }

        await mappingRepository.RemoveAsync(entity, ct);
        _ = await mappingRepository.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Soft-deactivates a printed-part SKU while retaining its immutable ledger.</summary>
    [HttpDelete("{sku}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(string sku, CancellationToken ct)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        PartInventory? part = await partRepository.GetBySkuAsync(sku, ct);
        if (part is null)
        {
            return NotFound(new { message = $"SKU '{sku}' not found." });
        }

        part.IsActive = false;
        part.UpdatedAt = DateTime.UtcNow;
        _ = await partRepository.SaveChangesAsync(ct);
        return NoContent();
    }

    private static PartInventoryResponse ToDto(PartInventory p)
    {
        return new PartInventoryResponse(
            p.Id,
            p.Sku,
            p.Name,
            p.Description,
            p.ModelFileRef,
            p.DefaultBinId,
            p.DefaultBin?.Code,
            p.DefaultBin?.Name,
            p.OnHand,
            p.ReorderPoint,
            NeedsReorder: p.IsActive && p.OnHand <= p.ReorderPoint,
            p.IsActive,
            p.CreatedAt,
            p.UpdatedAt);
    }

    private static PartOutputMappingResponse ToMappingDto(PartOutputMapping m, PartInventory? part = null)
    {
        PartInventory? owner = part ?? m.PartInventory;
        return new PartOutputMappingResponse(
            m.Id,
            m.PartInventoryId,
            owner?.Sku ?? string.Empty,
            m.GcodeFileId,
            m.PrintProjectFileId,
            m.Quantity,
            m.CreatedAt,
            m.UpdatedAt);
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
