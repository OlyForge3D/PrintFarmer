using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2284: asserts the OpenAPI document's declared response schema for the
/// <c>print-jobs</c> family (<c>GET /api/job-queue/{id}</c>, consumed by iOS's
/// <c>PrintJob</c> model) against the real wire payloads captured by the #2238 canonical
/// corpus (read-only; never modified here). This is the single-job detail sibling of the
/// <c>print-queue</c> family's list operation already covered by
/// <see cref="PrintQueueOpenApiSchemaTests"/>.
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class PrintJobsOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// Positive check: the 200 response resolves to <c>JobQueuePrintJobDto</c>, and the
    /// schema's declared property-name set is a superset of every property the corpus proves
    /// is emitted on the wire (populated + minimal-missing-optional fixtures combined, since
    /// no single fixture exercises every optional member).
    /// </summary>
    [Fact]
    public void GetJob_ResponseSchema_ResolvesToJobQueuePrintJobDto_WithPropertyNamesCoveringCorpus()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/JobQueuePrintJobDto");

        JsonElement jobQueuePrintJobDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);

        var corpusPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fixtureFile in new[] { "job.populated.json", "job.minimal-missing-optional.json" })
        {
            string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "print-jobs", fixtureFile));
            using JsonDocument corpusFixture = JsonDocument.Parse(json);
            foreach (JsonProperty property in corpusFixture.RootElement.EnumerateObject())
            {
                _ = corpusPropertyNames.Add(property.Name);
            }
        }

        IReadOnlySet<string> schemaPropertyNames = OpenApiSchemaTestSupport.GetPropertyNames(jobQueuePrintJobDto);
        _ = schemaPropertyNames.Should().Contain(corpusPropertyNames,
            "every property the corpus proves is emitted on the wire should also appear in the schema");
    }

    /// <summary>
    /// Positive check: <c>priority</c>/<c>jobKind</c> resolve to their respective named enum
    /// components (not inlined string enums), and each component correctly declares a
    /// <c>type: string</c> <c>enum</c> token list containing the corpus's exact observed tokens
    /// (<c>Normal</c>/<c>Urgent</c>, <c>Standard</c>) -- both are globally converted by the
    /// standard <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>, so they are
    /// in scope for <see cref="OpenApiEnumFidelityTests"/>' document-wide strict sweep and are
    /// expected to be fully documented here, unlike <c>status</c> below.
    /// </summary>
    [Theory]
    [InlineData("priority", "PrintJobPriority", new[] { "Normal", "Urgent" })]
    [InlineData("jobKind", "JobKind", new[] { "Standard" })]
    public void JobQueuePrintJobDto_GloballyConvertedEnumProperty_ResolvesToNamedEnumComponent_WithCorpusObservedTokens(
        string propertyName, string componentSchemaName, string[] corpusObservedTokens)
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "JobQueuePrintJobDto");
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(dto, propertyName);

        JsonElement resolved = ResolveDirectOrNullableRef(property);
        JsonElement componentSchema = OpenApiSchemaTestSupport.GetComponentSchema(_document, componentSchemaName);
        _ = resolved.GetRawText().Should().Be(componentSchema.GetRawText(),
            $"'{propertyName}' should resolve (directly or via 'oneOf') to the '{componentSchemaName}' component schema");

        _ = OpenApiSchemaTestSupport.GetTypes(componentSchema).Should().Contain("string");
        IReadOnlyList<string>? enumTokens = OpenApiSchemaTestSupport.GetEnumTokens(componentSchema);
        _ = enumTokens.Should().NotBeNull($"'{componentSchemaName}' should declare an 'enum' token list");
        _ = enumTokens!.Should().Contain(corpusObservedTokens,
            $"every {componentSchemaName} token the corpus proves is emitted on the wire should be a declared enum member");
    }

    /// <summary>
    /// Characterization test, not a fidelity failure: <c>status</c> resolves to the
    /// <c>PrintJobStatus</c> component, but that schema is documented as a completely
    /// unconstrained <c>{}</c> (no "type", no "enum") because <c>PrintJobStatus</c> carries a
    /// bespoke <c>PrintJobStatusJsonConverter</c> (to permissively accept numeric or string wire
    /// forms) rather than the global <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
    /// -- the same category of intentional exclusion <see cref="OpenApiEnumFidelityTests"/>
    /// already carves out for <c>UserTaskAnchorKind</c>/<c>UserTaskSourceKind</c>. This asserts
    /// the operation still correctly names the component (so a reader at least knows which type
    /// backs the property) and locks in today's known, unconstrained shape rather than silently
    /// tolerating an unrelated regression.
    /// </summary>
    [Fact]
    public void JobQueuePrintJobDto_Status_ResolvesToPrintJobStatusComponent_WhichIsIntentionallyUnconstrained()
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "JobQueuePrintJobDto");
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(dto, "status");

        JsonElement resolved = ResolveDirectOrNullableRef(property);
        JsonElement componentSchema = OpenApiSchemaTestSupport.GetComponentSchema(_document, "PrintJobStatus");
        _ = resolved.GetRawText().Should().Be(componentSchema.GetRawText(),
            "'status' should resolve (directly or via 'oneOf') to the 'PrintJobStatus' component schema");

        _ = OpenApiSchemaTestSupport.GetTypes(componentSchema).Should().BeEmpty(
            "PrintJobStatus's custom converter leaves the schema with no 'type' keyword, matching " +
            "the same custom-converter exclusion OpenApiEnumFidelityTests already documents for " +
            "other bespoke-converter enums");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(componentSchema).Should().BeNull(
            "PrintJobStatus's custom converter leaves the schema with no 'enum' token list either");
    }

    /// <summary>Resolves a property schema that is either a direct <c>$ref</c> or a nullable <c>oneOf: [null, $ref]</c> pairing to its target component schema.</summary>
    private JsonElement ResolveDirectOrNullableRef(JsonElement property)
    {
        if (property.TryGetProperty("$ref", out _))
        {
            return OpenApiSchemaTestSupport.ResolveRef(_document, property);
        }

        JsonElement oneOf = property.GetProperty("oneOf");
        foreach (JsonElement variant in oneOf.EnumerateArray())
        {
            if (variant.TryGetProperty("$ref", out _))
            {
                return OpenApiSchemaTestSupport.ResolveRef(_document, variant);
            }
        }

        throw new InvalidOperationException("Property schema has neither a direct '$ref' nor a 'oneOf' variant with a '$ref'.");
    }

    /// <summary>
    /// Positive check: <c>toolRequirements</c>/<c>toolheadUsages</c> are arrays whose item schema
    /// resolves to the corpus-observed nested DTOs, and every corpus-observed nested property
    /// (<c>toolIndex</c>, <c>materialType</c> from the populated fixture's single tool
    /// requirement) is declared.
    /// </summary>
    [Fact]
    public void ToolRequirements_ItemSchema_ResolvesToPrintJobToolRequirementDto_WithCorpusObservedProperties()
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "JobQueuePrintJobDto");
        JsonElement toolRequirements = OpenApiSchemaTestSupport.GetProperty(dto, "toolRequirements");
        _ = OpenApiSchemaTestSupport.GetTypes(toolRequirements).Should().Contain("array");

        JsonElement itemSchema = OpenApiSchemaTestSupport.ResolveRef(_document, toolRequirements.GetProperty("items"));

        string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "print-jobs", "job.populated.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(json);
        JsonElement corpusToolRequirement = corpusFixture.RootElement.GetProperty("toolRequirements")[0];

        IReadOnlySet<string> schemaPropertyNames = OpenApiSchemaTestSupport.GetPropertyNames(itemSchema);
        foreach (JsonProperty property in corpusToolRequirement.EnumerateObject())
        {
            _ = schemaPropertyNames.Should().Contain(property.Name,
                $"'{property.Name}' is emitted on the wire for every tool requirement in the corpus");
        }
    }
}
