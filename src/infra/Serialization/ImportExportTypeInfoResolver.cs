using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Serialization;

/// <summary>
/// TypeInfoResolver that wraps the default resolver and applies ImportExportAttribute-based suppression
/// for properties marked to be ignored during export.
/// </summary>
public sealed class ImportExportTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _inner = new();

    public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo ti = _inner.GetTypeInfo(type, options);
        // Ensure we have a JsonTypeInfo (Default resolver should return non-null)
        if (ti == null)
        {
            return _inner.GetTypeInfo(type, options);
        }

        // Only need to inspect types that may contain the attribute on properties
        foreach (JsonPropertyInfo prop in ti.Properties)
        {
            PropertyInfo? pi = type.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null)
            {
                continue;
            }

            ImportExportAttribute? attr = pi.GetCustomAttribute<ImportExportAttribute>(inherit: true);
            if (attr != null && (attr.IgnoreFor & ImportExportTargets.Export) != 0)
            {
                // Prevent this property from being written during serialization
                prop.ShouldSerialize = (obj, ctx) => false;
            }
        }

        return ti;
    }
}
