using System.Net;
using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for <c>GET /api/admin/overview</c>. Issue #2238.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the other P0 families, <c>AdminOverviewDto</c>'s shape is genuinely
/// non-deterministic at the JSON-tree level, not just in its leaf values: the number, order,
/// and keys of <c>subsystems</c>/<c>attention</c> entries depend on live health-probe state
/// (see <c>AdminOverviewService</c>), which can differ between the fixture-capture run and any
/// later verify run even in this isolated test host. A per-leaf <c>volatilePaths</c> entry
/// cannot express "this array's length/order may differ" — <see cref="JsonContractAssertions.CompareStructurally"/>
/// still requires equal array length and index-aligned elements for anything it descends into.
/// So instead of enumerating leaves, the whole <c>$.subsystems</c>/<c>$.attention</c>/
/// <c>$.overallStatus</c> subtrees are declared volatile: for a volatile path,
/// <c>CompareInto</c> checks only that both sides share the same <see cref="JsonValueKind"/>
/// (i.e. still an array/string) and never descends further, so a run with a different item
/// count or item order is not treated as corpus drift. Real structural/enum-token discipline
/// for whatever the live response actually contains is instead enforced unconditionally, on
/// every run, by the explicit per-item assertions below (<see cref="AssertKnownEnumToken"/>,
/// <see cref="JsonContractAssertions.AssertProperty"/>) — those still fail on a renamed
/// property, a missing key, or a numeric/mis-cased enum token, regardless of array shape.
/// </para>
/// <para>
/// The "each public enum as its exact string token" requirement is satisfied independently of
/// the (necessarily loose) fixture diff: <see cref="AssertKnownEnumToken"/> asserts that
/// <c>overallStatus</c> and every <c>subsystems[].status</c>/<c>attention[].severity</c> value
/// is an EXACT, case-sensitive C# enum member name of <c>SubsystemStatus</c>/
/// <c>AttentionSeverity</c> — proving the wire token is real <c>JsonStringEnumConverter</c>
/// output (not a numeric ordinal, not a differently-cased variant) without needing to predict
/// which member a live health probe reports.
/// </para>
/// </remarks>
public sealed class AdminOverviewContractTests : IAsyncLifetime
{
    private static readonly string[] KnownSubsystemStatusTokens = Enum.GetNames<Farm.Infrastructure.Dtos.SubsystemStatus>();
    private static readonly string[] KnownAttentionSeverityTokens = Enum.GetNames<Farm.Infrastructure.Dtos.AttentionSeverity>();

    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GetOverview_RealHealthProbes_MatchesShapeAndExactEnumTokens()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-admin-overview",
            email: "wire-contract-admin-overview@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/admin/overview");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        _ = JsonContractAssertions.AssertProperty(root, "checkedAt", JsonValueKind.String);
        JsonElement subsystems = JsonContractAssertions.AssertNonEmptyCollection(root, "subsystems");
        _ = JsonContractAssertions.AssertProperty(root, "attention", JsonValueKind.Array);

        AssertKnownEnumToken(root, "overallStatus", KnownSubsystemStatusTokens);

        // Whole subtrees, not individual leaves: see the class remarks for why per-leaf
        // volatility can't express "this array's length/order may legitimately differ."
        var volatilePaths = new HashSet<string> { "$.checkedAt", "$.overallStatus", "$.subsystems", "$.attention" };
        for (int i = 0; i < subsystems.GetArrayLength(); i++)
        {
            JsonElement subsystem = subsystems[i];
            _ = JsonContractAssertions.AssertProperty(subsystem, "key", JsonValueKind.String);
            _ = JsonContractAssertions.AssertProperty(subsystem, "name", JsonValueKind.String);
            AssertKnownEnumToken(subsystem, "status", KnownSubsystemStatusTokens);
        }

        JsonElement attention = root.GetProperty("attention");
        for (int i = 0; i < attention.GetArrayLength(); i++)
        {
            JsonElement item = attention[i];
            _ = JsonContractAssertions.AssertProperty(item, "key", JsonValueKind.String);
            AssertKnownEnumToken(item, "severity", KnownAttentionSeverityTokens);
        }

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "admin-overview/overview.live-shape.json",
            endpoint: "GET /api/admin/overview",
            producingTest: $"{nameof(AdminOverviewContractTests)}.{nameof(GetOverview_RealHealthProbes_MatchesShapeAndExactEnumTokens)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Asserts that <paramref name="propertyName"/> on <paramref name="element"/> is a JSON
    /// string whose value is EXACTLY (case-sensitive) one of <paramref name="knownTokens"/> —
    /// the real C# enum member names produced by <c>JsonStringEnumConverter</c>. This fails for
    /// a numeric ordinal (wrong <see cref="JsonValueKind"/>), a lowercase/camelCase variant
    /// (case-sensitive set membership), or any other drift, without hardcoding which specific
    /// member a live health probe reports.
    /// </summary>
    private static void AssertKnownEnumToken(JsonElement element, string propertyName, string[] knownTokens)
    {
        JsonElement property = JsonContractAssertions.AssertProperty(element, propertyName, JsonValueKind.String);
        string? actual = property.GetString();
        _ = knownTokens.Should().Contain(actual, because: $"'{propertyName}' must be an exact enum member token, not a numeric ordinal or a differently-cased variant");
    }
}
