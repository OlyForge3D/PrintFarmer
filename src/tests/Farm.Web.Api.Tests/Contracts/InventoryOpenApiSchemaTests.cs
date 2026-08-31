using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Issue #2284: asserts the OpenAPI document's declared response schemas for the
/// <c>inventory</c> family (Spoolman-backed spool/filament/vendor/material lookups plus the
/// printed-parts inventory, bins, reorder, mappings, adjustment, and harvest endpoints consumed
/// by the iOS app) against the real wire payloads captured by the #2238 canonical corpus
/// (read-only; never modified here).
/// </summary>
[Collection(OpenApiDocumentCollection.Name)]
public sealed class InventoryOpenApiSchemaTests(OpenApiDocumentFixture fixture)
{
    private readonly JsonDocument _document = fixture.Document;

    /// <summary>
    /// Positive check: every Spoolman list endpoint's 200 response resolves to the expected
    /// paged-result or plain-array shape, with the array/paged-items element schema $ref'd to
    /// the corpus-observed DTO.
    /// </summary>
    [Theory]
    [InlineData("/api/spoolman/spools", "SpoolmanPagedResultOfSpoolmanSpoolDto", "SpoolmanSpoolDto")]
    [InlineData("/api/spoolman/filaments", "SpoolmanPagedResultOfSpoolmanFilamentDto", "SpoolmanFilamentDto")]
    public void GetSpoolmanPagedListEndpoints_ResponseSchema_ResolvesToExpectedPagedResultDto(
        string path, string pagedResultSchemaName, string itemSchemaName)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, path, "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        _ = responseSchema.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{pagedResultSchemaName}");

        JsonElement pagedResultDto = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        _ = OpenApiSchemaTestSupport.GetPropertyNames(pagedResultDto).Should().BeEquivalentTo(new[] { "items", "totalCount" });
        _ = OpenApiSchemaTestSupport.GetRequiredSet(pagedResultDto).Should().BeEquivalentTo(new[] { "items", "totalCount" });

        JsonElement itemsProperty = OpenApiSchemaTestSupport.GetProperty(pagedResultDto, "items");
        _ = OpenApiSchemaTestSupport.GetTypes(itemsProperty).Should().Contain("array");
        _ = itemsProperty.GetProperty("items").GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{itemSchemaName}",
            "'items' should be an array of the corpus-observed item DTO, not just any schema with the right property names");
    }

    [Theory]
    [InlineData("/api/spoolman/vendors", "SpoolmanVendorDto")]
    [InlineData("/api/spoolman/materials", "SpoolmanMaterialDto")]
    [InlineData("/api/parts-inventory", "PartInventoryResponse")]
    [InlineData("/api/bins", "BinResponse")]
    [InlineData("/api/parts-inventory/reorder", "ReorderCandidateResponse")]
    [InlineData("/api/parts-inventory/mappings", "PartOutputMappingResponse")]
    public void GetPlainArrayListEndpoints_ResponseSchema_IsArrayOfExpectedDto(string path, string itemSchemaName)
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, path, "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;

        _ = OpenApiSchemaTestSupport.GetTypes(responseSchema).Should().Contain("array");
        JsonElement items = responseSchema.GetProperty("items");
        _ = items.GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{itemSchemaName}");
    }

    /// <summary>
    /// Positive check: <c>GET /api/spoolman/materials/available</c> correctly documents a plain
    /// string array (not a $ref'd enum component), matching the corpus's freeform material-name
    /// tokens (<c>"ASA"</c>, <c>"PLA"</c>).
    /// </summary>
    [Fact]
    public void GetSpoolmanAvailableMaterials_ResponseSchema_IsArrayOfPlainStrings()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/spoolman/materials/available", "get");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;

        _ = OpenApiSchemaTestSupport.GetTypes(responseSchema).Should().Contain("array");
        JsonElement itemSchema = responseSchema.GetProperty("items");
        _ = OpenApiSchemaTestSupport.GetTypes(itemSchema).Should().BeEquivalentTo(new[] { "string" });
        _ = OpenApiSchemaTestSupport.GetEnumTokens(itemSchema).Should().BeNull(
            "available materials is a freeform string list server-side, not a C# enum");

        string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "spoolman-available-materials.populated.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(json);
        _ = corpusFixture.RootElement.EnumerateArray().Select(e => e.GetString())
            .Should().Contain(new[] { "ASA", "PLA" },
                "every material-name token the corpus proves is emitted on the wire should be representable by a plain string schema");
    }

    /// <summary>
    /// Positive check: every corpus-provable "required" list (populated fixture's full property
    /// set minus the corresponding missing-key fixture's present set) exactly matches the
    /// schema's declared "required" array for each Spoolman/printed-parts DTO family.
    /// </summary>
    public static IEnumerable<object[]> RequiredListFixtureCases()
    {
        yield return ["SpoolmanSpoolDto", "spoolman-spools.missing-key.json", new[] { "id", "name", "material", "inUse" }];
        yield return ["SpoolmanFilamentDto", "spoolman-filaments.missing-key.json", new[] { "id" }];
        yield return ["SpoolmanVendorDto", "spoolman-vendors.missing-key.json", new[] { "id", "name" }];
        yield return ["SpoolmanMaterialDto", "spoolman-materials.missing-key.json", new[] { "id", "name" }];
        yield return ["PartInventoryResponse", "parts.missing-key.json", new[]
        {
            "id", "sku", "name", "onHand", "reorderPoint", "needsReorder", "isActive", "createdAt", "updatedAt",
        }];
        yield return ["BinResponse", "bins.missing-key.json", new[] { "id", "code", "name", "isActive", "createdAt", "updatedAt" }];
        yield return ["PartAdjustmentResponse", "adjustment.missing-key.json", new[]
        {
            "id", "partInventoryId", "sku", "delta", "resultingBalance", "reason", "createdAt",
        }];
    }

    [Theory]
    [MemberData(nameof(RequiredListFixtureCases))]
    public void DtoSchema_RequiredList_MatchesCorpusMissingKeyFixturePresentProperties(
        string dtoSchemaName, string missingKeyFixtureFile, string[] expectedRequired)
    {
        JsonElement dto = OpenApiSchemaTestSupport.GetComponentSchema(_document, dtoSchemaName);
        _ = OpenApiSchemaTestSupport.GetRequiredSet(dto).Should().BeEquivalentTo(expectedRequired,
            $"the schema should declare exactly the properties the corpus's '{missingKeyFixtureFile}' " +
            "fixture proves are always present");

        string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", missingKeyFixtureFile));
        using JsonDocument corpusFixture = JsonDocument.Parse(json);
        JsonElement firstItem = corpusFixture.RootElement.ValueKind == JsonValueKind.Array
            ? corpusFixture.RootElement[0]
            : corpusFixture.RootElement.TryGetProperty("items", out JsonElement items)
                ? items[0]
                : corpusFixture.RootElement;

        // The "missing-key" fixture naming convention proves the *lower bound* (every property
        // named here must be present), and by construction of these particular fixtures also the
        // *upper bound*: the minimal-scenario payload's own property set is exactly the always-
        // present set, with every optional member genuinely omitted. Asserting exact key-set
        // equality (not just containment) catches an under-declared "required" list the same way
        // #2261 caught one for QueueOverviewDto.
        var corpusPropertyNames = firstItem.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        _ = corpusPropertyNames.Should().BeEquivalentTo(expectedRequired,
            $"'{missingKeyFixtureFile}' is expected to include exactly the always-present properties " +
            "with every optional member omitted, per its 'missing-key' naming convention");
    }

    /// <summary>
    /// Positive check: <c>HarvestJobResponse</c>'s (<c>POST /api/job-queue/{id}/harvest</c>, 200)
    /// declared "required" list matches the always-present keys proven by the corpus's
    /// missing-key scenario, and its <c>adjustments</c>/<c>outputs</c> array items resolve to the
    /// corpus-observed nested DTOs.
    /// </summary>
    [Fact]
    public void PostHarvest_ResponseSchema_ResolvesToHarvestJobResponse_WithMatchingRequiredListAndNestedItemSchemas()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation)!.Value;
        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/HarvestJobResponse");

        JsonElement harvestJobResponse = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        var expectedRequired = new HashSet<string>(StringComparer.Ordinal)
        {
            "printJobId", "harvestedAt", "alreadyHarvested", "adjustments", "outputs",
        };
        _ = OpenApiSchemaTestSupport.GetRequiredSet(harvestJobResponse).Should().BeEquivalentTo(expectedRequired);

        string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", "harvest.missing-key.json"));
        using JsonDocument corpusFixture = JsonDocument.Parse(json);
        foreach (string propertyName in expectedRequired)
        {
            _ = corpusFixture.RootElement.TryGetProperty(propertyName, out _).Should().BeTrue(
                $"the corpus's missing-key harvest scenario proves '{propertyName}' is always present");
        }

        JsonElement adjustmentsItems = OpenApiSchemaTestSupport.GetProperty(harvestJobResponse, "adjustments").GetProperty("items");
        _ = adjustmentsItems.GetProperty("$ref").GetString().Should().Be("#/components/schemas/PartAdjustmentResponse");

        JsonElement outputsItems = OpenApiSchemaTestSupport.GetProperty(harvestJobResponse, "outputs").GetProperty("items");
        _ = outputsItems.GetProperty("$ref").GetString().Should().Be("#/components/schemas/HarvestOutputResponse");
    }

    /// <summary>
    /// <b>Known finding, filed as its own issue rather than fixed here per #2284's scope:</b> both
    /// harvest/adjust conflict (409) actions document their response as the generic ASP.NET Core
    /// <c>ProblemDetails</c> shape (<c>type</c>/<c>title</c>/<c>status</c>/<c>detail</c>/
    /// <c>instance</c> only), but the real wire payloads captured by the corpus prove the server
    /// always emits additional discriminator/context properties beyond that shape --
    /// <c>harvest.wrong-bin.json</c> adds <c>code</c>/<c>mismatches</c>, and
    /// <c>harvest.part-mapping-required.json</c> adds <c>code</c>/<c>jobId</c>/
    /// <c>projectFileId</c>/<c>gcodeFileId</c>/<c>guidance</c>. None of these extension
    /// properties are declared anywhere in the OpenAPI document, so a generated client has no
    /// schema-level guarantee they exist even though the server always emits them for these
    /// conflict codes. See issue #2294 for the production-side fix (a typed conflict-response
    /// DTO); this test locks in today's documented (drifted) shape so a future fix updates both
    /// the production annotation and this test together.
    /// </summary>
    [Fact]
    public void PostHarvestConflict_ResponseSchema_IsDocumentedAsGenericProblemDetails_MissingCorpusObservedExtensionProperties()
    {
        JsonElement operation = OpenApiSchemaTestSupport.GetOperation(_document, "/api/job-queue/{id}/harvest", "post");
        JsonElement responseSchema = OpenApiSchemaTestSupport.GetResponseSchema(operation, "409")!.Value;
        _ = responseSchema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/ProblemDetails");

        JsonElement problemDetails = OpenApiSchemaTestSupport.ResolveRef(_document, responseSchema);
        IReadOnlySet<string> documentedPropertyNames = OpenApiSchemaTestSupport.GetPropertyNames(problemDetails);
        _ = documentedPropertyNames.Should().BeEquivalentTo(new[] { "type", "title", "status", "detail", "instance" },
            "ProblemDetails is documented today with only the generic framework shape (see issue #2294)");

        foreach (string fixtureFile in new[] { "harvest.wrong-bin.json", "harvest.part-mapping-required.json" })
        {
            string json = File.ReadAllText(Path.Join(WireContractCorpusPaths.ApiRoot, "inventory", fixtureFile));
            using JsonDocument corpusFixture = JsonDocument.Parse(json);
            var undocumentedExtensionProperties = corpusFixture.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !documentedPropertyNames.Contains(name))
                .ToList();

            _ = undocumentedExtensionProperties.Should().NotBeEmpty(
                $"'{fixtureFile}' is expected to still carry properties beyond the generic ProblemDetails " +
                "shape today (issue #2294); if this now fails empty, the drift has been fixed and this " +
                "characterization test (and its comment) should be updated to a positive assertion instead");
        }
    }
}
