using Farm.Infrastructure.PrinterCalibration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Slicer.Module.Api.Health;

/// <summary>
/// Reports whether this host can actually serve calibration profile resolution.
/// </summary>
/// <remarks>
/// This is the no-data availability probe the main API's split-mode resolver adapter calls. It
/// carries no end-user identity and returns no profile data — only whether a resolver is registered
/// and its store answers. Anything unproven is reported unhealthy so the caller fails closed.
/// </remarks>
public sealed class CalibrationProfileResolverHealthCheck(
    ICalibrationProfileResolver? profileResolver = null) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (profileResolver is null)
        {
            return HealthCheckResult.Unhealthy(
                "No calibration profile resolver is registered in this host.");
        }

        try
        {
            return await profileResolver.IsAvailableAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Calibration profile resolution is available.")
                : HealthCheckResult.Unhealthy(
                    "The calibration profile store did not answer.");
        }
        catch (CalibrationProfileResolverUnavailableException)
        {
            return HealthCheckResult.Unhealthy(
                "The calibration profile store could not be queried.");
        }
    }
}
