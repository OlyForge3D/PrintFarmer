using System.Reflection;
using System.Text.Json;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2262 (piece 1 -- the mechanical, document-wide follow-up to #2242): a single test that
/// reflects over every enum type reachable from the full OpenAPI <c>components.schemas</c> graph
/// and asserts each is documented as <c>type: string</c> with a matching <c>enum</c> token array,
/// instead of hand-writing new per-operation/per-enum assertions the way the other classes in this
/// folder do for the 5 P0 corpus families. Reuses <see cref="OpenApiSchemaTestSupport"/> from
/// #2242 as-is; no new resolution helpers are added there.
///
/// Unlike its sibling classes, this is an ideal-behavior assertion, not a characterization test:
/// it expects every reachable enum schema to be correct, rather than locking in today's known-broken
/// shape for a hand-picked list of enums. Per #2262/#2261, most currently fail for the same systemic
/// root cause (<c>ConfigureHttpJsonOptions</c> vs MVC <c>AddJsonOptions</c> divergence) as #2242's
/// family-scoped tests -- that failure is expected while #2261 remains open and will resolve once
/// its fix lands; it is not a defect in this test.
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class OpenApiEnumFidelityTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    [Fact]
    public void EveryReachableEnumComponentSchema_IsDocumentedAsStringWithMatchingEnumTokens()
    {
        JsonElement schemas = _document.RootElement.GetProperty("components").GetProperty("schemas");
        Dictionary<string, Type> enumTypesBySchemaName = ResolveUnambiguousEnumTypesByName();

        var failures = new List<string>();
        var checkedSchemaNames = new List<string>();

        foreach (JsonProperty schemaProperty in schemas.EnumerateObject())
        {
            if (!enumTypesBySchemaName.TryGetValue(schemaProperty.Name, out Type? enumType))
            {
                // Not an unambiguous CLR enum by simple-name match (an object/DTO schema, or a name
                // shared by more than one loaded enum type) -- outside this sweep's reach.
                continue;
            }

            checkedSchemaNames.Add(schemaProperty.Name);
            JsonElement schema = schemaProperty.Value;

            IReadOnlySet<string> types = OpenApiSchemaTestSupport.GetTypes(schema);
            IReadOnlyList<string>? enumTokens = OpenApiSchemaTestSupport.GetEnumTokens(schema);
            string[] expectedTokens = Enum.GetNames(enumType);

            bool isCorrectlyDocumented =
                types.SetEquals(new[] { "string" }) &&
                enumTokens is not null &&
                enumTokens.Count == expectedTokens.Length &&
                enumTokens.ToHashSet(StringComparer.Ordinal).SetEquals(expectedTokens);

            if (!isCorrectlyDocumented)
            {
                string actualType = types.Count == 0 ? "<none>" : string.Join(",", types);
                string actualEnum = enumTokens is null ? "<none>" : string.Join(",", enumTokens);
                failures.Add(
                    $"{schemaProperty.Name}: documented type=[{actualType}] enum=[{actualEnum}]; " +
                    $"expected type=[string] enum=[{string.Join(",", expectedTokens)}]");
            }
        }

        _ = checkedSchemaNames.Should().NotBeEmpty(
            "the sweep should find at least one component schema that unambiguously resolves to a loaded CLR enum type");

        _ = failures.Should().BeEmpty(
            "every enum type reachable from components.schemas should be documented as 'type: string' with an " +
            "'enum' token array matching its CLR member names (see #2261 for the known systemic root cause behind " +
            "any failures below):\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Every public enum type across the already-loaded application assemblies -- the shared
    /// <see cref="OpenApiDocumentFixture"/> already booted the full host to fetch the document, so
    /// every assembly that can contribute a component schema is already resident in this
    /// <see cref="AppDomain"/> -- keyed by simple type name, mirroring how the reflection-based
    /// OpenAPI schema generator derives a component schema id from a CLR type. Names shared by more
    /// than one loaded enum type are dropped entirely: it would not be safe to match either to a
    /// single schema.
    /// </summary>
    private static Dictionary<string, Type> ResolveUnambiguousEnumTypesByName()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic &&
                (assembly.GetName().Name?.StartsWith("Farm.", StringComparison.Ordinal) ?? false))
            .SelectMany(SafeGetTypes)
            .Where(type => type.IsEnum && (type.IsPublic || type.IsNestedPublic))
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="Assembly.GetTypes"/> can throw <see cref="ReflectionTypeLoadException"/> for a
    /// small number of assemblies in this solution whose dependencies are not fully loadable in the
    /// test host (see the identical guard in <c>NetworkDiscoveryServiceTests</c>); recovering the
    /// types that did load, rather than dropping the whole assembly, keeps the sweep as complete as
    /// possible.
    /// </summary>
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}
