using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Json;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures SignalR for real-time updates.
/// </summary>
public static class SignalRStartup
{
    /// <summary>
    /// Adds PrintFarmer SignalR configuration with consistent JSON serialization.
    /// </summary>
    public static IServiceCollection AddPrintFarmerSignalR(this IServiceCollection services)
    {
        // SignalR for real-time updates
        services.AddSignalR(options =>
        {
            // Ensure single parallel invocation to prevent race conditions
            options.MaximumParallelInvocationsPerClient = 1;
        })
        .AddJsonProtocol(options =>
        {
            // CRITICAL: Use SAME JSON configuration as Controllers for consistency
            // This ensures SignalR broadcasts use camelCase property names matching client expectations
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.WriteIndented = false;
            options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

            // Register custom converters for complex types
            options.PayloadSerializerOptions.Converters.Add(new PrinterBackendJsonConverter());
            options.PayloadSerializerOptions.Converters.Add(new PrintJobStatusJsonConverter());

            // Default string enum converter
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        return services;
    }
}
