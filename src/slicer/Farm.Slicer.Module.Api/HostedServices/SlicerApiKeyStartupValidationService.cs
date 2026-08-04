using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.HostedServices;

internal sealed class SlicerApiKeyStartupValidationService(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<SlicerApiKeyStartupValidationService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        WorkerAuthKeyResolution? resolution =
            WorkerAuthConfiguration.ResolveSharedKey(configuration);
        if (resolution is null)
        {
            throw new InvalidOperationException(
                "The slicer module requires a shared API key. Configure " +
                $"{WorkerAuthConfiguration.SharedKeyPath} through configuration or a secret provider.");
        }

        logger.LogInformation(
            "Worker registration authentication configured from {ConfigurationPath} via " +
            "{ConfigurationSource} for environment {EnvironmentName}; key material is not logged.",
            WorkerAuthConfiguration.SharedKeyPath,
            resolution.Source,
            environment.EnvironmentName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
