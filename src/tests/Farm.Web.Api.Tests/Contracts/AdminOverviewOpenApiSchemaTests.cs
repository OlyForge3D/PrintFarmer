using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2242: asserts the OpenAPI document's declared response schema for the
/// <c>admin-overview</c> family (<c>GET /api/admin/overview</c>) against the real wire payloads
/// captured by the #2238 canonical corpus (read-only; never modified here).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class AdminOverviewOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// <b>Characterizes a confirmed mismatch (finding), the most self-contradicting instance of
    /// this issue's systemic root cause.</b> <c>SubsystemStatus</c> and <c>AttentionSeverity</c>
    /// are each documented as a plain <c>integer</c> with no "enum" token list — the same
    /// MVC-only-global-converter root cause as the <c>tasks</c> family — but each component
    /// schema's own XML-doc-derived "description" text literally states the type "is serialized
    /// as a string via JsonStringEnumConverter", directly contradicting its own sibling "type"
    /// keyword one line away in the same schema object. This self-contradiction is quoted into the
    /// assertion failure message so it is visible without cross-referencing the source.
    /// </summary>
    [Theory]
    [InlineData("SubsystemStatus")]
    [InlineData("AttentionSeverity")]
    public void EnumComponentSchema_IsIntegerTyped_ContradictingItsOwnDescriptionText(string componentSchemaName)
    {
        JsonElement schema = OpenApiSchemaTestSupport.GetComponentSchema(_document, componentSchemaName);
        string description = schema.TryGetProperty("description", out JsonElement descriptionElement)
            ? descriptionElement.GetString() ?? string.Empty
            : string.Empty;

        _ = OpenApiSchemaTestSupport.GetTypes(schema).Should().BeEquivalentTo(new[] { "integer" },
            $"'{componentSchemaName}' relies solely on the MVC-only global JsonStringEnumConverter, which OpenAPI " +
            $"schema generation does not consult, even though its own description text says: \"{description}\"");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(schema).Should().BeNull();
    }

    /// <summary>
    /// Positive check, added in response to review feedback: confirms
    /// <c>GET /api/admin/overview</c>'s 200 response schema actually <c>$ref</c>s
    /// <c>AdminOverviewDto</c>. Without this, the other tests in this file inspect the
    /// <c>AdminOverviewDto</c>/<c>AttentionItemDto</c> component schemas in isolation and would
    /// still pass even if the operation's response were missing or bound to the wrong schema.
    /// </summary>
    [Fact]
    public void GetAdminOverview_ResponseSchema_ReferencesAdminOverviewDto()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/admin/overview", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;

        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/AdminOverviewDto");
    }

    /// <summary>
    /// Positive check: <c>AdminOverviewDto</c>'s top-level "required" list correctly matches the
    /// corpus's always-present top-level keys.
    /// </summary>
    [Fact]
    public void AdminOverviewDto_RequiredList_MatchesCorpusAlwaysPresentTopLevelKeys()
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "AdminOverviewDto");
        var expected = new HashSet<string>(StringComparer.Ordinal) { "checkedAt", "overallStatus", "subsystems", "attention" };

        _ = OpenApiSchemaTestSupport.GetRequiredSet(dto).Should().BeEquivalentTo(expected);

        string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "admin-overview", "overview.live-shape.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(json);
        foreach (string key in expected)
        {
            _ = corpusFixture.RootElement.TryGetProperty(key, out _).Should().BeTrue(
                $"the corpus proves '{key}' is always present, matching the schema's correct 'required' declaration");
        }
    }

    /// <summary>
    /// Positive check: <c>AttentionItemDto</c>'s "required" list correctly excludes the three
    /// nullable, genuinely-optional properties (<c>actionLabel</c>, <c>actionDestinationId</c>,
    /// <c>actionRoute</c>) while requiring the four always-present ones.
    /// </summary>
    [Fact]
    public void AttentionItemDto_RequiredList_CorrectlyExcludesTheThreeOptionalActionProperties()
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, "AttentionItemDto");
        var expectedRequired = new HashSet<string>(StringComparer.Ordinal) { "key", "severity", "title", "detail" };
        var expectedOptional = new[] { "actionLabel", "actionDestinationId", "actionRoute" };

        IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(dto);
        _ = required.Should().BeEquivalentTo(expectedRequired);
        foreach (string optionalProperty in expectedOptional)
        {
            _ = required.Should().NotContain(optionalProperty);
            JsonElement property = OpenApiSchemaTestSupport.GetProperty(dto, optionalProperty);
            _ = OpenApiSchemaTestSupport.IsNullable(property).Should().BeTrue(
                $"'{optionalProperty}' is correctly typed nullable since it is genuinely optional");
        }
    }
}
