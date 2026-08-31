using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Farm.Infrastructure;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2273 (audit/characterization follow-up to #2261): a document-wide sweep proving
/// <see cref="Farm.Web.Api.Infrastructure.OpenApi.NullablePropertiesNotRequiredSchemaTransformer"/>
/// removed every nullable-typed property from every object schema's "required" list, plus
/// targeted checks against the four suspect DTOs the issue named
/// (<c>PrinterDetailsDto</c>, <c>ToolheadDto</c>, <c>SpoolmanSpoolDto</c>, <c>PrintJobDto</c>).
///
/// A repo-wide sweep of positional records under <c>src/infra/Dtos/</c> found 60 affected types
/// (constructor parameters that are nullable but lack a default value) -- far more than the ~30
/// the issue estimated -- so per-DTO wire-contract corpus proof (the #2261 <c>UserTaskDto</c>
/// pattern) was not attempted for every one individually. Instead, the fix is structural: every
/// nullable property is *always* omitted from the wire when null under the
/// <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> option that both
/// <c>ConfigureHttpJsonOptions</c> (native OpenAPI/minimal APIs) and <c>ControllerStartup.AddJsonOptions</c>
/// (MVC) already set, so it can never be "always present" and must never be "required" --
/// independent of which specific DTO it lives on. This document-wide sweep is the corresponding
/// document-wide proof: unlike a hand-picked per-DTO corpus test, it checks every reachable object
/// schema, present and future, using only the OpenAPI document's own self-consistency (a
/// "required" property whose own schema types it "null" is a structural contradiction the .NET
/// exporter should never produce once <see cref="Farm.Web.Api.Infrastructure.OpenApi.NullablePropertiesNotRequiredSchemaTransformer"/>
/// runs).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class RequiredListNullabilityFidelityTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    [Fact]
    public void EveryObjectComponentSchema_RequiredListExcludesNullableProperties()
    {
        JsonElement schemas = _document.RootElement.GetProperty("components").GetProperty("schemas");
        Dictionary<string, IReadOnlyList<Type>> candidatesByTypeName = ResolveFarmTypeCandidatesByName();
        var failures = new List<string>();
        int schemasWithRequiredProperties = 0;

        foreach (JsonProperty schemaProperty in schemas.EnumerateObject())
        {
            if (!candidatesByTypeName.TryGetValue(schemaProperty.Name, out IReadOnlyList<Type>? candidates)
                || candidates.Count != 1)
            {
                // Not a schema backed by exactly one loaded Farm.* CLR type -- either a
                // framework/BCL-generated schema (e.g. ASP.NET Core Identity's WebAuthn
                // KeyValuePair<string, T> shapes) whose own nullability metadata is outside this
                // repo's control, or a same-named ambiguity this sweep does not attempt to
                // disambiguate. Out of scope for issue #2273, which only concerns this repo's DTOs.
                continue;
            }

            JsonElement schema = schemaProperty.Value;
            IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(schema);
            if (required.Count == 0 || !schema.TryGetProperty("properties", out JsonElement properties))
            {
                continue;
            }

            schemasWithRequiredProperties++;

            foreach (string requiredName in required)
            {
                if (!properties.TryGetProperty(requiredName, out JsonElement property))
                {
                    // A "required" name with no matching "properties" entry is a different kind of
                    // document defect, outside this sweep's scope.
                    continue;
                }

                if (OpenApiSchemaTestSupport.IsNullable(property))
                {
                    failures.Add($"{schemaProperty.Name}.{requiredName}");
                }
            }
        }

        _ = schemasWithRequiredProperties.Should().BeGreaterThan(0,
            "the sweep should find at least one Farm.*-backed schema with a non-empty 'required' " +
            "list -- otherwise it is vacuously passing because the document fetch or type " +
            "resolution is broken, not because the fix works");

        _ = failures.Should().BeEmpty(
            "every reachable, Farm.*-backed object schema's 'required' list should exclude " +
            "nullable-typed properties, since DefaultIgnoreCondition = WhenWritingNull means a " +
            "nullable property is always omitted from the wire when its value is null (issue #2273):\n" +
            string.Join("\n", failures));
    }

    /// <summary>
    /// Every public (or nested-public) type across the already-loaded <c>Farm.*</c> assemblies --
    /// the shared <see cref="OpenApiDocumentFixture"/> already booted the full host to fetch the
    /// document, so every assembly that can contribute a component schema is already resident in
    /// this <see cref="AppDomain"/> -- keyed by simple type name, mirroring how the reflection-based
    /// OpenAPI schema generator derives a component schema id from a CLR type. Scoping the sweep to
    /// these types (rather than every component schema in the document) excludes framework/BCL
    /// schemas this repo does not own and cannot fix, e.g. ASP.NET Core Identity's WebAuthn
    /// <c>KeyValuePair&lt;string, AuthenticationExtensionsPRFValues&gt;</c> shape.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<Type>> ResolveFarmTypeCandidatesByName()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic &&
                (assembly.GetName().Name?.StartsWith("Farm.", StringComparison.Ordinal) ?? false))
            .SelectMany(SafeGetTypes)
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<Type> (group) => group.Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="Assembly.GetTypes"/> can throw <see cref="ReflectionTypeLoadException"/> for a
    /// small number of assemblies in this solution whose dependencies are not fully loadable in the
    /// test host; recovering the types that did load, rather than dropping the whole assembly,
    /// keeps the sweep as complete as possible.
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

    /// <summary>
    /// <c>PrinterDetailsDto</c>, one of the issue's named suspects: a 40+ parameter positional
    /// record where every parameter up to (but not including) the first defaulted one --
    /// <c>Backend = PrinterBackend.Moonraker</c> -- was forced "required" by the generator
    /// regardless of nullability. <c>Id</c> and <c>Name</c> are the only two of that prefix that
    /// are genuinely non-nullable and should remain required.
    /// </summary>
    [Fact]
    public void PrinterDetailsDto_RequiredList_ExcludesItsNullableProperties()
    {
        JsonElement schema = OpenApiSchemaTestSupport.GetComponentSchema(_document, "PrinterDetailsDto");
        IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(schema);

        string[] previouslyOverRequired =
        [
            "serverUrl", "notes", "manufacturerId", "manufacturerName", "modelId", "modelName",
            "modelMotionType", "modelMaxX", "modelMaxY", "modelMaxZ", "dateAcquired",
        ];
        foreach (string propertyName in previouslyOverRequired)
        {
            _ = required.Should().NotContain(propertyName,
                $"'{propertyName}' is a nullable constructor parameter with no default value; it is " +
                "always omitted from the wire when null and must not be documented as required");
        }

        _ = required.Should().Contain(["id", "name"],
            "the two genuinely non-nullable leading parameters should remain required");
    }

    /// <summary>
    /// <c>ToolheadDto</c>, one of the issue's named suspects. <c>id</c> and <c>index</c> are the
    /// only genuinely non-nullable parameters in the affected (non-defaulted) prefix; every other
    /// parameter up to the first defaulted one (<c>lastUpdated = null</c>) is nullable and was
    /// wrongly forced required.
    /// </summary>
    [Fact]
    public void ToolheadDto_RequiredList_ExcludesItsNullableProperties()
    {
        JsonElement schema = OpenApiSchemaTestSupport.GetComponentSchema(_document, "ToolheadDto");
        IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(schema);

        string[] previouslyOverRequired =
        [
            "name", "nozzleDiameter", "nozzleType", "maxFlowRate", "maxTemp",
            "hotendModelId", "hotendModelName", "extruderModelId", "extruderModelName",
            "toolheadModelDefId", "toolheadModelDefName", "nozzleModelId", "nozzleModelName",
            "supportedMaterials",
        ];
        foreach (string propertyName in previouslyOverRequired)
        {
            _ = required.Should().NotContain(propertyName,
                $"'{propertyName}' is a nullable constructor parameter with no default value; it is " +
                "always omitted from the wire when null and must not be documented as required");
        }

        _ = required.Should().Contain(["id", "index", "isPrimary"],
            "the genuinely non-nullable parameters in the affected prefix should remain required");
    }

    /// <summary>
    /// <c>SpoolmanSpoolDto</c>, one of the issue's named suspects. <c>remainingWeightG</c> and
    /// <c>colorHex</c> are the only two nullable, non-defaulted parameters; every other parameter
    /// in the affected prefix (<c>id</c>, <c>name</c>, <c>material</c>, <c>inUse</c>) is genuinely
    /// non-nullable.
    /// </summary>
    [Fact]
    public void SpoolmanSpoolDto_RequiredList_ExcludesItsNullableProperties()
    {
        JsonElement schema = OpenApiSchemaTestSupport.GetComponentSchema(_document, "SpoolmanSpoolDto");
        IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(schema);

        _ = required.Should().NotContain(["remainingWeightG", "colorHex"],
            "these are nullable constructor parameters with no default value; they are always " +
            "omitted from the wire when null and must not be documented as required");
        _ = required.Should().Contain(["id", "name", "material", "inUse"],
            "the genuinely non-nullable parameters in the affected prefix should remain required");
    }

    /// <summary>
    /// <c>PrintJobDto</c>, the issue's fourth named suspect -- but on inspection it is <b>not</b>
    /// actually affected, and is not currently reachable through any documented endpoint's schema
    /// at all (controllers return wrapper types such as <c>JobQueuePrintJobDto</c>/
    /// <c>QueuedPrintJobDto</c> instead), so it cannot be asserted against the live OpenAPI
    /// document the way the other three suspects are above. This is instead a reflection check
    /// against the positional record's own primary constructor, using the same
    /// <see cref="JsonPropertyInfo.IsGetNullable"/> signal the fix itself relies on (see
    /// <see cref="Farm.Web.Api.Infrastructure.OpenApi.NullablePropertiesNotRequiredSchemaTransformer"/>):
    /// every nullable parameter already carries an explicit <c>= null</c> default value, so .NET's
    /// generator would never have forced them into "required" in the first place (the bug only
    /// affects non-defaulted parameters) -- true independent of whether or where the type is ever
    /// serialized. This is a deliberate "ruled out" characterization result, not an oversight: the
    /// issue asks to "confirm (or rule out)" each suspect, and this one rules out.
    /// </summary>
    [Fact]
    public void PrintJobDto_WasAlreadyUnaffected_BecauseEveryNullableParameterHasADefault()
    {
        ConstructorInfo primaryConstructor = typeof(PrintJobDto).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        ParameterInfo[] parameters = primaryConstructor.GetParameters();

        JsonSerializerOptions options = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonTypeInfo typeInfo = options.GetTypeInfo(typeof(PrintJobDto));
        Dictionary<string, bool> isNullableByPropertyName = typeInfo.Properties
            .ToDictionary(p => p.Name, p => p.IsGetNullable, StringComparer.Ordinal);

        List<string> nullableParametersWithoutDefault = parameters
            .Where(p => isNullableByPropertyName.TryGetValue(p.Name!, out bool isNullable) && isNullable)
            .Where(p => !p.HasDefaultValue)
            .Select(p => p.Name!)
            .ToList();

        _ = nullableParametersWithoutDefault.Should().BeEmpty(
            "every nullable constructor parameter on PrintJobDto already carries an explicit " +
            "default value, so .NET's OpenAPI generator's 'no default => required' rule never " +
            "applied to it -- PrintJobDto is a ruled-out suspect, not a fix target");
    }
}
