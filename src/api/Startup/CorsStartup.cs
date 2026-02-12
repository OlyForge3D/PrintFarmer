namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures CORS policies for API access.
/// </summary>
public static class CorsStartup
{
    /// <summary>
    /// Adds PrintFarmer CORS configuration.
    /// </summary>
    public static IServiceCollection AddPrintFarmerCors(this IServiceCollection services)
    {
        // CORS configuration for API access
        services.AddCors(options =>
        {
            options.AddPolicy("Default", policy =>
            {
                // Get allowed origins from environment variable or use defaults.
                string allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
                    ?? Environment.GetEnvironmentVariable("CORS__AllowedOrigins")
                    ?? "http://localhost:3000,https://localhost:3000,http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001";
                bool allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
                _ = policy.SetIsOriginAllowed(origin =>
                {
                    if (allowLocalNetwork)
                    {
                        return true;
                    }

                    string[] configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(o => o.Trim()).ToArray();
                    return configuredOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
                });
                _ = policy.AllowCredentials();
                _ = policy.WithHeaders("Content-Type", "Authorization", "x-correlation-id", "traceparent", "x-signalr-user-agent", "x-requested-with");
                _ = policy.WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS");
            });
        });

        return services;
    }
}
