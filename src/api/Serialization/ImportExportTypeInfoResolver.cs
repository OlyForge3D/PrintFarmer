using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using Farm.Web.Shared.Annotations;
using Farm.Web.Shared;

namespace Farm.Web.Api.Serialization;

/// <summary>
/// TypeInfoResolver that wraps the default resolver and applies ImportExportAttribute-based suppression
/// for properties marked to be ignored during export.
/// </summary>
public sealed class ImportExportTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _inner = new();

    public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var ti = _inner.GetTypeInfo(type, options);
        // Ensure we have a JsonTypeInfo (Default resolver should return non-null)
        if (ti == null)
        {
            return _inner.GetTypeInfo(type, options);
        }

        // Only need to inspect types that may contain the attribute on properties
        foreach (var prop in ti.Properties)
        {
            var pi = type.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null)
            {
                continue;
            }

            var attr = pi.GetCustomAttribute<ImportExportAttribute>(inherit: true);
            if (attr != null && (attr.IgnoreFor & ImportExportTargets.Export) != 0)
            {
                // Prevent this property from being written during serialization
                prop.ShouldSerialize = (obj, ctx) => false;
            }
        }

        return ti;
    }
}
