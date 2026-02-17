using Microsoft.AspNetCore.DataProtection;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures Data Protection for encrypting sensitive data (API keys, passwords).
/// </summary>
public static class DataProtectionStartup
{
    /// <summary>
    /// Adds PrintFarmer Data Protection configuration.
    /// IMPORTANT: In Docker deployments we mount a persistent host volume at
    /// /root/.aspnet/DataProtection-Keys. Persisting keys here ensures secrets can
    /// be decrypted across container restarts and upgrades.
    /// </summary>
    public static IServiceCollection AddPrintFarmerDataProtection(
        this IServiceCollection services,
        IWebHostEnvironment environment,
        string contentRootPath)
    {
        var keysDirectoryPath = Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_PATH");

        if (string.IsNullOrWhiteSpace(keysDirectoryPath))
        {
            var userProfileDirectoryPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            keysDirectoryPath = string.IsNullOrWhiteSpace(userProfileDirectoryPath)
                ? Path.Combine(contentRootPath, "data-protection-keys")
                : Path.Combine(userProfileDirectoryPath, ".aspnet", "DataProtection-Keys");
        }

        Directory.CreateDirectory(keysDirectoryPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectoryPath))
            .SetApplicationName("PrintFarmer");

        // Register sensitive data encryption service
        services.AddSingleton<Farm.Infrastructure.Services.Security.ISensitiveDataProtector,
            Farm.Infrastructure.Services.Security.SensitiveDataProtector>();

        return services;
    }
}
