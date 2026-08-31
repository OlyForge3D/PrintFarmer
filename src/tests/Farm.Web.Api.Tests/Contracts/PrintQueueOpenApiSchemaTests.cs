using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2242: asserts the OpenAPI document's declared response schema for the
/// <c>print-queue</c> family (<c>GET /api/job-queue</c>, consumed by iOS's
/// <c>JobService.swift</c>) against the real wire payloads captured by the #2238 canonical
/// corpus (read-only; never modified here).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class PrintQueueOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// Positive check: the 200 response is a JSON array of <c>QueueOverviewDto</c>, and its
    /// property-name set exactly matches the corpus-proven property set (always-present
    /// properties from <c>queue.populated.json</c> plus the schema's own nullable-typed
    /// properties, which the populated fixture's scenario happened not to exercise).
    /// </summary>
    [Fact]
    public void GetQueue_ResponseSchema_IsArrayOfQueueOverviewDto_WithPropertyNamesMatchingCorpus()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;

        _ = OpenApiSchemaTestSupport.GetTypes(responseSchema).Should().Contain("array");
        JsonElement items = responseSchema.GetProperty("items");
        _ = items.GetProperty("$ref").GetString().Should().Be("#/components/schemas/QueueOverviewDto");

        JsonElement queueOverviewDto = OpenApiSchemaTestSupport.ResolveRef(_document, items);
        var expectedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // Always present, per corpus queue.populated.json:
            "printerId", "printerName", "printerModel", "modelAliases", "isAvailable",
            "queuedJobsCount", "supportedMaterials",
            // Nullable-typed schema properties not exercised by the populated fixture's scenario:
            "currentJobId", "currentJobName", "estimatedCompletionTime", "nozzleDiameter",
        };
        _ = OpenApiSchemaTestSupport.GetPropertyNames(queueOverviewDto).Should().BeEquivalentTo(expectedPropertyNames);
    }

    /// <summary>
    /// <b>Characterizes a confirmed mismatch (finding).</b> <c>QueueOverviewDto</c> has no
    /// "required" keyword at all — meaning, per JSON Schema semantics, the OpenAPI document
    /// asserts <em>nothing</em> is guaranteed present, including <c>printerId</c>/
    /// <c>printerName</c>/<c>printerModel</c>/<c>isAvailable</c>/<c>queuedJobsCount</c>, which the
    /// #2238 corpus's populated fixture proves are always emitted with non-null values for a
    /// registered printer. This is the opposite-direction defect from the <c>tasks</c> family's
    /// over-required <c>UserTaskDto</c> (see <c>TasksOpenApiSchemaTests</c>): the same root
    /// misconfiguration (schema generation not honoring the MVC-only JSON options) produces
    /// under-declaration here rather than over-declaration, depending on the DTO's own nullable
    /// reference-type annotations.
    /// </summary>
    [Fact]
    public void GetQueue_ItemSchema_HasNoRequiredList_DespiteCorpusProvingSomePropertiesAreAlwaysPresent()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        JsonElement queueOverviewDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema.GetProperty("items"));

        _ = OpenApiSchemaTestSupport.GetRequiredSet(queueOverviewDto).Should().BeEmpty(
            "the schema currently declares no 'required' properties at all for QueueOverviewDto");

        string populatedJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "print-queue", "queue.populated.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(populatedJson);
        JsonElement firstItem = corpusFixture.RootElement[0];
        foreach (string alwaysPresent in new[] { "printerId", "printerName", "printerModel", "isAvailable", "queuedJobsCount" })
        {
            _ = firstItem.TryGetProperty(alwaysPresent, out _).Should().BeTrue(
                $"the corpus proves '{alwaysPresent}' is always present for a registered printer, even though the " +
                "schema's empty 'required' list documents nothing as guaranteed");
        }
    }

    /// <summary>
    /// Positive check: <c>supportedMaterials</c> is a plain string array (not an enum component),
    /// which correctly matches the corpus's plain string tokens (<c>"PLA"</c>, <c>"PETG"</c>).
    /// </summary>
    [Fact]
    public void GetQueue_SupportedMaterials_ItemSchema_IsPlainStringMatchingCorpusTokenShape()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        JsonElement queueOverviewDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema.GetProperty("items"));
        JsonElement supportedMaterials = OpenApiSchemaTestSupport.GetProperty(queueOverviewDto, "supportedMaterials");
        JsonElement itemSchema = supportedMaterials.GetProperty("items");

        _ = OpenApiSchemaTestSupport.GetTypes(itemSchema).Should().BeEquivalentTo(new[] { "string" });
        _ = OpenApiSchemaTestSupport.GetEnumTokens(itemSchema).Should().BeNull(
            "supportedMaterials is a freeform string list server-side, not a C# enum, so no enum token list is expected");

        string populatedJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "print-queue", "queue.populated.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(populatedJson);
        JsonElement corpusMaterials = corpusFixture.RootElement[0].GetProperty("supportedMaterials");
        foreach (JsonElement material in corpusMaterials.EnumerateArray())
        {
            _ = material.ValueKind.Should().Be(JsonValueKind.String);
        }
    }
}
