using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the user-tasks family (<c>GET /api/tasks</c>,
/// <c>POST /api/tasks</c>). Issue #2238: fixtures are produced by a real
/// <c>WebApplicationFactory</c> HTTP round trip through the actual registered MVC
/// <c>JsonSerializerOptions</c>.
/// <para>
/// Real production evidence, not an assumption: <c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c>
/// declare a bespoke, type-level <c>[JsonConverter(typeof(UserTaskAnchorKindJsonConverter))]</c>
/// (and the source-kind equivalent) whose <c>ToWire</c> logic emits lowercase camelCase tokens
/// (e.g. <c>"unspecified"</c>). Over the real MVC pipeline they do NOT take effect: this corpus
/// captured <c>"Unspecified"</c> (PascalCase) for both properties instead. The reason is System.Text.Json's
/// converter-resolution precedence — a converter registered into the global
/// <c>JsonSerializerOptions.Converters</c> list (here, <c>ControllerStartup</c>'s trailing
/// <c>new JsonStringEnumConverter()</c>) outranks a <c>[JsonConverter]</c> attribute applied to
/// the enum TYPE itself (it is only outranked by a property-level attribute, which
/// <c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c> do not carry). The bespoke converters are
/// therefore effectively dead code for this DTO's real wire output, contradicting their own doc
/// comments. This is a candidate serialization defect and is filed as its own linked finding on
/// #2237 rather than fixed here; this corpus intentionally asserts the REAL observed token, not
/// the one the enum's own converter attribute would suggest.
/// </para>
/// </summary>
public sealed class TasksContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>Empty-collection variant: a fresh database has no tasks.</summary>
    [Fact]
    public async Task GetTasks_NoTasks_ReturnsEmptyCollection()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-tasks-empty",
            email: "wire-contract-tasks-empty@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/tasks");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        _ = document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        _ = document.RootElement.GetArrayLength().Should().Be(0);

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "tasks/tasks.empty-collection.json",
            endpoint: "GET /api/tasks",
            producingTest: $"{nameof(TasksContractTests)}.{nameof(GetTasks_NoTasks_ReturnsEmptyCollection)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    /// <summary>
    /// Populated + missing-key + real-vs-declared-converter variant, created through the real
    /// <c>POST /api/tasks</c> endpoint (never seeded directly): a manual task has no
    /// due date, no completion, no metadata, and no shift-plan anchor/source, so those
    /// optional properties are entirely missing from the wire payload — while
    /// <c>status</c>/<c>priority</c>/<c>taskType</c>/<c>anchorKind</c>/<c>sourceKind</c> are all
    /// present as their exact real production string tokens (see class remarks: all five are
    /// PascalCase in practice, because the global <c>JsonStringEnumConverter</c> wins over the
    /// bespoke <c>AnchorKind</c>/<c>SourceKind</c> converters' type-level attribute).
    /// </summary>
    [Fact]
    public async Task CreateThenGetTask_Populated_MatchesCorpus()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-tasks-create",
            email: "wire-contract-tasks-create@example.com");

        var createRequest = new
        {
            title = "Wire contract manual task",
            description = (string?)null,
            priority = "High",
        };

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/tasks", createRequest);
        _ = createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        string createdJson = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createdDocument = JsonDocument.Parse(createdJson);
        JsonElement created = createdDocument.RootElement;

        JsonContractAssertions.AssertMissingKey(created, "description");
        JsonContractAssertions.AssertMissingKey(created, "dueAt");
        JsonContractAssertions.AssertMissingKey(created, "completedAt");
        JsonContractAssertions.AssertMissingKey(created, "metadataJson");
        JsonContractAssertions.AssertMissingKey(created, "anchorAtUtc");
        JsonContractAssertions.AssertMissingKey(created, "windowStartUtc");
        JsonContractAssertions.AssertMissingKey(created, "windowEndUtc");
        JsonContractAssertions.AssertMissingKey(created, "sourceId");

        // Default JsonStringEnumConverter: PascalCase tokens.
        JsonContractAssertions.AssertEnumToken(created, "status", "Pending");
        JsonContractAssertions.AssertEnumToken(created, "priority", "High");
        JsonContractAssertions.AssertEnumToken(created, "taskType", "Custom");

        // Real behavior, NOT the bespoke converters' documented intent: the global
        // JsonStringEnumConverter (registered in ControllerStartup's Converters list) takes
        // precedence over the type-level [JsonConverter] attribute on UserTaskAnchorKind/
        // UserTaskSourceKind, so these are PascalCase over the wire too. See class remarks.
        // NEGATIVE CONTROL (issue #2238 acceptance criterion): this line is deliberately
        // wrong (asserting a value the real wire contract never emits) to prove the CI leg
        // that runs Farm.Web.Api.Tests.Contracts.* actually executes and turns RED. It is
        // reverted immediately after the red run is captured -- see the linked run in the PR.
        JsonContractAssertions.AssertEnumToken(created, "anchorKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(created, "sourceKind", "Unspecified");

        var volatilePaths = new HashSet<string> { "$.id", "$.entityId", "$.createdAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "tasks/tasks.populated.json",
            endpoint: "POST /api/tasks",
            producingTest: $"{nameof(TasksContractTests)}.{nameof(CreateThenGetTask_Populated_MatchesCorpus)}",
            schemaVersion: "1.0",
            actualJson: createdJson,
            volatilePaths: volatilePaths);
    }
}
