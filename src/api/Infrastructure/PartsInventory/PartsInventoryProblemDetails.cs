using Farm.Infrastructure.Dtos.PartsInventory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Infrastructure.PartsInventory;

/// <summary>Canonical ProblemDetails payloads for printed-part harvest conflicts.</summary>
public static class PartsInventoryProblemDetails
{
    public const string WrongBinCode = "wrongBin";
    public const string PartMappingRequiredCode = "partMappingRequired";

    /// <summary>Builds the adjudicated wrong-bin conflict response.</summary>
    public static ObjectResult WrongBin(WrongBinResponse details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Wrong destination bin",
            Detail = "One or more scanned destination bins do not match the expected bins.",
            Type = "https://printfarmer.io/errors/wrong-bin",
        };
        problem.Extensions["code"] = WrongBinCode;
        problem.Extensions["mismatches"] = details.Mismatches;
        return Conflict(problem);
    }

    /// <summary>Builds the adjudicated missing printed-output mapping response.</summary>
    public static ObjectResult PartMappingRequired(PartMappingRequiredResponse details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Printed-part mapping required",
            Detail = details.Guidance,
            Type = "https://printfarmer.io/errors/part-mapping-required",
        };
        problem.Extensions["code"] = PartMappingRequiredCode;
        problem.Extensions["jobId"] = details.JobId;
        problem.Extensions["projectFileId"] = details.ProjectFileId;
        problem.Extensions["gcodeFileId"] = details.GcodeFileId;
        problem.Extensions["guidance"] = details.Guidance;
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
