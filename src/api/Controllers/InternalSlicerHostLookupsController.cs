using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.SlicerHost;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides authenticated, read-only cross-domain lookups to the standalone slicer host.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route(SlicerHostLookupContract.RouteBase)]
public sealed class InternalSlicerHostLookupsController(
    SlicerHostServiceAuthenticator authenticator,
    ICatalogService catalogService,
    IPrintersService printersService) : ControllerBase
{
    private readonly SlicerHostServiceAuthenticator _authenticator = authenticator;
    private readonly ICatalogService _catalogService = catalogService;
    private readonly IPrintersService _printersService = printersService;

    /// <summary>Gets all catalog manufacturers.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Catalog manufacturers, or an authentication problem.</returns>
    [AllowAnonymous]
    [HttpGet("catalog/manufacturers")]
    public async Task<ActionResult<IReadOnlyList<ManufacturerDto>>> GetManufacturersAsync(
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        (IReadOnlyList<ManufacturerDto> manufacturers, _) =
            await _catalogService.GetManufacturersAsync(ct);
        return Ok(manufacturers);
    }

    /// <summary>Gets one catalog printer model.</summary>
    /// <param name="modelId">Catalog printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model, 404, or an authentication problem.</returns>
    [AllowAnonymous]
    [HttpGet("catalog/printer-models/{modelId:guid}")]
    public async Task<ActionResult<PrinterModelDto>> GetPrinterModelAsync(
        Guid modelId,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        PrinterModelDto? model = await _catalogService.GetModelByIdAsync(modelId, ct);
        return model is null ? NotFound() : Ok(model);
    }

    /// <summary>Gets slicer aliases for one catalog printer model.</summary>
    /// <param name="modelId">Catalog printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model aliases, or an authentication problem.</returns>
    [AllowAnonymous]
    [HttpGet("catalog/printer-models/{modelId:guid}/aliases")]
    public async Task<ActionResult<IEnumerable<SlicerModelAliasDto>>> GetModelAliasesAsync(
        Guid modelId,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        IEnumerable<SlicerModelAliasDto> aliases =
            await _catalogService.GetModelAliasesAsync(modelId, ct);
        return Ok(aliases);
    }

    /// <summary>Gets the printer identity required by the slicer host.</summary>
    /// <param name="printerId">Printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The minimal printer projection, 404, or an authentication problem.</returns>
    [AllowAnonymous]
    [HttpGet("printers/{printerId:guid}")]
    public async Task<ActionResult<SlicerHostPrinterLookupDto>> GetPrinterAsync(
        Guid printerId,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        Printer? printer = await _printersService.FindByIdWithIncludesAsync(printerId, ct);
        return printer is null
            ? NotFound()
            : Ok(new SlicerHostPrinterLookupDto(
                printer.Id,
                printer.Name,
                printer.ModelId,
                printer.Model?.Name));
    }

    private ObjectResult? GetAuthenticationFailure()
    {
        if (!_authenticator.IsConfigured)
        {
            return ServiceProblem(
                StatusCodes.Status503ServiceUnavailable,
                "Authentication service unavailable",
                "authentication_unavailable");
        }

        return _authenticator.IsAuthorized(Request)
            ? null
            : ServiceProblem(
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "authentication_required");
    }

    private ObjectResult ServiceProblem(int status, string title, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = Request.Path,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
