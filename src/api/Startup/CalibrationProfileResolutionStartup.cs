using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Chooses how this process resolves calibration profiles for a given deployment topology.
/// </summary>
/// <remarks>
/// <para>
/// Monolith hosts load the slicer module in process, so <c>AddSlicerModule</c> registers the local
/// database-backed <c>CalibrationProfileResolver</c> and this class does nothing.
/// </para>
/// <para>
/// Split and microservices hosts deliberately do not load the slicer module, which previously left
/// <see cref="ICalibrationProfileResolver"/> unregistered. Selected-printer context requests then
/// short-circuited to <c>503 profile_service_unavailable</c> and
/// <c>calibrationContextEnabled</c> stayed false. Here the API instead talks to the slicer host that
/// owns the profile store, over an authenticated internal HTTP hop.
/// </para>
/// </remarks>
public static class CalibrationProfileResolutionStartup
{
    /// <summary>
    /// Registers the split-deployment calibration profile resolver adapter when the deployment mode
    /// requires it and a slicer-host base URL is configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a slicer-host base URL is present but invalid, so the misconfiguration surfaces at
    /// startup rather than as a silent calibration outage.
    /// </exception>
    public static IServiceCollection AddCalibrationProfileResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsSplitDeployment(configuration))
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
                    $"Calibration profile resolution is misconfigured for this split deployment. {error}");
            }

            // Nothing configured: keep the previous fail-closed behaviour rather than guessing an
            // internal address. Calibration stays unavailable and says so through capabilities.
            return services;
        }

        services.AddHttpContextAccessor();
        services.RemoveAll<ICalibrationProfileResolver>();
        _ = services.AddSingleton(options);
        _ = services
            .AddHttpClient<ICalibrationProfileResolver, SlicerHostCalibrationProfileResolver>(client =>
            {
                client.BaseAddress = options.BaseUrl;

                // Per-request linked cancellation applies the real bound; this is a backstop.
                client.Timeout = options.ResolveTimeout + TimeSpan.FromSeconds(5);
                client.MaxResponseContentBufferSize = options.MaxResponseBytes;
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            })

            // The default IHttpClientFactory request logger writes the outbound URI at Information.
            // That would publish the internal slicer-host address into ordinary application logs.
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>
    /// Returns whether this host runs without an in-process slicer module.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns><see langword="true"/> for split and microservices deployments.</returns>
    /// <remarks>
    /// Covers both gates that can leave the API without a local resolver, because they read
    /// different keys and must not disagree:
    /// <list type="bullet">
    /// <item><description>
    /// <c>SlicerModuleExtensions.AddSlicerModule</c> returns before registering the local
    /// database resolver when <c>DEPLOYMENT_MODE</c>/<c>Deployment:Mode</c> is split or microservices.
    /// </description></item>
    /// <item><description>
    /// <c>Program.cs</c> skips slicer integration entirely when <c>DEPLOYMENT_MODE</c> or its
    /// <c>DEPLOYMENT_TYPE</c> synonym is microservices — the value the deploy installers write.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static bool IsSplitDeployment(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? deploymentMode =
            configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            configuration.GetValue<string>("Deployment:Mode");
        if (string.Equals(deploymentMode, "microservices", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(deploymentMode, "split", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? deploymentType =
            configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            configuration.GetValue<string>("DEPLOYMENT_TYPE");
        return string.Equals(deploymentType, "microservices", StringComparison.OrdinalIgnoreCase);
    }
}
