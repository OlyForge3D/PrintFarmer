using Farm.Infrastructure.Services;
using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Host;

/// <summary>
/// Extension methods that register stub (no-op) implementations for slicer service
/// interfaces that do not yet have concrete implementations in
/// <c>Farm.Slicer.Module</c> or <c>Farm.Slicer.Module.Api</c>.
/// </summary>
/// <remarks>
/// As of Phase 2, all slicer service interfaces are covered by real implementations
/// in <see cref="Farm.Slicer.Module.Api.SlicerApiExtensions.AddSlicerApiServices"/>
/// except for the two listed below.
/// </remarks>
public static class StubServiceRegistrations
{
    /// <summary>
    /// Registers stub implementations for the remaining interfaces that have no
    /// concrete implementation yet:
    /// <list type="bullet">
    ///   <item><see cref="IModel3DFileService"/> – 3D model CRUD (not yet in module)</item>
    ///   <item><see cref="I3MfToStlConversionService"/> – 3MF-to-STL conversion (not yet in module)</item>
    /// </list>
    /// All other controller-injected interfaces are now covered by
    /// <c>AddSlicerApiServices()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddUnimplementedSlicerServiceStubs(this IServiceCollection services)
    {
        RegisterStub<IModel3DFileService>(services);
        RegisterStub<I3MfToStlConversionService>(services);

        return services;
    }

    /// <summary>
    /// Registers a <see cref="StubServiceProxy{T}"/> as the scoped implementation
    /// for <typeparamref name="TInterface"/>.
    /// </summary>
    private static void RegisterStub<TInterface>(IServiceCollection services)
        where TInterface : class
    {
        services.AddScoped(_ => StubServiceProxy<TInterface>.CreateInstance());
    }
}
