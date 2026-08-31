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
    /// <b>Fixed by issue #2261.</b> <c>QueueOverviewDto</c> now marks the 5 properties the
    /// #2238 corpus's populated fixture proves are always present for a registered printer
    /// (<c>printerId</c>, <c>printerName</c>, <c>printerModel</c>, <c>isAvailable</c>,
    /// <c>queuedJobsCount</c>) with the C# <c>required</c> modifier, so the generated schema's
    /// "required" list now names exactly them — the opposite-direction fix from the <c>tasks</c>
    /// family's previously over-required <c>UserTaskDto</c> (see <c>TasksOpenApiSchemaTests</c>).
    /// </summary>
    [Fact]
    public void GetQueue_ItemSchema_RequiredList_MatchesCorpusProvenAlwaysPresentProperties()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        JsonElement queueOverviewDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema.GetProperty("items"));

        string[] alwaysPresent = ["printerId", "printerName", "printerModel", "isAvailable", "queuedJobsCount"];
        _ = OpenApiSchemaTestSupport.GetRequiredSet(queueOverviewDto).Should().BeEquivalentTo(alwaysPresent,
            "the schema now declares exactly the properties the corpus proves are always present");

        string populatedJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "print-queue", "queue.populated.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(populatedJson);
        JsonElement firstItem = corpusFixture.RootElement[0];
        foreach (string propertyName in alwaysPresent)
        {
            _ = firstItem.TryGetProperty(propertyName, out _).Should().BeTrue(
                $"the corpus proves '{propertyName}' is always present for a registered printer");
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
