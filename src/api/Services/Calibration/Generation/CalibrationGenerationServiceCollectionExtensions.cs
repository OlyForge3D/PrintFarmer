using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Registers the deterministic calibration generation core.
/// </summary>
/// <remarks>
/// These services are pure, side-effect free application/domain services apart from the profile patch
/// exporter, which persists through the authoritative calibration project service. Registering them
/// does not advertise generation as operational: the capability document keeps
/// <c>calibrationGenerationEnabled</c> false until the whole production path is proven.
/// </remarks>
public static class CalibrationGenerationServiceCollectionExtensions
{
    /// <summary>Adds the calibration generation services to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddCalibrationGeneration();
    /// </code>
    /// </example>
    public static IServiceCollection AddCalibrationGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<
            ICalibrationSpecificationCompiler,
            CalibrationSpecificationCompiler>();
        services.TryAddScoped<ICalibrationModelValidator, CalibrationModelValidator>();
        services.TryAddScoped<IOrcaCalibrationPlanCompiler, OrcaCalibrationPlanCompiler>();
        services.TryAddScoped<
            IKlipperCalibrationGcodeGenerator,
            KlipperCalibrationGcodeGenerator>();
        services.TryAddScoped<ICalibrationGcodeAnnotator, CalibrationGcodeAnnotator>();
        services.TryAddScoped<
            ICalibrationGcodeSafetyValidator,
            CalibrationGcodeSafetyValidator>();
        services.TryAddScoped<
            ICalibrationProfilePatchExporter,
            CalibrationProfilePatchExporter>();
        return services;
    }
}
