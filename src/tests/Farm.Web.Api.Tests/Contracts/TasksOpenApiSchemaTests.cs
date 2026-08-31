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
    /// <b>Characterizes a confirmed mismatch (finding).</b> <c>GET /api/tasks</c> with no <c>view</c>
    /// query parameter is documented by its own operation summary as preserving "the existing flat
    /// list contract" — and the #2238 corpus (<c>tasks.empty-collection.json</c>, captured via a real
    /// HTTP round trip with no <c>view</c> parameter) proves the real default-view payload is a JSON
    /// <em>array</em>. The OpenAPI document nonetheless declares a single <c>ShiftPlanDto</c> object
    /// schema for the operation's only documented 200 response — because native OpenAPI records one
    /// response schema per operation regardless of query-string-driven branching in the handler. A
    /// client generated from this document would be typed for the <c>view=shift</c> response shape
    /// even when calling the default (and far more common) flat-list variant.
    /// </summary>
    [Fact]
    public void GetTasks_DefaultViewResponseSchema_IsDocumentedAsShiftPlanObject_NotTheFlatArrayItActuallyReturns()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "get");
        JsonElement? responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation);
        _ = responseSchema.Should().NotBeNull("the operation documents a 200 response with a JSON schema");

        _ = responseSchema!.Value.GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ShiftPlanDto",
                "the document's only schema for GET /api/tasks is the shift-plan (view=shift) shape");

        string emptyCollectionJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "tasks", "tasks.empty-collection.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(emptyCollectionJson);
        _ = corpusFixture.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the #2238 corpus proves the real default-view (no 'view' query parameter) payload is a flat JSON array, " +
            "not the ShiftPlanDto object the OpenAPI schema documents");
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
    /// <b>Characterizes a confirmed mismatch (finding).</b> The <c>UserTaskDto</c> schema's
    /// "required" list names every one of its 18 properties — including the 8 that the #2238
    /// corpus proves are routinely <em>absent</em> from the wire payload when null (the API's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> MVC option, which — like the enum-string
    /// converter — has no influence on OpenAPI schema generation; only the separate
    /// <c>ConfigureHttpJsonOptions</c> options object does, and it sets neither). A client
    /// generated from this schema would treat these 8 properties as always-present non-nullable
    /// values, when the real payload routinely omits them entirely.
    /// </summary>
    [Fact]
    public void CreateTask_ResponseSchema_RequiredList_IncludesPropertiesCorpusProvesAreOmittedWhenNull()
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

        foreach (string propertyName in corpusProvenOmittable)
        {
            _ = required.Should().Contain(propertyName,
                $"the schema currently (incorrectly) marks '{propertyName}' required even though the corpus " +
                "proves it is omitted from the wire payload when null");
        }
    }

    /// <summary>
    /// <b>Characterizes a confirmed mismatch (finding), root cause.</b> <c>status</c>/<c>priority</c>/
    /// <c>taskType</c> rely solely on the global <c>JsonStringEnumConverter</c> registered in
    /// <c>ControllerStartup</c>'s MVC-only JSON options — which OpenAPI schema generation never
    /// consults. Each component schema is therefore documented as a plain <c>integer</c> with no
    /// "enum" token list, even though the #2238 corpus proves the real wire value is always a
    /// PascalCase string token (e.g. <c>"status": "Pending"</c>).
    /// </summary>
    [Theory]
    [InlineData("status", "UserTaskStatus")]
    [InlineData("priority", "UserTaskPriority")]
    [InlineData("taskType", "UserTaskType")]
    public void CreateTask_ResponseSchema_PlainEnumProperties_AreDocumentedAsIntegerNotString(string propertyName, string componentSchemaName)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(userTaskDto, propertyName);
        _ = property.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{componentSchemaName}");
        JsonElement enumSchema = OpenApiSchemaTestSupport.ResolveRef(_document, property);

        _ = OpenApiSchemaTestSupport.GetTypes(enumSchema).Should().BeEquivalentTo(new[] { "integer" },
            $"'{componentSchemaName}' relies solely on the MVC-only global JsonStringEnumConverter, which OpenAPI " +
            "schema generation (ConfigureHttpJsonOptions) does not consult");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(enumSchema).Should().BeNull(
            "an integer-typed schema with no property-level converter carries no string enum token list");
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
