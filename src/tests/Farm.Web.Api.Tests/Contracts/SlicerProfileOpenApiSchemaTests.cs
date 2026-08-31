using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2242: asserts the OpenAPI document's declared response schemas for the
/// <c>slicer-profiles</c> family (<c>GET/POST /api/slicer/profiles[/{id}]</c>) against the real
/// wire payloads captured by the #2238 canonical corpus (read-only; never modified here).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class SlicerProfileOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// <b>Fixed by issue #2283 (finding 5 of #2261).</b> <c>GET /api/slicer/profiles</c>'s 200
    /// response schema previously was <c>{"type":"array"}</c> with no <c>items</c> key — the array
    /// element shape was completely undocumented. Per <c>ProfilesController.GetProfilesAsync</c>,
    /// this action actually returns a projection with a distinct property set (<c>id</c>/<c>name</c>
    /// both set to the process-profile name, <c>layerHeight</c>, <c>infillPercentage</c>,
    /// <c>printSpeed</c>, <c>nozzleTemperature</c>, <c>bedTemperature</c>, <c>supports</c>,
    /// <c>material</c>, <c>quality</c>) that does not match the sibling <c>ProcessProfileResponseDto</c>
    /// shape (returned by <c>POST /api/slicer/profiles</c> and <c>GET /api/slicer/profiles/{id}</c>
    /// below) — so reusing <c>ProcessProfileResponseDto</c> verbatim would misdocument the real
    /// payload. The action now returns a strongly-typed <c>ProcessProfileListEntryDto</c> projection
    /// and is annotated accordingly, giving the array element shape a named, accurate <c>$ref</c>
    /// the same way the sibling operations do.
    /// </summary>
    [Fact]
    public void GetProfilesList_ResponseSchema_IsArray_WithItemsResolvingToProcessProfileListEntryDto()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/slicer/profiles", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;

        _ = OpenApiSchemaTestSupport.GetTypes(responseSchema).Should().BeEquivalentTo(new[] { "array" });
        _ = responseSchema.TryGetProperty("items", out JsonElement items).Should().BeTrue(
                "GET /api/slicer/profiles now declares an 'items' schema for its array element");
        _ = items.GetProperty("$ref").GetString().Should().Be("#/components/schemas/ProcessProfileListEntryDto",
            "the array's element schema is now a named DTO matching the real projection the handler returns");

        JsonElement processProfileListEntryDto = OpenApiSchemaTestSupport.ResolveRef(_document, items);
        var expectedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "layerHeight", "infillPercentage", "printSpeed",
            "nozzleTemperature", "bedTemperature", "supports", "material", "quality",
        };
        _ = OpenApiSchemaTestSupport.GetPropertyNames(processProfileListEntryDto).Should().BeEquivalentTo(expectedPropertyNames,
            "the documented shape should match exactly what ProfilesController.GetProfilesAsync's projection returns");
    }

    /// <summary>
    /// Positive check: the sibling <c>GET /api/slicer/profiles/{id}</c> and
    /// <c>POST /api/slicer/profiles</c> operations both correctly declare a
    /// <c>ProcessProfileResponseDto</c> response schema, and every corpus-observed property name
    /// (populated + missing-key fixtures combined, since no single fixture exercises every
    /// property) appears in the schema. The schema additionally declares four nullable
    /// printer/model-scoping properties (<c>printerModelId</c>, <c>printerModelName</c>,
    /// <c>specificPrinterId</c>, <c>specificPrinterName</c>) that no current corpus fixture
    /// exercises — none of the captured scenarios happen to be a printer- or model-scoped
    /// profile — so this asserts a superset relationship rather than exact equality.
    /// </summary>
    [Theory]
    [InlineData("/api/slicer/profiles/{id}", "get", "200")]
    [InlineData("/api/slicer/profiles", "post", "201")]
    public void ProfileResponseSchema_ResolvesToProcessProfileResponseDto_WithPropertyNamesMatchingCorpus(string path, string method, string statusCode)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, path, method);
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode)!.Value;
        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/ProcessProfileResponseDto");

        JsonElement processProfileResponseDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);

        var corpusPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fixtureFile in new[] { "profiles.populated.json", "profiles.missing-key.json" })
        {
            string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "slicer-profiles", fixtureFile));
            using JsonDocument corpusFixture = JsonDocument.Parse(json);
            JsonElement profileObject = corpusFixture.RootElement.ValueKind == JsonValueKind.Array
                ? corpusFixture.RootElement[0]
                : corpusFixture.RootElement;
            foreach (JsonProperty property in profileObject.EnumerateObject())
            {
                _ = corpusPropertyNames.Add(property.Name);
            }
        }

        IReadOnlySet<string> schemaPropertyNames = OpenApiSchemaTestSupport.GetPropertyNames(processProfileResponseDto);
        _ = schemaPropertyNames.Should().Contain(corpusPropertyNames,
            "every property the corpus proves is emitted on the wire should also appear in the schema");

        string[] printerScopingPropertiesNoCorpusFixtureExercises =
        [
            "printerModelId", "printerModelName", "specificPrinterId", "specificPrinterName",
        ];
        foreach (string propertyName in printerScopingPropertiesNoCorpusFixtureExercises)
        {
            JsonElement property = OpenApiSchemaTestSupport.GetProperty(processProfileResponseDto, propertyName);
            _ = OpenApiSchemaTestSupport.IsNullable(property).Should().BeTrue(
                $"'{propertyName}' is correctly nullable-typed even though no corpus fixture exercises a " +
                "printer/model-scoped profile scenario");
        }

        _ = schemaPropertyNames.Should().BeEquivalentTo(
            corpusPropertyNames.Concat(printerScopingPropertiesNoCorpusFixtureExercises));
    }

    /// <summary>
    /// Positive check: <c>material</c>/<c>quality</c> are correctly documented as plain
    /// <c>type: string</c> (not $ref'd enum components), matching the real DTO design — these are
    /// plain strings server-side, not C# enums, so no enum-schema divergence applies here.
    /// </summary>
    [Theory]
    [InlineData("material")]
    [InlineData("quality")]
    public void ProcessProfileResponseDto_MaterialAndQuality_ArePlainStrings_NotEnumRefs(string propertyName)
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "ProcessProfileResponseDto");
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(dto, propertyName);

        _ = property.TryGetProperty("$ref", out _).Should().BeFalse();
        _ = OpenApiSchemaTestSupport.GetTypes(property).Should().Contain("string");
    }
}
