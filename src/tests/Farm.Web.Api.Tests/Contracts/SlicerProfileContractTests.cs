using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the slicer process-profile family
/// (<c>POST /api/slicer/profiles</c>, <c>GET /api/slicer/profiles/{id}</c>,
/// <c>GET /api/slicer/profiles</c>). Issue #2238: every fixture here is produced by a real
/// <c>WebApplicationFactory</c> HTTP round trip through the actual registered MVC
/// <c>JsonSerializerOptions</c> (see <c>src/api/Startup/ControllerStartup.cs</c>) — never a
/// hand-built CLR object serialized with a locally constructed
/// <see cref="JsonSerializerOptions"/>.
/// </summary>
public sealed class SlicerProfileContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Empty-collection variant: <c>GET /api/slicer/profiles</c> maps the internal
    /// <c>SlicerProfileDto</c> list through an anonymous-object projection
    /// (<c>ProfilesController.GetProfilesAsync</c>); with no seeded profiles the result is a
    /// real, genuinely empty JSON array — not a missing key, not null.
    /// </summary>
    [Fact]
    public async Task GetProfiles_NoProfiles_ReturnsEmptyCollection()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-profiles-empty",
            email: "wire-contract-profiles-empty@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/slicer/profiles");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        _ = document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        _ = document.RootElement.GetArrayLength().Should().Be(0);

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slicer-profiles/profiles.empty-collection.json",
            endpoint: "GET /api/slicer/profiles",
            producingTest: $"{nameof(SlicerProfileContractTests)}.{nameof(GetProfiles_NoProfiles_ReturnsEmptyCollection)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    /// <summary>
    /// Missing-key variant, round-tripped through the real create endpoint (not seeded
    /// directly into the database): <c>Description</c> is left null on the create request,
    /// and because <c>ControllerStartup</c> configures
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>
    /// globally, the response omits the <c>description</c> key entirely rather than emitting
    /// an explicit JSON <c>null</c> — this is the real production behavior for this DTO, and is
    /// exactly why wire-contract assertions must operate on <see cref="JsonElement"/>, never a
    /// deserialized CLR object which would hide the missing-vs-null distinction.
    /// </summary>
    [Fact]
    public async Task CreateThenGetProfile_MissingOptionalDescription_OmitsKeyAndMatchesCorpus()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-profiles-create",
            email: "wire-contract-profiles-create@example.com");

        var createRequest = new
        {
            name = "Wire Contract PLA Profile",
            slicerType = "OrcaSlicer",
            quality = "Standard",
            description = (string?)null,
        };

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/slicer/profiles", createRequest);
        _ = createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        string createdJson = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createdDocument = JsonDocument.Parse(createdJson);
        JsonElement created = createdDocument.RootElement;

        JsonContractAssertions.AssertMissingKey(created, "description");
        JsonContractAssertions.AssertEnumToken(created, "slicerType", "OrcaSlicer");
        JsonContractAssertions.AssertEnumToken(created, "quality", "Standard");
        Guid id = created.GetProperty("id").GetGuid();

        using HttpResponseMessage getResponse = await client.GetAsync($"/api/slicer/profiles/{id}");
        _ = getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string getJson = await getResponse.Content.ReadAsStringAsync();
        using JsonDocument getDocument = JsonDocument.Parse(getJson);
        JsonContractAssertions.AssertMissingKey(getDocument.RootElement, "description");

        var volatilePaths = new HashSet<string> { "$.id", "$.createdAt", "$.updatedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slicer-profiles/profiles.missing-key.json",
            endpoint: "GET /api/slicer/profiles/{id}",
            producingTest: $"{nameof(SlicerProfileContractTests)}.{nameof(CreateThenGetProfile_MissingOptionalDescription_OmitsKeyAndMatchesCorpus)}",
            schemaVersion: "1.0",
            actualJson: getJson,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Populated variant: every optional field set, plus an exact enum-token check on both
    /// <c>slicerType</c> and <c>quality</c> — these are plain strings validated server-side
    /// against the <c>SlicerType</c>/<c>ProfileQuality</c> enums (see
    /// <c>ProfilesController.CreateProfileAsync</c>), so the token equality check still guards
    /// against an accidental case or naming drift in either validator, even though the wire
    /// property itself is not a <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>-typed
    /// enum.
    /// <para>
    /// Real production evidence, not an assumption: <c>advancedSettings</c> is asserted MISSING
    /// on the create response, even though the request sends a non-null value. Tracing the real
    /// pipeline (<c>ProfilesService.CreateProfileAsync</c>) shows the submitted value is stored
    /// into <c>ProcessProfile.RawJson</c>, but <c>ProfilesService.ToResponseDto</c> never copies
    /// any value onto <c>ProcessProfileResponseDto.AdvancedSettings</c> — it is always the CLR
    /// default (<see langword="null"/>) and, per <c>ControllerStartup</c>'s
    /// <c>WhenWritingNull</c> policy, omitted from the wire payload. The checked-in fixture also
    /// shows <c>nozzleTemperature</c>/<c>bedTemperature</c> pinned at <c>0</c> and
    /// <c>material</c> as an empty string despite this test's request sending 235/80/"PETG" —
    /// <c>ToResponseDto</c> never maps those either. This is exactly the kind of surprising real
    /// behavior this corpus exists to catch; it is a candidate serialization/mapping defect and
    /// is filed as its own linked finding on #2237 rather than fixed here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateThenGetProfile_Populated_MatchesCorpus()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-profiles-populated",
            email: "wire-contract-profiles-populated@example.com");

        var createRequest = new
        {
            name = "Wire Contract PETG Profile",
            description = "Populated wire-contract fixture profile.",
            slicerType = "PrusaSlicer",
            layerHeight = 0.16,
            infillPercentage = 35,
            printSpeed = 60,
            nozzleTemperature = 235,
            bedTemperature = 80,
            enableSupports = true,
            material = "PETG",
            quality = "Fine",
            advancedSettings = "{\"retractionDistance\":1.2}",
            isDefault = false,
            isPublic = true,
        };

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/slicer/profiles", createRequest);
        _ = createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        string createdJson = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createdDocument = JsonDocument.Parse(createdJson);
        JsonElement created = createdDocument.RootElement;

        JsonContractAssertions.AssertEnumToken(created, "slicerType", "PrusaSlicer");
        JsonContractAssertions.AssertEnumToken(created, "quality", "Fine");
        _ = JsonContractAssertions.AssertProperty(created, "description", JsonValueKind.String);
        JsonContractAssertions.AssertMissingKey(created, "advancedSettings");

        var volatilePaths = new HashSet<string> { "$.id", "$.createdAt", "$.updatedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slicer-profiles/profiles.populated.json",
            endpoint: "POST /api/slicer/profiles",
            producingTest: $"{nameof(SlicerProfileContractTests)}.{nameof(CreateThenGetProfile_Populated_MatchesCorpus)}",
            schemaVersion: "1.0",
            actualJson: createdJson,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Unknown-additive-field tolerance: production JSON reads via
    /// <c>System.Text.Json</c>'s default (non-strict) object binding silently ignore extra
    /// properties in a request body rather than rejecting it — proven here against the real
    /// create endpoint, not a hand-rolled deserializer.
    /// </summary>
    [Fact]
    public async Task CreateProfile_UnknownAdditiveRequestField_IsIgnoredNotRejected()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-profiles-additive",
            email: "wire-contract-profiles-additive@example.com");

        var createRequest = new Dictionary<string, object?>
        {
            ["name"] = "Wire Contract Additive Field Profile",
            ["slicerType"] = "OrcaSlicer",
            ["quality"] = "Standard",
            ["futureFieldNotYetKnown"] = "some-future-value",
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/slicer/profiles", createRequest);
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement created = document.RootElement;
        JsonContractAssertions.AssertMissingKey(created, "futureFieldNotYetKnown");

        var volatilePaths = new HashSet<string> { "$.id", "$.createdAt", "$.updatedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slicer-profiles/profiles.unknown-additive-request-field.json",
            endpoint: "POST /api/slicer/profiles (unknown additive request field)",
            producingTest: $"{nameof(SlicerProfileContractTests)}.{nameof(CreateProfile_UnknownAdditiveRequestField_IsIgnoredNotRejected)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }
}
