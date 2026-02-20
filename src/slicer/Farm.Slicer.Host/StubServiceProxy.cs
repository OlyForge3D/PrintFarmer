using System.Collections;
using System.Reflection;

namespace Farm.Slicer.Host;

/// <summary>
/// A <see cref="DispatchProxy"/>-based stub that returns sensible defaults for
/// every method call. Used during the transitional phase before service
/// implementations are migrated into <c>Farm.Slicer.Module</c>.
/// </summary>
/// <typeparam name="T">The service interface to proxy.</typeparam>
/// <remarks>
/// Async methods return completed tasks with default/empty values.
/// Sync methods return default values. Collection-typed returns yield empty
/// collections rather than null to avoid <see cref="NullReferenceException"/>
/// in callers that enumerate results.
/// </remarks>
public class StubServiceProxy<T> : DispatchProxy
    where T : class
{
    /// <summary>
    /// Creates a new proxy instance for the specified service interface.
    /// </summary>
#pragma warning disable CA1000 // Do not declare static members on generic types — factory method pattern
    public static T CreateInstance() => Create<T, StubServiceProxy<T>>();
#pragma warning restore CA1000

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        Type returnType = targetMethod.ReturnType;

        // void methods
        if (returnType == typeof(void))
        {
            return null;
        }

        // Task (non-generic)
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        // ValueTask (non-generic)
        if (returnType == typeof(ValueTask))
        {
            return ValueTask.CompletedTask;
        }

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

    /// <summary>
    /// Produces a sensible default for the given <paramref name="type"/>:
    /// empty collections for enumerable/list types, default(T) otherwise.
    /// </summary>
    private static object? CreateDefault(Type type)
    {
        // Nullable<T> → default
        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return null;
        }

        // IReadOnlyList<T>, IList<T>, IEnumerable<T>, ICollection<T>
        if (type.IsGenericType)
        {
            Type genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(IReadOnlyList<>)
                || genDef == typeof(IList<>)
                || genDef == typeof(IEnumerable<>)
                || genDef == typeof(ICollection<>)
                || genDef == typeof(IReadOnlyCollection<>))
            {
                Type elemType = type.GetGenericArguments()[0];
                return typeof(Array)
                    .GetMethod(nameof(Array.Empty))!
                    .MakeGenericMethod(elemType)
                    .Invoke(null, null);
            }

            // List<T>
            if (genDef == typeof(List<>))
            {
                return Activator.CreateInstance(type);
            }

            // Dictionary<K,V>
            if (genDef == typeof(Dictionary<,>)
                || genDef == typeof(IDictionary<,>)
                || genDef == typeof(IReadOnlyDictionary<,>))
            {
                return Activator.CreateInstance(
                    typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
            }
        }

        // Non-generic IEnumerable
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            return Array.Empty<object>();
        }

        // Value types → default(T)
        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        // Reference types (including DTOs, domain entities) → null
        return null;
    }
}
