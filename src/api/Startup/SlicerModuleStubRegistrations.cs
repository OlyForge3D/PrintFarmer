using System.Collections;
using System.Reflection;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Registers stub (no-op) implementations for slicer module service interfaces.
/// These stubs bridge the gap while real service implementations are migrated
/// from the API project into <c>Farm.Slicer.Module</c>.
/// </summary>
public static class SlicerModuleStubRegistrations
{
    /// <summary>
    /// Registers stub implementations for all slicer module service interfaces
    /// that the module controllers depend on via DI.
    /// </summary>
    public static IServiceCollection AddSlicerModuleStubServices(this IServiceCollection services)
    {
        // Core slicer services (SlicersController, SlicerManagementController)
        RegisterStub<ISlicersService>(services);

        // Slicing submission (SlicingSubmissionController)
        RegisterStub<ISlicingSubmissionService>(services);

        // Slice job support (SliceJobController)
        RegisterStub<ISliceJobEventService>(services);
        RegisterStub<IArtifactsService>(services);
        RegisterStub<IRateLimitService>(services);
        RegisterStub<IWorkerAuthService>(services);
        RegisterStub<IWorkerCircuitBreakerService>(services);

        // Slicing orchestration (SlicingJobsController)
        RegisterStub<Farm.Slicer.Module.Services.ISlicerOrchestrator>(services);
        RegisterStub<IFileManagementService>(services);
        RegisterStub<IStoredFileOperationsService>(services);
        RegisterStub<ITempPathProvider>(services);

        // Model/file management (Model3DFilesController)
        RegisterStub<IModel3DFileService>(services);
        RegisterStub<I3MfToStlConversionService>(services);

        // Profiles (ProfilesController)
        RegisterStub<IProfilesService>(services);

        // Orca bundle services (ProfilesController)
        RegisterStub<IOrcaBundleExportService>(services);
        RegisterStub<IOrcaBundleParsingService>(services);
        RegisterStub<IOrcaPresetMappingService>(services);

        // Progress notifier (SlicerProgressHub)
        RegisterStub<Farm.Slicer.Module.Services.ISlicerProgressNotifier>(services);

        return services;
    }

    private static void RegisterStub<TInterface>(IServiceCollection services)
        where TInterface : class
    {
        services.AddScoped(_ => ModuleStubProxy<TInterface>.CreateInstance());
    }
}

/// <summary>
/// A <see cref="DispatchProxy"/>-based stub that returns sensible defaults for
/// every method call. Used during the transitional phase before service
/// implementations are migrated into <c>Farm.Slicer.Module</c>.
/// </summary>
internal class ModuleStubProxy<T> : DispatchProxy
    where T : class
{
    public static T CreateInstance() => Create<T, ModuleStubProxy<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        Type returnType = targetMethod.ReturnType;

        if (returnType == typeof(void)) return null;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (returnType == typeof(bool)) return false;
        if (returnType == typeof(string)) return string.Empty;

        // Task<T>
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type innerType = returnType.GetGenericArguments()[0];
            object? defaultValue = CreateDefault(innerType);
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(innerType)
                .Invoke(null, [defaultValue]);
        }

        // ValueTask<T>
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            Type innerType = returnType.GetGenericArguments()[0];
            object? defaultValue = CreateDefault(innerType);
            return Activator.CreateInstance(returnType, defaultValue);
        }

        return CreateDefault(returnType);
    }

    private static object? CreateDefault(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is not null) return null;

        if (type.IsGenericType)
        {
            Type genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(IReadOnlyList<>) || genDef == typeof(IList<>)
                || genDef == typeof(IEnumerable<>) || genDef == typeof(ICollection<>)
                || genDef == typeof(IReadOnlyCollection<>))
            {
                Type elemType = type.GetGenericArguments()[0];
                return typeof(Array).GetMethod(nameof(Array.Empty))!
                    .MakeGenericMethod(elemType).Invoke(null, null);
            }

            if (genDef == typeof(List<>)) return Activator.CreateInstance(type);

            if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>)
                || genDef == typeof(IReadOnlyDictionary<,>))
            {
                return Activator.CreateInstance(
                    typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
            }
        }

        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            return Array.Empty<object>();
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
