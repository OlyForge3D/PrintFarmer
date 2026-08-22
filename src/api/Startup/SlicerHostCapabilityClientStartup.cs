using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Chooses how this process answers calibration generation worker/version compatibility for a given
/// deployment topology (issue #1848).
/// </summary>
/// <remarks>
/// <para>
/// Monolith hosts load the slicer module in process, so <c>AddSlicerModule</c> registers the local
/// <c>IDbContextFactory&lt;SlicerDbContext&gt;</c> that
/// <c>CalibrationGenerationCapabilityProbe</c> already reads directly, and this class does nothing.
/// </para>
/// <para>
/// Split and microservices hosts deliberately do not load the slicer module, which previously left
/// the probe with no factory and no fallback — it always returned an empty snapshot, misreporting
/// <c>calibrationGenerationEnabled: false</c> even when a fully attested worker was online. Here the
/// API instead talks to the slicer host that owns the worker registry, over an authenticated internal
/// HTTP hop guarded by the shared worker key rather than a forwarded end-user token.
/// </para>
/// </remarks>
public static class SlicerHostCapabilityClientStartup
{
    /// <summary>
    /// Registers the split-deployment worker-compatibility client when the deployment mode requires
    /// it and a slicer-host base URL is configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a slicer-host base URL is present but invalid, so the misconfiguration surfaces at
    /// startup rather than as a silent calibration outage.
    /// </exception>
    public static IServiceCollection AddSlicerHostCapabilityClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!CalibrationProfileResolutionStartup.IsSplitDeployment(configuration))
        {
            return services;
        }

        if (!SlicerHostCalibrationResolverOptions.TryCreate(
                configuration,
                out SlicerHostCalibrationResolverOptions? options,
                out string? error))
        {
            if (error is not null)
            {
                throw new InvalidOperationException(
                    $"Worker compatibility resolution is misconfigured for this split deployment. {error}");
            }

            // Nothing configured: keep the previous fail-closed behaviour rather than guessing an
            // internal address. Calibration generation stays unavailable and says so through
            // capabilities.
            return services;
        }

        services.TryAddSingleton(options);
        _ = services.RemoveAll<ISlicerHostCapabilityClient>();
        _ = services
            .AddHttpClient<ISlicerHostCapabilityClient, SlicerHostCapabilityClient>(client =>
            {
                client.BaseAddress = options.BaseUrl;

                // Per-request linked cancellation applies the real bound; this is a backstop.
                client.Timeout = options.ResolveTimeout + TimeSpan.FromSeconds(5);
                client.MaxResponseContentBufferSize = WorkerCompatibilityContract.MaxResponseBytes;
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            })

            // The default IHttpClientFactory request logger writes the outbound URI at Information.
            // That would publish the internal slicer-host address into ordinary application logs.
            .RemoveAllLoggers();

        return services;
    }
}
