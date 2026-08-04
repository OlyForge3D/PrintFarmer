using Farm.Slicer.Module.Api.Services;
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
        bool insecureDevelopmentRegistrationEnabled = configuration.GetValue(
            SlicerApiKeyConfiguration.AllowInsecureDevelopmentRegistrationPath,
            false);

        if (insecureDevelopmentRegistrationEnabled && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{SlicerApiKeyConfiguration.AllowInsecureDevelopmentRegistrationPath} " +
                "may only be enabled in the Development environment.");
        }

        if (!string.IsNullOrWhiteSpace(SlicerApiKeyConfiguration.ResolveSharedKey(configuration)))
        {
            return Task.CompletedTask;
        }

        if (!insecureDevelopmentRegistrationEnabled)
        {
            throw new InvalidOperationException(
                "The slicer module requires a shared API key. Configure " +
                $"{SlicerApiKeyConfiguration.SharedKeyPath} through configuration or a secret provider.");
        }

        logger.LogCritical(
            "INSECURE DEVELOPMENT MODE: slicer registration API-key validation is disabled. " +
            "Never enable {ConfigurationPath} outside local development.",
            SlicerApiKeyConfiguration.AllowInsecureDevelopmentRegistrationPath);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
