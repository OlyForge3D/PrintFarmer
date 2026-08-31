using System.Text.Json;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// xUnit collection fixture (issue #2242): boots exactly one <see cref="CustomWebApplicationFactory"/>
/// and fetches <c>/openapi/v1.json</c> exactly once, so every OpenAPI schema-fidelity test class in
/// <see cref="OpenApiDocumentCollection"/> shares a single parsed document instead of each spinning up
/// its own web host. The document is treated as read-only for the fixture's lifetime.
/// </summary>
public sealed class OpenApiDocumentFixture : IAsyncLifetime
{
    private CustomWebApplicationFactory? _factory;

    public JsonDocument Document { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        _ = response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync();
        Document = await JsonDocument.ParseAsync(content);
    }

    public async Task DisposeAsync()
    {
        Document.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}

/// <summary>Groups every OpenAPI schema-fidelity test class (issue #2242) onto the single shared <see cref="OpenApiDocumentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class OpenApiDocumentCollection : ICollectionFixture<OpenApiDocumentFixture>
{
    public const string Name = "OpenApi document (issue #2242)";
}

/// <summary>
/// Resolution helpers over the live OpenAPI document (issue #2242): operation lookup, <c>$ref</c>
/// resolution, required-set/property lookup, and enum-token extraction. Every helper operates
/// directly on <see cref="JsonElement"/> — the raw parsed document — rather than a strongly typed
/// OpenAPI object model, so a test can assert exactly what a generated client would see, including
/// the <em>absence</em> of a schema, type, or enum list that a typed model might silently normalize
/// away.
/// </summary>
public static class OpenApiSchemaTestSupport
{
    private static readonly HashSet<string> EmptySet = [];

    /// <summary>Looks up <c>paths.{path}.{method}</c> (method is lowercase, e.g. <c>"get"</c>).</summary>
    public static JsonElement GetOperation(JsonDocument document, string path, string method)
    {
        JsonElement paths = document.RootElement.GetProperty("paths");
        if (!paths.TryGetProperty(path, out JsonElement pathItem))
        {
            throw new InvalidOperationException($"OpenAPI document has no path '{path}'.");
        }

        if (!pathItem.TryGetProperty(method, out JsonElement operation))
        {
            throw new InvalidOperationException($"OpenAPI document path '{path}' has no '{method}' operation.");
        }

        return operation;
    }

    /// <summary>
    /// The response schema for the given status code/media type, or <c>null</c> if the operation
    /// documents that response with no <c>content</c>/schema at all (a real, testable state — see
    /// <c>GET /api/slice/{id}</c>).
    /// </summary>
    public static JsonElement? GetResponseSchema(JsonElement operation, string statusCode = "200", string mediaType = "application/json")
    {
        if (!operation.GetProperty("responses").TryGetProperty(statusCode, out JsonElement response))
        {
            throw new InvalidOperationException($"Operation has no '{statusCode}' response documented.");
        }

        if (!response.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (!content.TryGetProperty(mediaType, out JsonElement media))
        {
            return null;
        }

        return media.GetProperty("schema");
    }

    public static JsonElement GetComponentSchema(JsonDocument document, string name)
    {
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        if (!schemas.TryGetProperty(name, out JsonElement schema))
        {
            throw new InvalidOperationException($"OpenAPI document has no component schema '{name}'.");
        }

        return schema;
    }

    /// <summary>Follows a single <c>$ref</c> hop to its component schema; returns <paramref name="schema"/> unchanged if it is not a $ref.</summary>
    public static JsonElement ResolveRef(JsonDocument document, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("$ref", out JsonElement refElement))
        {
            return schema;
        }

        string pointer = refElement.GetString() ?? throw new InvalidOperationException("$ref value was null.");
        const string prefix = "#/components/schemas/";
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported $ref pointer '{pointer}'; only local component schema refs are handled.");
        }

        return GetComponentSchema(document, pointer[prefix.Length..]);
    }

    /// <summary>The declared "required" property names, or an empty set if the schema has no "required" array at all.</summary>
    public static IReadOnlySet<string> GetRequiredSet(JsonElement objectSchema)
    {
        if (!objectSchema.TryGetProperty("required", out JsonElement required))
        {
            return EmptySet;
        }

        return required.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static JsonElement GetProperty(JsonElement objectSchema, string propertyName)
    {
        JsonElement properties = objectSchema.GetProperty("properties");
        if (!properties.TryGetProperty(propertyName, out JsonElement property))
        {
            throw new InvalidOperationException($"Schema has no property '{propertyName}'.");
        }

        return property;
    }

    /// <summary>The declared property names, or an empty set if the schema has no "properties" object at all.</summary>
    public static IReadOnlySet<string> GetPropertyNames(JsonElement objectSchema)
    {
        if (!objectSchema.TryGetProperty("properties", out JsonElement properties))
        {
            return EmptySet;
        }

        return properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Normalizes OpenAPI 3.1's "type" keyword (a single string, e.g. <c>"integer"</c>, or an array
    /// such as <c>["null","string"]</c> for a nullable property) into a set for uniform membership
    /// checks. Returns an empty set if the schema declares no "type" at all (a completely
    /// unconstrained schema — see <c>UserTaskAnchorKind</c>/<c>UserTaskSourceKind</c>).
    /// </summary>
    public static IReadOnlySet<string> GetTypes(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return EmptySet;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => new HashSet<string>(StringComparer.Ordinal) { type.GetString()! },
            JsonValueKind.Array => type.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal),
            _ => EmptySet,
        };
    }

    /// <summary>Whether the schema's "type" set includes JSON Schema's <c>"null"</c> (OpenAPI 3.1 nullability representation).</summary>
    public static bool IsNullable(JsonElement schema) => GetTypes(schema).Contains("null");

    /// <summary>The declared "enum" token list for a schema, or <c>null</c> if it has no "enum" keyword at all.</summary>
    public static IReadOnlyList<string>? GetEnumTokens(JsonElement schema)
    {
        if (!schema.TryGetProperty("enum", out JsonElement enumElement))
        {
            return null;
        }

        return [.. enumElement.EnumerateArray().Select(e => e.GetString()!)];
    }
}
