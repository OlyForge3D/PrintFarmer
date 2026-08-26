using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Abstractions.Problems;

/// <summary>
/// Reusable ProblemDetails factories for printed-part harvest conflicts, for modules that adopt
/// this shared conflict shape.
/// </summary>
/// <remarks>
/// Self-contained sibling of
/// <c>Farm.Web.Api.Infrastructure.PartsInventory.PartsInventoryProblemDetails</c> -- same
/// response shapes, but parameterized on primitives/generics instead of the monolith's
/// <c>Farm.Infrastructure.Dtos.PartsInventory.WrongBinResponse</c> /
/// <c>PartMappingRequiredResponse</c> DTOs, so this assembly has no dependency on
/// <c>Farm.Infrastructure</c>. The existing monolith type is untouched by this phase.
/// </remarks>
public static class PartsInventoryProblemDetails
{
    public const string WrongBinCode = "wrongBin";
    public const string PartMappingRequiredCode = "partMappingRequired";

    /// <summary>Builds the adjudicated wrong-bin conflict response.</summary>
    /// <param name="mismatches">The scanned-bin mismatches to surface as the <c>mismatches</c> extension.</param>
    public static ObjectResult WrongBin<TMismatch>(IReadOnlyCollection<TMismatch> mismatches)
    {
        ArgumentNullException.ThrowIfNull(mismatches);
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Wrong destination bin",
            Detail = "One or more scanned destination bins do not match the expected bins.",
            Type = "https://printfarmer.io/errors/wrong-bin",
        };
        problem.Extensions["code"] = WrongBinCode;
        problem.Extensions["mismatches"] = mismatches;
        return Conflict(problem);
    }

    /// <summary>Builds the adjudicated missing printed-output mapping response.</summary>
    public static ObjectResult PartMappingRequired(Guid jobId, Guid projectFileId, Guid? gcodeFileId, string guidance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Printed-part mapping required",
            Detail = guidance,
            Type = "https://printfarmer.io/errors/part-mapping-required",
        };
        problem.Extensions["code"] = PartMappingRequiredCode;
        problem.Extensions["jobId"] = jobId;
        problem.Extensions["projectFileId"] = projectFileId;
        problem.Extensions["gcodeFileId"] = gcodeFileId;
        problem.Extensions["guidance"] = guidance;
        return Conflict(problem);
    }

    private static ObjectResult Conflict(Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        var result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict,
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
