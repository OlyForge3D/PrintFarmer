using Farm.Infrastructure.Dtos.PartsInventory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Infrastructure.PartsInventory;

/// <summary>Canonical ProblemDetails payloads for printed-part harvest conflicts.</summary>
public static class PartsInventoryProblemDetails
{
    public const string WrongBinCode = "wrongBin";
    public const string PartMappingRequiredCode = "partMappingRequired";

    /// <summary>Code emitted when the job has not reached a harvestable state.</summary>
    public const string JobNotCompletedCode = "jobNotCompleted";

    /// <summary>Code emitted for any other harvest conflict outcome without a dedicated adjudication.</summary>
    public const string GenericConflictCode = "conflict";

    /// <summary>Builds the adjudicated wrong-bin conflict response.</summary>
    public static ObjectResult WrongBin(WrongBinResponse details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problem = new HarvestConflictResponse
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Wrong destination bin",
            Detail = "One or more scanned destination bins do not match the expected bins.",
            Type = "https://printfarmer.io/errors/wrong-bin",
            Code = WrongBinCode,
            Mismatches = details.Mismatches,
        };
        return Conflict(problem);
    }

    /// <summary>
    /// Builds the conflict response for a harvest attempt against a job that has not reached a
    /// harvestable state yet. Carries only the discriminator <see cref="HarvestConflictResponse.Code"/>
    /// and a human-readable <paramref name="message"/> -- unlike <see cref="WrongBin"/> and
    /// <see cref="PartMappingRequired"/>, this outcome has no code-specific structured details, so it
    /// otherwise matches the plain <c>{ message }</c> shape callers previously received (issue #2294:
    /// this path was reachable but undeclared, so the newly-typed <c>[ProducesResponseType]</c> on
    /// <c>HarvestJobAsync</c> now covers it too).
    /// </summary>
    public static ObjectResult JobNotCompleted(string? message)
    {
        var problem = new HarvestConflictResponse
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Job not completed",
            Detail = message,
            Type = "https://printfarmer.io/errors/job-not-completed",
            Code = JobNotCompletedCode,
        };
        return Conflict(problem);
    }

    /// <summary>
    /// Builds the conflict response for any other harvest outcome without a dedicated adjudication.
    /// See <see cref="JobNotCompleted"/> remarks for why this exists alongside the richer,
    /// code-specific builders.
    /// </summary>
    public static ObjectResult Generic(string? message)
    {
        var problem = new HarvestConflictResponse
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Harvest conflict",
            Detail = message,
            Type = "https://printfarmer.io/errors/harvest-conflict",
            Code = GenericConflictCode,
        };
        return Conflict(problem);
    }

    /// <summary>Builds the adjudicated missing printed-output mapping response.</summary>
    public static ObjectResult PartMappingRequired(PartMappingRequiredResponse details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problem = new HarvestConflictResponse
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Printed-part mapping required",
            Detail = details.Guidance,
            Type = "https://printfarmer.io/errors/part-mapping-required",
            Code = PartMappingRequiredCode,
            JobId = details.JobId,
            ProjectFileId = details.ProjectFileId,
            GcodeFileId = OptionalGuid.Of(details.GcodeFileId),
            Guidance = details.Guidance,
        };
        return Conflict(problem);
    }

    private static ObjectResult Conflict(ProblemDetails problem)
    {
        var result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict,
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
