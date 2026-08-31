using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2242: asserts the OpenAPI document's declared response schema for the <c>tasks</c>
/// family (<c>GET/POST /api/tasks</c>, consumed by React's <c>tasksApi.ts</c> and iOS's
/// <c>ShiftTaskService.swift</c>) against the real wire payloads captured by the #2238 canonical
/// corpus (read-only; never modified here). These are characterization tests: several assertions
/// below document a currently-real generator-readiness defect rather than the ideal/fixed
/// behavior, per this issue's file-ownership boundary (no production-code changes from this test
/// child). Each confirmed defect is filed as its own linked finding issue — see the class remarks
/// on each test.
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class TasksOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// <b>Fixed by issue #2283 (finding 3 of #2261).</b> <c>GET /api/tasks</c> with no <c>view</c>
    /// query parameter is documented by its own operation summary as preserving "the existing flat
    /// list contract" — and the #2238 corpus (<c>tasks.empty-collection.json</c>, captured via a real
    /// HTTP round trip with no <c>view</c> parameter) proves the real default-view payload is a JSON
    /// <em>array</em>. Native OpenAPI generation records only one response schema per status code, and
    /// resolves it from the last-declared <c>[ProducesResponseType(200)]</c> attribute on the action;
    /// the action's attributes were reordered so the <c>UserTaskDto</c> array (declared last) — not
    /// <c>ShiftPlanDto</c> — is now the documented 200 schema, matching what the default (no
    /// <c>view</c> parameter) call path actually returns.
    /// </summary>
    [Fact]
    public void GetTasks_DefaultViewResponseSchema_IsDocumentedAsFlatArray_MatchingWhatItActuallyReturns()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "get");
        JsonElement? responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation);
        _ = responseSchema.Should().NotBeNull("the operation documents a 200 response with a JSON schema");

        _ = OpenApiSchemaTestSupport.GetTypes(responseSchema!.Value).Should().Contain("array",
            "GET /api/tasks now documents its 200 response as a flat JSON array, matching the default-view payload");

        JsonElement items = responseSchema.Value.GetProperty("items");
        _ = items.GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/UserTaskDto",
                "the array's element schema should be the same UserTaskDto sibling operations use");

        string emptyCollectionJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "tasks", "tasks.empty-collection.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(emptyCollectionJson);
        _ = corpusFixture.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the #2238 corpus proves the real default-view (no 'view' query parameter) payload is a flat JSON array, " +
            "which the OpenAPI schema now correctly documents");
    }

    /// <summary>
    /// Positive check: <c>POST /api/tasks</c>'s 201 response schema correctly resolves to
    /// <c>UserTaskDto</c>, and its property-name set is exactly the corpus-proven property set
    /// (both the always-present and the corpus-proven omittable-when-null properties).
    /// </summary>
    [Fact]
    public void CreateTask_ResponseSchema_PropertyNames_MatchCorpusPropertySet()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);

        var expectedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // Always present, per corpus tasks.populated.json:
            "id", "taskType", "entityType", "entityId", "title", "status", "priority",
            "createdAt", "relatedEntityCount", "anchorKind", "sourceKind",
            // Corpus-proven omittable-when-null (JsonContractAssertions.AssertMissingKey in
            // TasksContractTests.CreateThenGetTask_Populated_MatchesCorpus):
            "description", "dueAt", "completedAt", "metadataJson",
            "anchorAtUtc", "windowStartUtc", "windowEndUtc", "sourceId",
        };

        _ = OpenApiSchemaTestSupport.GetPropertyNames(userTaskDto).Should().BeEquivalentTo(expectedPropertyNames);
    }

    /// <summary>
    /// <b>Fixed by issue #2261.</b> The <c>UserTaskDto</c> schema's "required" list no longer
    /// names the 8 properties that the #2238 corpus proves are routinely <em>absent</em> from
    /// the wire payload when null: <c>ConfigureHttpJsonOptions</c> now mirrors MVC's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c>, and <c>UserTaskDto</c> was converted from
    /// a positional record (whose constructor parameters are all non-optional, and therefore all
    /// "required" regardless of nullability) to a property-only record that marks only the 11
    /// always-present properties <c>required</c>.
    /// </summary>
    [Fact]
    public void CreateTask_ResponseSchema_RequiredList_ExcludesPropertiesCorpusProvesAreOmittedWhenNull()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);

        IReadOnlySet<string> required = OpenApiSchemaTestSupport.GetRequiredSet(userTaskDto);
        string[] corpusProvenOmittable =
        [
            "description", "dueAt", "completedAt", "metadataJson",
            "anchorAtUtc", "windowStartUtc", "windowEndUtc", "sourceId",
        ];
        string[] corpusProvenAlwaysPresent =
        [
            "id", "taskType", "entityType", "entityId", "title", "status", "priority",
            "createdAt", "relatedEntityCount", "anchorKind", "sourceKind",
        ];

        foreach (string propertyName in corpusProvenOmittable)
        {
            _ = required.Should().NotContain(propertyName,
                $"the corpus proves '{propertyName}' is omitted from the wire payload when null");
        }

        _ = required.Should().BeEquivalentTo(corpusProvenAlwaysPresent,
            "these are exactly the properties the corpus proves are always present on the wire");
    }

    /// <summary>
    /// <b>Fixed by issue #2261.</b> Before the fix, <c>status</c>/<c>priority</c>/<c>taskType</c>
    /// were documented as a plain <c>type: integer</c> with no "enum" token list at all. Now that
    /// the global <c>JsonStringEnumConverter</c> is also registered on
    /// <c>ConfigureHttpJsonOptions</c> (previously MVC-only, via <c>ControllerStartup</c>), each
    /// component schema is documented purely via its "enum" token list — .NET's OpenAPI schema
    /// exporter's convention for a converter-driven string enum: no explicit "type" keyword, since
    /// the string-typed "enum" array is self-describing — matching the #2238 corpus's real wire
    /// value (e.g. <c>"status": "Pending"</c>).
    /// </summary>
    [Theory]
    [InlineData("status", "UserTaskStatus", new[] { "Pending", "InProgress", "Completed", "Dismissed", "Skipped" })]
    [InlineData("priority", "UserTaskPriority", new[] { "Low", "Normal", "High" })]
    [InlineData("taskType", "UserTaskType", new[]
    {
        "None", "ProfileImport", "MaintenanceDue", "FirmwareUpdate", "CalibrationNeeded", "Custom",
        "FailureClear", "HarvestReady", "FilamentRunout", "MaintenanceInIdleWindow", "SpoolRestock", "PrintedPartRestock",
    })]
    public void CreateTask_ResponseSchema_PlainEnumProperties_AreDocumentedAsStringWithEnumTokens(string propertyName, string componentSchemaName, string[] expectedTokens)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(userTaskDto, propertyName);
        _ = property.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{componentSchemaName}");
        JsonElement enumSchema = OpenApiSchemaTestSupport.ResolveRef(_document, property);

        _ = OpenApiSchemaTestSupport.GetTypes(enumSchema).Should().BeEmpty(
            $"'{componentSchemaName}' now relies on the global JsonStringEnumConverter registered on both " +
            "ConfigureHttpJsonOptions and MVC's AddJsonOptions");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(enumSchema).Should().BeEquivalentTo(expectedTokens,
            "the schema now lists every enum member as a string token");
    }

    /// <summary>
    /// <b>Characterizes a confirmed mismatch (finding), a distinct sub-case of the same root cause.</b>
    /// <c>anchorKind</c>/<c>sourceKind</c> use PROPERTY-level <c>[JsonConverter]</c> attributes
    /// (issue #2246) that outrank the global converter for the real wire value (lowercase camelCase
    /// tokens, e.g. <c>"unspecified"</c>), but .NET's reflection-based OpenAPI schema generator does
    /// not inspect property-level converters when producing a schema for the referenced enum type.
    /// The resulting component schema carries no "type" or "enum" keyword at all — completely
    /// unconstrained — which is worse for a generated client than the integer-typed case above: it
    /// would see no schema information whatsoever for these two properties.
    /// </summary>
    [Theory]
    [InlineData("anchorKind", "UserTaskAnchorKind")]
    [InlineData("sourceKind", "UserTaskSourceKind")]
    public void CreateTask_ResponseSchema_PropertyLevelConverterEnums_HaveNoTypeConstraintAtAll(string propertyName, string componentSchemaName)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(userTaskDto, propertyName);
        _ = property.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{componentSchemaName}");
        JsonElement enumSchema = OpenApiSchemaTestSupport.ResolveRef(_document, property);

        _ = OpenApiSchemaTestSupport.GetTypes(enumSchema).Should().BeEmpty(
            $"'{componentSchemaName}' is emitted by a property-level [JsonConverter] attribute the schema generator " +
            "does not inspect, so the component schema has no 'type' keyword at all");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(enumSchema).Should().BeNull();
    }
}
