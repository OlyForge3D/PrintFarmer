using Farm.Slicer.Module.Services.Configuration;

namespace Farm.Slicer.Module.Api.Services;

internal static class SlicerApiKeyConfiguration
{
    internal const string AllowInsecureDevelopmentRegistrationPath =
        $"{WorkerAuthSettings.SectionName}:AllowInsecureDevelopmentRegistration";

    internal const string SharedKeyPath = $"{WorkerAuthSettings.SectionName}:SharedKey";

    internal static bool IsInsecureDevelopmentRegistrationAllowed(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        environment.IsDevelopment() &&
        configuration.GetValue(AllowInsecureDevelopmentRegistrationPath, false);

    internal static string? ResolveSharedKey(IConfiguration configuration) =>
        FirstNonBlank(
            configuration[SharedKeyPath],
            configuration[$"{WorkerAuthSettings.SectionName}:SharedApiKey"],
            configuration["SlicerRegistry:ApiKey"],
            configuration["WORKER_SHARED_API_KEY"],
            configuration["SLICER_REGISTRATION_KEY"]);

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
