using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Json;
using Farm.Web.Api.Infrastructure.Filters;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures controllers with JSON serialization and filters.
/// </summary>
public static class ControllerStartup
{
    /// <summary>
    /// Adds PrintFarmer Controllers with JSON options and filters.
    /// </summary>
    public static IServiceCollection AddPrintFarmerControllers(this IServiceCollection services)
    {
        // Add API services
        services.AddControllers(options =>
            {
                _ = options.Filters.Add<DuplicateConflictExceptionFilter>();
            })
            .AddJsonOptions(options =>
            {
                // Configure JSON options for .NET 9 compatibility
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.WriteIndented = false;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new PrinterBackendJsonConverter());
                options.JsonSerializerOptions.Converters.Add(new PrintJobStatusJsonConverter());

                // Default string enum converter for all other enums
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        // TODO(PFarm1-24w): Once old slicer controllers are deleted, add a
        // ProjectReference to Farm.Slicer.Module.Api and enable:
        // .AddSlicerControllers();
        return services;
    }
}
