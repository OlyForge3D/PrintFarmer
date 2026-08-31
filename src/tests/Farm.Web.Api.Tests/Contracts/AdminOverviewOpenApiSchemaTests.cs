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
    /// <b>Fixed by issue #2261/#2282.</b> Before the fix, <c>SubsystemStatus</c> and
    /// <c>AttentionSeverity</c> were documented as a plain <c>type: integer</c> with no "enum"
    /// token list at all, directly contradicting each schema's own XML-doc-derived "description"
    /// text (which already said the type "is serialized as a string via JsonStringEnumConverter"
    /// even before this fix). Registering the global <c>JsonStringEnumConverter</c> on
    /// <c>ConfigureHttpJsonOptions</c> fixed the "enum" token list, but .NET's OpenAPI schema
    /// exporter has a confirmed limitation (dotnet/aspnetcore#61303, #62022) that leaves the
    /// schema's own "type" keyword unset even though "enum" is present.
    /// <c>EnumSchemaTypeStringTransformer</c> (registered in <c>Program.cs</c>) now adds the
    /// missing <c>type: string</c>, so the schema matches both the description text and the
    /// corpus's real string wire values with both keywords present.
    /// </summary>
    [Theory]
    [InlineData("SubsystemStatus", new[] { "Healthy", "Degraded", "Unknown", "Unhealthy" })]
    [InlineData("AttentionSeverity", new[] { "Info", "Warning", "Error" })]
    public void EnumComponentSchema_IsStringTyped_MatchingItsOwnDescriptionText(string componentSchemaName, string[] expectedTokens)
    {
        JsonElement schema = OpenApiSchemaTestSupport.GetComponentSchema(_document, componentSchemaName);

        _ = OpenApiSchemaTestSupport.GetTypes(schema).Should().BeEquivalentTo(new[] { "string" },
            $"'{componentSchemaName}' is now constrained by EnumSchemaTypeStringTransformer, matching the " +
            "schema's own description text and the pre-existing 'enum' token list");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(schema).Should().BeEquivalentTo(expectedTokens);
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
