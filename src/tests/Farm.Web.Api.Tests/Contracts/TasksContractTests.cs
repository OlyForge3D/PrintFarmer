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
/// Issue #2246: <c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c> (and
/// <c>ShiftPlanGroupDto.AnchorKind</c>) now carry PROPERTY-level
/// <c>[JsonConverter(typeof(UserTaskAnchorKindJsonConverter))]</c> (and the source-kind
/// equivalent) attributes, whose <c>ToWire</c> logic emits lowercase camelCase tokens (e.g.
/// <c>"unspecified"</c>). A property-level attribute is the only thing that outranks a converter
/// registered into the global <c>JsonSerializerOptions.Converters</c> list (here,
/// <c>ControllerStartup</c>'s trailing <c>new JsonStringEnumConverter()</c>), which in turn
/// outranks a type-level <c>[JsonConverter]</c> attribute on the enum itself. Before this fix, the
/// type-level attributes on <c>UserTaskAnchorKind</c>/<c>UserTaskSourceKind</c> were dead code for
/// this DTO's real wire output: the corpus previously captured <c>"Unspecified"</c> (PascalCase)
/// for both properties. This corpus now asserts the corrected, canonical lowercase camelCase
/// tokens per Dallas's sign-off on #2246.
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
    /// Populated + missing-key + property-level-converter variant, created through the real
    /// <c>POST /api/tasks</c> endpoint (never seeded directly): a manual task has no
    /// due date, no completion, no metadata, and no shift-plan anchor/source, so those
    /// optional properties are entirely missing from the wire payload — while
    /// <c>status</c>/<c>priority</c>/<c>taskType</c> remain PascalCase (global
    /// <c>JsonStringEnumConverter</c>) and <c>anchorKind</c>/<c>sourceKind</c> are lowercase
    /// camelCase (property-level converter attributes; see class remarks).
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

        // Property-level [JsonConverter] attributes on UserTaskDto.AnchorKind/SourceKind now
        // outrank the global JsonStringEnumConverter, so these are the canonical lowercase
        // camelCase tokens (issue #2246). See class remarks.
        JsonContractAssertions.AssertEnumToken(created, "anchorKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(created, "sourceKind", "unspecified");

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

    /// <summary>
    /// Unknown-additive-field tolerance: production JSON reads via <c>System.Text.Json</c>'s
    /// default (non-strict) object binding silently ignore extra properties in a request body
    /// rather than rejecting it — proven here against the real <c>POST /api/tasks</c> endpoint,
    /// not a hand-rolled deserializer.
    /// </summary>
    [Fact]
    public async Task CreateTask_UnknownAdditiveRequestField_IsIgnoredNotRejected()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-tasks-additive",
            email: "wire-contract-tasks-additive@example.com");

        var createRequest = new Dictionary<string, object?>
        {
            ["title"] = "Wire contract additive field task",
            ["priority"] = "Normal",
            ["futureFieldNotYetKnown"] = "some-future-value",
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/tasks", createRequest);
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement created = document.RootElement;
        JsonContractAssertions.AssertMissingKey(created, "futureFieldNotYetKnown");

        var volatilePaths = new HashSet<string> { "$.id", "$.entityId", "$.createdAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "tasks/tasks.unknown-additive-request-field.json",
            endpoint: "POST /api/tasks (unknown additive request field)",
            producingTest: $"{nameof(TasksContractTests)}.{nameof(CreateTask_UnknownAdditiveRequestField_IsIgnoredNotRejected)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }
}
