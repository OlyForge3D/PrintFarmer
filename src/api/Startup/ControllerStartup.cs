using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Json;
using Farm.Slicer.Module.Api;
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
    public static IServiceCollection AddPrintFarmerControllers(this IServiceCollection services, bool slicerEnabled = true)
    {
        // Add API services
        var mvcBuilder = services.AddControllers(options =>
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

        // Only discover slicer controllers when the module is enabled.
        // ASP.NET Core auto-discovers controllers from referenced assemblies,
        // so we must explicitly remove the slicer assembly when disabled to
        // prevent DI activation errors for unregistered slicer services.
        if (slicerEnabled)
        {
            mvcBuilder.AddSlicerControllers();
        }
        else
        {
            mvcBuilder.ConfigureApplicationPartManager(manager =>
            {
                var slicerPart = manager.ApplicationParts
                    .FirstOrDefault(p => p.Name == "Farm.Slicer.Module.Api");
                if (slicerPart is not null)
                {
                    manager.ApplicationParts.Remove(slicerPart);
                }
            });
        }

        return services;
    }
}
