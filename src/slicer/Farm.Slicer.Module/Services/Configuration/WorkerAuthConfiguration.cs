using Microsoft.Extensions.Configuration;

namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Resolves the canonical slicer worker registration credential.
/// </summary>
public static class WorkerAuthConfiguration
{
    /// <summary>The only supported configuration path for the shared registration key.</summary>
    public const string SharedKeyPath = $"{WorkerAuthSettings.SectionName}:SharedKey";

    /// <summary>
    /// Resolves the configured key together with the provider type that supplied it.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The resolution, or <see langword="null"/> when the key is missing or blank.</returns>
    public static WorkerAuthKeyResolution? ResolveSharedKey(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? value = configuration[SharedKeyPath];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new WorkerAuthKeyResolution(value, ResolveSource(configuration, value));
    }

    private static string ResolveSource(IConfiguration configuration, string value)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (IConfigurationProvider provider in root.Providers.Reverse())
            {
                if (provider.TryGet(SharedKeyPath, out string? candidate) &&
                    string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return provider.GetType().Name;
                }
            }
        }

        return configuration.GetType().Name;
    }
}

/// <summary>
/// A resolved worker registration key and its non-secret configuration source.
/// </summary>
/// <param name="Value">Secret key material. Callers must never log this value.</param>
/// <param name="Source">Configuration provider type that supplied the value.</param>
public sealed record WorkerAuthKeyResolution(string Value, string Source);
