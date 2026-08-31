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
    /// <b>Fixed by issue #2261/#2282.</b> Before the fix, <c>status</c>/<c>priority</c>/<c>taskType</c>
    /// were documented as a plain <c>type: integer</c> with no "enum" token list at all. Once the
    /// global <c>JsonStringEnumConverter</c> was also registered on <c>ConfigureHttpJsonOptions</c>
    /// (previously MVC-only, via <c>ControllerStartup</c>), each component schema's "enum" token
    /// list was populated correctly, but .NET's OpenAPI schema exporter has a confirmed limitation
    /// (dotnet/aspnetcore#61303, #62022) that leaves the schema's own "type" keyword unset even
    /// though "enum" is present. <c>EnumSchemaTypeStringTransformer</c> (registered in
    /// <c>Program.cs</c>) now adds the missing <c>type: string</c>, so the schema matches the
    /// #2238 corpus's real wire value (e.g. <c>"status": "Pending"</c>) with both keywords present.
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

        _ = OpenApiSchemaTestSupport.GetTypes(enumSchema).Should().BeEquivalentTo(new[] { "string" },
            $"'{componentSchemaName}' is now constrained by EnumSchemaTypeStringTransformer");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(enumSchema).Should().BeEquivalentTo(expectedTokens,
            "the schema now lists every enum member as a string token");
    }

    /// <summary>
    /// <b>Fixed by issue #2282 (finding 2 from #2261).</b> <c>anchorKind</c>/<c>sourceKind</c> use a
    /// custom <c>[JsonConverter]</c> (issue #2246, applied both on the enum declaration itself and
    /// again on each referencing property) for their real wire value (lowercase camelCase tokens,
    /// e.g. <c>"unspecified"</c>), but .NET's reflection-based OpenAPI schema generator only knows
    /// how to introspect the standard <c>JsonStringEnumConverter</c> when producing an enum's
    /// component schema -- any other custom converter, at either placement, is opaque to it, since
    /// enumerating its real output would require executing arbitrary converter code. This
    /// previously left the component schema with no "type" or "enum" keyword at all (see the
    /// corpus-proven wire tokens in <c>TasksContractTests</c>). <c>CustomConverterEnumSchemaTransformer</c>
    /// now constrains both component schemas directly from <c>UserTaskAnchorKindJsonConverter.ToWire</c>/
    /// <c>UserTaskSourceKindJsonConverter.ToWire</c>, so the documented shape matches the real wire
    /// contract and can never silently drift from the converter that actually serializes these
    /// properties.
    /// </summary>
    [Theory]
    [InlineData("anchorKind", "UserTaskAnchorKind", new[] { "unspecified", "now", "at", "window", "anytimeToday", "timeline" })]
    [InlineData("sourceKind", "UserTaskSourceKind", new[]
    {
        "unspecified", "attention", "failureIncident", "harvest", "filamentCoverage", "maintenance",
        "spoolReorder", "printedPartStock",
    })]
    public void CreateTask_ResponseSchema_PropertyLevelConverterEnums_AreDocumentedAsStringWithMatchingEnumTokens(
        string propertyName, string componentSchemaName, string[] expectedTokens)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/tasks", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "201")!.Value;
        JsonElement userTaskDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement property = OpenApiSchemaTestSupport.GetProperty(userTaskDto, propertyName);
        _ = property.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{componentSchemaName}");
        JsonElement enumSchema = OpenApiSchemaTestSupport.ResolveRef(_document, property);

        _ = OpenApiSchemaTestSupport.GetTypes(enumSchema).Should().BeEquivalentTo(new[] { "string" },
            $"'{componentSchemaName}' is now constrained by CustomConverterEnumSchemaTransformer");
        _ = OpenApiSchemaTestSupport.GetEnumTokens(enumSchema).Should().BeEquivalentTo(expectedTokens,
            $"'{componentSchemaName}' should list the exact wire tokens its property-level converter emits");
    }
}
