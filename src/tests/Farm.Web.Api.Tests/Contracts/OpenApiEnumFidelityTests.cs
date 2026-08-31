using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2262 (piece 1 -- the mechanical, document-wide follow-up to #2242): a single test that
/// reflects over every enum type reachable from the full OpenAPI <c>components.schemas</c> graph
/// and asserts each is documented as <c>type: string</c> with a matching <c>enum</c> token array,
/// instead of hand-writing new per-operation/per-enum assertions the way the other classes in this
/// folder do for the 5 P0 corpus families. Reuses <see cref="OpenApiSchemaTestSupport"/> from
/// #2242 as-is; no new resolution helpers are added there.
///
/// Two categories of enum are excluded from the strict "matches <c>Enum.GetNames</c>" comparison
/// because it is provably the wrong expectation for them, independent of #2261:
/// <list type="bullet">
/// <item><description>
/// <c>[Flags]</c> enums (e.g. <c>ApiKeyScope</c>) can be serialized as a combined, comma-separated
/// value that a simple OpenAPI <c>enum</c> array of base member names cannot express.
/// </description></item>
/// <item><description>
/// Enums carrying a type-level <see cref="JsonConverterAttribute"/> naming a converter other than
/// the global <see cref="JsonStringEnumConverter"/> (e.g. <c>UserTaskAnchorKind</c>,
/// <c>AttentionSeverity</c> in the <c>Attention</c> namespace) have converter-defined wire tokens
/// (often camelCase) that do not match <see cref="Enum.GetNames(Type)"/>'s PascalCase output --
/// asserting against that would misreport an intentional, already-shipped wire contract as broken.
/// </description></item>
/// </list>
/// Excluding a schema's only CLR candidate for either reason also removes it from a same-named
/// ambiguity check (see <see cref="ResolveEnumTypeCandidatesByName"/>): e.g. two distinct
/// <c>AttentionSeverity</c> CLR types share that simple name, but only one lacks a custom
/// converter, so the sweep still checks that one instead of silently dropping the name entirely.
///
/// Unlike its sibling classes, this is otherwise an ideal-behavior assertion, not a
/// characterization test: for every remaining (non-excluded) reachable enum schema it expects the
/// correct documented shape, rather than locking in today's known-broken shape for a hand-picked
/// list of enums. Per #2262/#2261, most currently fail for the same systemic root cause
/// (<c>ConfigureHttpJsonOptions</c> vs MVC <c>AddJsonOptions</c> divergence) as #2242's
/// family-scoped tests. Left unskipped, that would permanently redden the shared test assembly, so
/// this fact is skipped until #2261 lands -- remove the <see cref="FactAttribute.Skip"/> then and
/// let the (correctly scoped) failures drive the remaining fix-up work.
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class OpenApiEnumFidelityTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    [Fact(Skip = "Ideal-behavior sweep blocked by #2261 (ConfigureHttpJsonOptions vs AddJsonOptions " +
        "divergence) -- remove this Skip once #2261 lands and address whatever the sweep still flags.")]
    public void EveryReachableEnumComponentSchema_IsDocumentedAsStringWithMatchingEnumTokens()
    {
        JsonElement schemas = _document.RootElement.GetProperty("components").GetProperty("schemas");
        Dictionary<string, IReadOnlyList<Type>> candidatesBySchemaName = ResolveEnumTypeCandidatesByName();

        var failures = new List<string>();
        var checkedSchemaNames = new List<string>();
        var ambiguousSchemaNames = new List<string>();

        foreach (JsonProperty schemaProperty in schemas.EnumerateObject())
        {
            if (!candidatesBySchemaName.TryGetValue(schemaProperty.Name, out IReadOnlyList<Type>? candidates))
            {
                // Not a CLR enum by simple-name match at all (an object/DTO schema) -- outside this
                // sweep's reach.
                continue;
            }

            List<Type> eligible = candidates.Where(IsEligibleForStrictComparison).ToList();
            if (eligible.Count != 1)
            {
                // Zero candidates eligible: every same-named CLR enum is a [Flags] type and/or
                // carries a custom, non-PascalCase converter -- deliberately out of scope for this
                // strict check (see class doc comment). More than one still eligible: a genuine
                // same-named ambiguity between two "plain" enums that this sweep cannot safely
                // resolve; call it out explicitly rather than silently dropping the name.
                if (eligible.Count > 1)
                {
                    ambiguousSchemaNames.Add(schemaProperty.Name);
                }

                continue;
            }

            Type enumType = eligible[0];
            checkedSchemaNames.Add(schemaProperty.Name);
            JsonElement schema = schemaProperty.Value;

            IReadOnlySet<string> types = OpenApiSchemaTestSupport.GetTypes(schema);
            IReadOnlyList<string>? enumTokens;
            try
            {
                enumTokens = OpenApiSchemaTestSupport.GetEnumTokens(schema);
            }
            catch (InvalidOperationException)
            {
                // A documented `enum` array containing a non-string token (e.g. numeric) -- itself
                // a fidelity failure, not a bug in this sweep.
                enumTokens = null;
            }

            string[] expectedTokens = Enum.GetNames(enumType);

            bool isCorrectlyDocumented =
                types.SetEquals(new[] { "string" }) &&
                enumTokens is not null &&
                enumTokens.Count == expectedTokens.Length &&
                enumTokens.ToHashSet(StringComparer.Ordinal).SetEquals(expectedTokens);

            if (!isCorrectlyDocumented)
            {
                string actualType = types.Count == 0 ? "<none>" : string.Join(",", types);
                string actualEnum = enumTokens is null
                    ? "<none>"
                    : string.Join(",", enumTokens.Select(token => token ?? "<null>"));
                failures.Add(
                    $"{schemaProperty.Name}: documented type=[{actualType}] enum=[{actualEnum}]; " +
                    $"expected type=[string] enum=[{string.Join(",", expectedTokens)}]");
            }
        }

        _ = ambiguousSchemaNames.Should().BeEmpty(
            "these component schema names match more than one loaded CLR enum type that is eligible " +
            "for the strict comparison (not [Flags], no custom non-standard JsonConverter) -- this " +
            "sweep cannot safely pick one, so add a disambiguation rule instead of leaving them unchecked");

        // A regression check on the resolution/eligibility logic itself: "ApiKeyPurpose" is a plain,
        // globally-converted enum with no same-name collisions, while "AttentionSeverity" has two
        // same-named CLR types and is only checkable because the custom-converter one is correctly
        // excluded from eligibility -- both must still be found and checked.
        _ = checkedSchemaNames.Should().Contain(new[] { "ApiKeyPurpose", "AttentionSeverity" },
            "the sweep should resolve and check at least these known, non-ambiguous-after-filtering enums");

        _ = failures.Should().BeEmpty(
            "every reachable, in-scope enum type in components.schemas should be documented as " +
            "'type: string' with an 'enum' token array matching its CLR member names (see #2261 for " +
            "the known systemic root cause behind any failures below):\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// True when <paramref name="enumType"/>'s wire representation is expected to be exactly
    /// <see cref="Enum.GetNames(Type)"/> under the global <see cref="JsonStringEnumConverter"/> --
    /// i.e. it is not a <c>[Flags]</c> combinable enum, and it either carries no type-level
    /// <see cref="JsonConverterAttribute"/> or one that names the standard converter itself (as
    /// opposed to a bespoke converter class with its own, possibly non-PascalCase, token mapping).
    /// </summary>
    private static bool IsEligibleForStrictComparison(Type enumType)
    {
        if (Attribute.IsDefined(enumType, typeof(FlagsAttribute)))
        {
            return false;
        }

        var converterAttribute = enumType.GetCustomAttribute<JsonConverterAttribute>();
        if (converterAttribute?.ConverterType is null)
        {
            return true;
        }

        Type converterType = converterAttribute.ConverterType;
        return converterType == typeof(JsonStringEnumConverter) ||
            (converterType.IsGenericType &&
                converterType.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>));
    }

    /// <summary>
    /// Every public enum type across the already-loaded application assemblies -- the shared
    /// <see cref="OpenApiDocumentFixture"/> already booted the full host to fetch the document, so
    /// every assembly that can contribute a component schema is already resident in this
    /// <see cref="AppDomain"/> -- keyed by simple type name, mirroring how the reflection-based
    /// OpenAPI schema generator derives a component schema id from a CLR type. Unlike a
    /// name-to-single-type map, every candidate for a shared simple name is kept so the caller can
    /// disambiguate using <see cref="IsEligibleForStrictComparison"/> instead of dropping the name
    /// outright.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<Type>> ResolveEnumTypeCandidatesByName()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic &&
                (assembly.GetName().Name?.StartsWith("Farm.", StringComparison.Ordinal) ?? false))
            .SelectMany(SafeGetTypes)
            .Where(type => type.IsEnum && (type.IsPublic || type.IsNestedPublic))
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<Type> (group) => group.Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="Assembly.GetTypes"/> can throw <see cref="ReflectionTypeLoadException"/> for a
    /// small number of assemblies in this solution whose dependencies are not fully loadable in the
    /// test host (see the analogous, broader guard in <c>NetworkDiscoveryServiceTests</c>, which
    /// catches at the whole-assembly level instead of recovering partially-loaded types);
    /// recovering the types that did load, rather than dropping the whole assembly, keeps the sweep
    /// as complete as possible.
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
