using Farm.Infrastructure;
using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.SlicerHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Web.Api.Startup;

/// <summary>Registers the split-host metadata repositories and authenticated promotion byte adapter.</summary>
public static class SlicerPromotionDependenciesStartup
{
    private const int DefaultStreamTimeoutSeconds = 240;
    private const int ProxyReadTimeoutSeconds = 300;

    /// <summary>Registers promotion dependencies only when the slicer module is out of process.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerPromotionDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!CalibrationProfileResolutionStartup.IsSplitDeployment(configuration))
        {
            return services;
        }

        _ = services.EnsureSlicerDatabaseRegistered(configuration);
        services.TryAddScoped<IArtifactsRepository, EfArtifactsRepository>();
        services.TryAddScoped<ISliceJobRepository, EfSliceJobRepository>();

        string? sharedKey = configuration[SlicerPromotionContract.SharedKeyPath];
        string? rawBaseUrl =
            configuration["SlicerHost:BaseUrl"] ??
            configuration["SLICER_HOST_URL"];
        if (string.IsNullOrWhiteSpace(sharedKey) || string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            return services;
        }

        if (!Uri.TryCreate(rawBaseUrl.Trim(), UriKind.Absolute, out Uri? baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            throw new InvalidOperationException(
                "'SlicerHost:BaseUrl' must be an absolute http(s) URL without query, fragment, or user information.");
        }

        int timeoutSeconds = configuration.GetValue(
            $"{SlicerPromotionContract.SectionName}:StreamTimeoutSeconds",
            DefaultStreamTimeoutSeconds);
        if (timeoutSeconds is < 1 or >= ProxyReadTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"'{SlicerPromotionContract.SectionName}:StreamTimeoutSeconds' must be between 1 and " +
                $"{ProxyReadTimeoutSeconds - 1} seconds.");
        }

        var options = new SlicerHostPromotionOptions(
            new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute),
            TimeSpan.FromSeconds(timeoutSeconds));
        _ = services.AddSingleton(options);
        _ = services.AddTransient<SlicerPromotionAuthenticationHandler>();
        _ = services
            .AddHttpClient<IPromotionArtifactContentSource, SlicerHostPromotionArtifactContentSource>(client =>
            {
                client.BaseAddress = options.BaseUrl;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddHttpMessageHandler<SlicerPromotionAuthenticationHandler>()
            .RemoveAllLoggers();

        return services;
    }
}
