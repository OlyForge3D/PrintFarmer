using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2294: asserts the OpenAPI document's declared 409 conflict schemas for
/// <c>POST /api/job-queue/{id}/harvest</c> and <c>POST /api/parts-inventory/{sku}/adjust</c>
/// against the real wire payloads captured by the corpus fixtures under
/// <c>fixtures/wire-contracts/api/inventory/</c> (read-only; never modified here). Before this
/// fix, both actions were annotated with the generic 5-property <c>ProblemDetails</c> fallback,
/// which hid the discriminator/context extension properties (<c>code</c>, <c>mismatches</c>,
/// <c>jobId</c>, <c>projectFileId</c>, <c>gcodeFileId</c>, <c>guidance</c>) the server actually
/// emits -- invisible to the OpenAPI schema and to any generated client (including iOS) even
/// though every consumer needs <c>code</c> to know how to interpret the conflict.
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class InventoryOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// The harvest endpoint's 409 response now <c>$ref</c>s <c>HarvestConflictResponse</c>
    /// (not the generic <c>ProblemDetails</c>), whose declared property set names exactly the
    /// six extension properties the corpus fixtures prove the server emits, with <c>code</c> as
    /// the only required member (it is the only property present in both wrongBin and
    /// partMappingRequired scenarios).
    /// </summary>
    [Fact]
    public void HarvestConflict_ResponseSchema_IsHarvestConflictResponse_WithCodeRequiredAndAllExtensionPropertiesDeclared()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;

        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/HarvestConflictResponse");

        JsonElement harvestConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        _ = OpenApiSchemaTestSupport.GetRequiredSet(harvestConflictResponse).Should().BeEquivalentTo(new[] { "code" },
            "code is the only property present in both the wrongBin and partMappingRequired corpus fixtures");

        var expectedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // Inherited from the base ProblemDetails shape:
            "type", "title", "status", "detail", "instance",
            // Issue #2294's new discriminator/context extension properties:
            "code", "mismatches", "jobId", "projectFileId", "gcodeFileId", "guidance",
        };
        _ = OpenApiSchemaTestSupport.GetPropertyNames(harvestConflictResponse).Should().BeEquivalentTo(expectedPropertyNames);
    }

    /// <summary>
    /// <c>code</c> is a plain, non-nullable string discriminator (no enum token list is declared
    /// server-side -- it is a plain <c>string</c> member, not a C# enum).
    /// </summary>
    [Fact]
    public void HarvestConflict_Code_IsNonNullableString()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;
        JsonElement harvestConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement code = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "code");

        _ = OpenApiSchemaTestSupport.GetTypes(code).Should().BeEquivalentTo(new[] { "string" });
        _ = OpenApiSchemaTestSupport.IsNullable(code).Should().BeFalse();
    }

    /// <summary>
    /// <c>mismatches</c> is an array of <c>WrongBinMismatchResponse</c>, matching the wrongBin
    /// corpus fixture's <c>mismatches</c> array shape.
    /// </summary>
    [Fact]
    public void HarvestConflict_Mismatches_IsArrayOfWrongBinMismatchResponse()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;
        JsonElement harvestConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement mismatches = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "mismatches");

        _ = OpenApiSchemaTestSupport.GetTypes(mismatches).Should().Contain("array");
        JsonElement items = mismatches.GetProperty("items");
        _ = items.GetProperty("$ref").GetString().Should().Be("#/components/schemas/WrongBinMismatchResponse");

        string wrongBinJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.wrong-bin.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(wrongBinJson);
        _ = corpusFixture.RootElement.TryGetProperty("mismatches", out JsonElement corpusMismatches).Should().BeTrue(
            "the wrongBin corpus fixture proves the server emits a 'mismatches' array for this code");
        _ = corpusMismatches.ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// <c>jobId</c>/<c>projectFileId</c>/<c>guidance</c> are the plain nullable types the
    /// partMappingRequired corpus fixture proves them to be (nullable UUID strings for the two
    /// IDs, nullable string for <c>guidance</c>); they are correctly omitted entirely -- rather
    /// than serialized as an explicit <c>null</c> -- for the wrongBin scenario, matching the
    /// default <c>WhenWritingNull</c> ignore-condition behavior.
    /// </summary>
    [Fact]
    public void HarvestConflict_PartMappingRequiredOnlyProperties_HaveCorpusMatchingTypes()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;
        JsonElement harvestConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);

        JsonElement jobId = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "jobId");
        _ = OpenApiSchemaTestSupport.GetTypes(jobId).Should().BeEquivalentTo(new[] { "string", "null" });
        _ = jobId.GetProperty("format").GetString().Should().Be("uuid");

        JsonElement projectFileId = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "projectFileId");
        _ = OpenApiSchemaTestSupport.GetTypes(projectFileId).Should().BeEquivalentTo(new[] { "string", "null" });
        _ = projectFileId.GetProperty("format").GetString().Should().Be("uuid");

        JsonElement guidance = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "guidance");
        _ = OpenApiSchemaTestSupport.GetTypes(guidance).Should().BeEquivalentTo(new[] { "string", "null" });

        string partMappingRequiredJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.part-mapping-required.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(partMappingRequiredJson);
        foreach (string propertyName in new[] { "jobId", "projectFileId", "guidance" })
        {
            _ = corpusFixture.RootElement.TryGetProperty(propertyName, out _).Should().BeTrue(
                $"the partMappingRequired corpus fixture proves '{propertyName}' is present for this code");
        }

        string wrongBinJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.wrong-bin.json"));
        using JsonDocument wrongBinFixture = JsonDocument.Parse(wrongBinJson);
        foreach (string propertyName in new[] { "jobId", "projectFileId", "guidance" })
        {
            _ = wrongBinFixture.RootElement.TryGetProperty(propertyName, out _).Should().BeFalse(
                $"the wrongBin corpus fixture proves '{propertyName}' is entirely absent (not present-as-null) for this code");
        }
    }

    /// <summary>
    /// <b>The one property with a real duality:</b> the partMappingRequired corpus fixture
    /// proves <c>gcodeFileId</c> must be present with an explicit JSON <c>null</c> when no gcode
    /// file exists yet, while the wrongBin fixture proves it is entirely absent for that code. A
    /// plain nullable <c>Guid?</c> cannot express both "explicitly null" and "entirely absent"
    /// under a single ignore-condition policy, so the property is typed as the <c>OptionalGuid</c>
    /// wrapper struct -- this asserts that its component schema (which the wrapper's custom
    /// <c>JsonConverter&lt;OptionalGuid&gt;</c> would otherwise leave undocumented) resolves to a
    /// plain nullable UUID string, exactly matching what actually appears on the wire, rather than
    /// the empty/typeless schema the native OpenAPI generator produces for any type with a custom
    /// converter it cannot introspect.
    /// </summary>
    [Fact]
    public void HarvestConflict_GcodeFileId_ResolvesToPlainNullableUuidString_NotOpaqueOptionalGuidRef()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;
        JsonElement harvestConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        JsonElement gcodeFileId = OpenApiSchemaTestSupport.GetProperty(harvestConflictResponse, "gcodeFileId");

        JsonElement resolved = OpenApiSchemaTestSupport.ResolveRef(_document, gcodeFileId);
        _ = OpenApiSchemaTestSupport.GetTypes(resolved).Should().BeEquivalentTo(new[] { "string", "null" });
        _ = resolved.GetProperty("format").GetString().Should().Be("uuid");

        string partMappingRequiredJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.part-mapping-required.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(partMappingRequiredJson);
        _ = corpusFixture.RootElement.TryGetProperty("gcodeFileId", out JsonElement corpusGcodeFileId).Should().BeTrue(
            "the partMappingRequired corpus fixture proves the key is present (as an explicit null) for this code");
        _ = corpusGcodeFileId.ValueKind.Should().Be(JsonValueKind.Null);

        string wrongBinJson = File.ReadAllText(
            Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.wrong-bin.json"));
        using JsonDocument wrongBinFixture = JsonDocument.Parse(wrongBinJson);
        _ = wrongBinFixture.RootElement.TryGetProperty("gcodeFileId", out _).Should().BeFalse(
            "the wrongBin corpus fixture proves the key is entirely absent (not present-as-null) for this code");
    }

    /// <summary>
    /// The adjust endpoint's 409 response <c>$ref</c>s its own, narrower
    /// <c>PartAdjustmentConflictResponse</c> -- adjust's only conflict path never raises the
    /// wrong-bin/mapping-required codes above (those are harvest-only outcomes), so it is
    /// deliberately not forced into <c>HarvestConflictResponse</c>'s richer, code-discriminated
    /// shape. It carries just a single nullable <c>message</c> string, not required: the shared
    /// <c>[Idempotent]</c> filter can also short-circuit this action with a filter-level 409 that
    /// has no <c>message</c> at all (see <c>PartAdjustmentConflictResponse</c> remarks), so
    /// <c>message</c> must stay optional for the declared schema to describe every 409 this
    /// action can actually emit.
    /// </summary>
    [Fact]
    public void AdjustConflict_ResponseSchema_IsPartAdjustmentConflictResponse_WithOptionalMessage()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/parts-inventory/{sku}/adjust", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, statusCode: "409")!.Value;

        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/PartAdjustmentConflictResponse");

        JsonElement partAdjustmentConflictResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        _ = OpenApiSchemaTestSupport.GetRequiredSet(partAdjustmentConflictResponse).Should().BeEmpty();
        _ = OpenApiSchemaTestSupport.GetPropertyNames(partAdjustmentConflictResponse).Should().BeEquivalentTo(new[] { "message" });

        JsonElement message = OpenApiSchemaTestSupport.GetProperty(partAdjustmentConflictResponse, "message");
        _ = OpenApiSchemaTestSupport.GetTypes(message).Should().BeEquivalentTo(new[] { "string", "null" });
    }
}
