using Farm.Infrastructure;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>Serves bytes only for an artifact pinned by the presenting promotion operation.</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route(SlicerPromotionContract.RouteBase)]
public sealed class InternalPromotionArtifactController(
    SlicerPromotionServiceAuthenticator authenticator,
    IArtifactsRepository artifactsRepository,
    IArtifactsService artifacts) : ControllerBase
{
    private readonly SlicerPromotionServiceAuthenticator _authenticator = authenticator;
    private readonly IArtifactsRepository _artifactsRepository = artifactsRepository;
    private readonly IArtifactsService _artifacts = artifacts;

    /// <summary>Streams one actively pinned artifact without returning metadata or a storage path.</summary>
    /// <param name="artifactId">Artifact identifier.</param>
    /// <param name="operationKey">Owner-scoped operation identity that must hold the active pin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw artifact bytes, or a fail-closed problem response.</returns>
    [AllowAnonymous]
    [HttpGet("artifacts/{artifactId:guid}/content")]
    public async Task<IActionResult> GetContentAsync(
        Guid artifactId,
        [FromHeader(Name = SlicerPromotionContract.OperationKeyHeaderName)] string? operationKey,
        CancellationToken cancellationToken)
    {
        if (!_authenticator.IsConfigured)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "promotion_authentication_unavailable");
        }

        if (!_authenticator.IsAuthorized(Request))
        {
            return Problem(StatusCodes.Status401Unauthorized, "promotion_authentication_required");
        }

        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return Problem(StatusCodes.Status403Forbidden, "promotion_pin_required");
        }

        Artifact? artifact = await _artifactsRepository.GetByIdAsync(artifactId, cancellationToken);
        if (artifact is null ||
            artifact.PromotionStartedAtUtc is null ||
            artifact.PromotedAtUtc is not null ||
            !string.Equals(artifact.PromotionOperationKey, operationKey, StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status403Forbidden, "promotion_pin_mismatch");
        }

        ArtifactContentStream? content =
            await _artifacts.OpenReadStreamAsync(artifactId, cancellationToken);
        if (content is null)
        {
            return NotFound();
        }

        Response.ContentLength = artifact.SizeBytes;
        return File(content.Content, "application/octet-stream", enableRangeProcessing: false);
    }

    private ObjectResult Problem(int statusCode, string code)
    {
        ProblemDetails details = new()
        {
            Status = statusCode,
            Title = code.Replace('_', ' '),
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = Request.Path,
        };
        details.Extensions["code"] = code;
        return StatusCode(statusCode, details);
    }
}
