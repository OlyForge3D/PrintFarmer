using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2242: asserts the OpenAPI document's declared response schema for the
/// <c>slice-jobs</c> family (<c>GET /api/slice/{id}</c>) against the real wire payloads captured
/// by the #2238 canonical corpus (read-only; never modified here).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class SliceJobOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// <b>Characterizes a confirmed mismatch (finding), the sharpest of this issue's findings.</b>
    /// <c>GET /api/slice/{id}</c>'s 200 response is documented with no <c>content</c> key at
    /// all — i.e. literally zero schema information, not even an untyped placeholder — while the
    /// #2238 corpus (<c>job.completed-populated.json</c>, <c>job.minimal-status.json</c>) proves a
    /// real, stable, well-defined JSON object shape is always returned. A client generated from
    /// this document would have no return type at all for this operation.
    /// </summary>
    [Fact]
    public void GetSliceJob_ResponseSchema_IsCompletelyUndocumented_DespiteCorpusProvingAStableShapeExists()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/slice/{id}", "get");

        JsonElement response200 = operation.GetProperty("responses").GetProperty("200");
        _ = response200.TryGetProperty("content", out _).Should().BeFalse(
            "the 200 response for GET /api/slice/{id} should have no 'content' key at all — not merely a " +
            "'content' object missing an 'application/json' entry — to support the claim that the schema " +
            "information is completely absent, not just absent for one particular media type");

        JsonElement? responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation);
        _ = responseSchema.Should().BeNull(
            "the OpenAPI document currently has no 'content'/schema at all for the 200 response of GET /api/slice/{id}");

        foreach (string fixtureFile in new[] { "job.completed-populated.json", "job.minimal-status.json" })
        {
            string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "slice-jobs", fixtureFile));
            using JsonDocument corpusFixture = JsonDocument.Parse(json);
            _ = corpusFixture.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
                $"the corpus fixture '{fixtureFile}' proves a real, well-defined JSON object is actually returned, " +
                "even though the OpenAPI document declares no schema for it at all");
        }
    }
}
