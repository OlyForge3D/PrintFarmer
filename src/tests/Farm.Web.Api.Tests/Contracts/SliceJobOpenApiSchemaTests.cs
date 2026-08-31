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
    /// <b>Fixed by issue #2283 (finding 4 of #2261).</b> <c>GET /api/slice/{id}</c>'s 200 response
    /// previously had no <c>content</c> key at all — literally zero schema information — while the
    /// #2238 corpus (<c>job.completed-populated.json</c>, <c>job.minimal-status.json</c>) proves a
    /// real, stable, well-defined JSON object shape is always returned. The action is now annotated
    /// with <c>[ProducesResponseType(typeof(SliceJobStatusResponse), 200)]</c>, matching the type its
    /// handler actually maps onto via <c>MapToPublicStatusResponse</c>.
    /// </summary>
    [Fact]
    public void GetSliceJob_ResponseSchema_ResolvesToSliceJobStatusResponse_MatchingCorpusShape()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/slice/{id}", "get");

        JsonElement response200 = operation.GetProperty("responses").GetProperty("200");
        _ = response200.TryGetProperty("content", out _).Should().BeTrue(
            "GET /api/slice/{id} now documents a 'content' key for its 200 response");

        JsonElement? responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation);
        _ = responseSchema.Should().NotBeNull(
            "the OpenAPI document now declares a 'content'/schema for the 200 response of GET /api/slice/{id}");

        _ = responseSchema!.Value.GetProperty("$ref").GetString().Should().Be("#/components/schemas/SliceJobStatusResponse",
            "the action is now annotated with [ProducesResponseType(typeof(SliceJobStatusResponse), 200)], " +
            "matching the type MapToPublicStatusResponse actually returns");

        JsonElement sliceJobStatusResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema.Value);
        IReadOnlySet<string> propertyNames = OpenApiSchemaTestSupport.GetPropertyNames(sliceJobStatusResponse);

        foreach (string fixtureFile in new[] { "job.completed-populated.json", "job.minimal-status.json" })
        {
            string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "slice-jobs", fixtureFile));
            using JsonDocument corpusFixture = JsonDocument.Parse(json);
            _ = corpusFixture.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
                $"the corpus fixture '{fixtureFile}' proves a real, well-defined JSON object is returned");

            foreach (JsonProperty corpusProperty in corpusFixture.RootElement.EnumerateObject())
            {
                _ = propertyNames.Should().Contain(corpusProperty.Name,
                    $"the documented schema should describe every property the corpus fixture '{fixtureFile}' proves is on the wire");
            }
        }
    }
}
