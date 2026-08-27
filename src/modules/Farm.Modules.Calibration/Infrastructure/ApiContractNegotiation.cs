using Farm.Modules.Calibration.Services.Capabilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Calibration.Infrastructure;

/// <summary>Applies the additive desktop API contract negotiation used by capability endpoints.</summary>
public static class ApiContractNegotiation
{
    public const string ContractVersionHeader = "X-PrintFarmer-Api-Contract-Version";

    public const string MinimumContractVersionHeader = "X-PrintFarmer-Minimum-Api-Contract-Version";

    public const string MinimumSupportedContractVersionHeader =
        "X-PrintFarmer-Minimum-Supported-Api-Contract-Version";

    public const string ContractVersionQueryParameter = "apiContractVersion";

    /// <summary>Adds current and minimum server contract versions to the response.</summary>
    public static void AddResponseHeaders(HttpResponse response)
    {
        response.Headers[ContractVersionHeader] =
            CalibrationCapabilityService.CurrentApiContractVersion;
        response.Headers[MinimumContractVersionHeader] =
            CalibrationCapabilityService.MinimumSupportedApiContractVersion;
        response.Headers[MinimumSupportedContractVersionHeader] =
            CalibrationCapabilityService.MinimumSupportedApiContractVersion;
    }

    /// <summary>
    /// Returns an upgrade response when a client explicitly negotiates a contract below the minimum.
    /// Clients that omit negotiation retain legacy-compatible behavior.
    /// </summary>
    public static ObjectResult? Negotiate(HttpRequest request)
    {
        string? requestedVersion = request.Headers[ContractVersionHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            requestedVersion = request.Query[ContractVersionQueryParameter].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return null;
        }

        if (!Version.TryParse(requestedVersion, out Version? requested) ||
            !Version.TryParse(
                CalibrationCapabilityService.MinimumSupportedApiContractVersion,
                out Version? minimum) ||
            requested < minimum)
        {
            ProblemDetails problem = new()
            {
                Status = StatusCodes.Status426UpgradeRequired,
                Title = "Client upgrade required",
                Detail = $"This server requires API contract version {CalibrationCapabilityService.MinimumSupportedApiContractVersion} or newer.",
                Type = "https://docs.printfarmer.io/problems/client-upgrade-required",
            };
            problem.Extensions["code"] = "client_upgrade_required";
            problem.Extensions["apiContractVersion"] =
                CalibrationCapabilityService.CurrentApiContractVersion;
            problem.Extensions["minimumSupportedApiContractVersion"] =
                CalibrationCapabilityService.MinimumSupportedApiContractVersion;

            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status426UpgradeRequired,
                ContentTypes = { "application/problem+json" },
            };
        }

        return null;
    }
}
