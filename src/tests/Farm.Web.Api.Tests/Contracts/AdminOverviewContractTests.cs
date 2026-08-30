using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

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
/// This explicitly covers every documented property of <c>SubsystemHealthDto</c> and
/// <c>AttentionItemDto</c> (including <c>detail</c>, <c>title</c>, and the optional
/// <c>action*</c> triplet), not just the identifying <c>key</c>/<c>status</c>/<c>severity</c>
/// fields, so a rename anywhere in either item shape still fails this test even though the
/// fixture diff itself cannot see it.
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
/// <para>
/// Two DI overrides make the array-item coverage below deterministic rather than dependent on
/// whatever the live probe happens to report: an additional, always-<c>Unhealthy</c> named
/// health check (<c>wire-contract-test-attention</c>) forces a real
/// <c>AttentionSeverity.Error</c> item through the health-check attention path (covering
/// <c>actionDestinationId</c>), and a stubbed <see cref="IPrinterConnectionHealthProvider"/>
/// reporting one offline printer forces a real <c>AttentionSeverity.Warning</c> item through
/// the printer-connectivity attention path (covering <c>actionRoute</c>). Without these,
/// <c>attention</c> is empty on a clean test host and every per-item assertion below —
/// including the property-name allowlist that is the actual defence against a field rename —
/// would never execute at all.
/// </para>
/// </remarks>
public sealed class AdminOverviewContractTests : IAsyncLifetime
{
    private static readonly string[] KnownSubsystemStatusTokens = Enum.GetNames<Farm.Infrastructure.Dtos.SubsystemStatus>();
    private static readonly string[] KnownAttentionSeverityTokens = Enum.GetNames<Farm.Infrastructure.Dtos.AttentionSeverity>();

    // The full, exact set of properties SubsystemHealthDto/AttentionItemDto may legally emit.
    // Enumerating each item's actual property names against this allowlist is what actually
    // catches a rename of an OPTIONAL field (detail/actionLabel/actionDestinationId/
    // actionRoute): a conditional `if (item.TryGetProperty(oldName, out _))` check alone would
    // simply skip a renamed property rather than fail, since the property is legitimately
    // allowed to be absent. An unrecognized property name means the wire produced something
    // this allowlist doesn't know about — either a rename or a genuinely new additive field
    // that must be added here deliberately, not silently tolerated.
    private static readonly HashSet<string> KnownSubsystemProperties = new(StringComparer.Ordinal) { "key", "name", "status", "detail" };
    private static readonly HashSet<string> KnownAttentionProperties = new(StringComparer.Ordinal)
    {
        "key", "severity", "title", "detail", "actionLabel", "actionDestinationId", "actionRoute",
    };

    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GetOverview_RealHealthProbes_MatchesShapeAndExactEnumTokens()
    {
        using HttpClient adminClient = await _factory.CreateAdminClientAsync(
            username: "wire-contract-admin-overview",
            email: "wire-contract-admin-overview@example.com");

        Mock<IPrinterConnectionHealthProvider> offlinePrinterProvider = new();
        _ = offlinePrinterProvider
            .Setup(p => p.GetConnectionHealth())
            .Returns(new Dictionary<Guid, PrinterConnectionHealth>
            {
                [Guid.NewGuid()] = new PrinterConnectionHealth
                {
                    PrinterId = Guid.NewGuid(),
                    PrinterName = "wire-contract-offline-printer",
                    Backend = PrinterBackend.Moonraker,
                    ConnectionState = PrinterConnectionState.Offline,
                },
            });

        // Forces at least one real, deterministic attention item through EACH of the two
        // production code paths that populate AttentionItemDto — see class remarks — so the
        // per-item assertions below are never vacuous, regardless of what the live host's own
        // health probes happen to report.
        await using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrinterConnectionHealthProvider>();
                services.AddSingleton(offlinePrinterProvider.Object);

                _ = services.AddHealthChecks().AddCheck(
                    "wire-contract-test-attention",
                    () => HealthCheckResult.Unhealthy("Deliberately forced unhealthy for issue #2238 wire-contract coverage."));
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = adminClient.DefaultRequestHeaders.Authorization;

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

            // SubsystemHealthDto.Detail is nullable: when present it must serialize as a
            // string (not renamed/dropped); when the live probe omits it, the property is
            // absent per the hub's DefaultIgnoreCondition=WhenWritingNull policy. Either way
            // this line still fails on a rename to e.g. "message" or a wrong JsonValueKind,
            // closing the gap the whole-subtree volatility above would otherwise leave open
            // for this property.
            if (subsystem.TryGetProperty("detail", out JsonElement detail))
            {
                Assert.Equal(JsonValueKind.String, detail.ValueKind);
            }

            AssertKnownPropertyNames(subsystem, KnownSubsystemProperties, "subsystems[]");
        }

        JsonElement attention = root.GetProperty("attention");
        // Guaranteed non-empty by the two DI overrides above — see class remarks. This
        // assertion documents that guarantee as an explicit test precondition: if it ever
        // fails, the DI overrides stopped working (e.g. a production change bypassed
        // IPrinterConnectionHealthProvider or the health-check pipeline), not that the
        // per-item assertions below became vacuous silently.
        _ = attention.GetArrayLength().Should().BeGreaterThanOrEqualTo(2,
            because: "the health-check and printer-connectivity DI overrides above must each contribute a real attention item");

        for (int i = 0; i < attention.GetArrayLength(); i++)
        {
            JsonElement item = attention[i];
            _ = JsonContractAssertions.AssertProperty(item, "key", JsonValueKind.String);
            AssertKnownEnumToken(item, "severity", KnownAttentionSeverityTokens);

            // AttentionItemDto.Title/Detail are required strings; assert them explicitly so a
            // rename or dropped property fails here even though the enclosing array's
            // length/order is (correctly) volatile above.
            _ = JsonContractAssertions.AssertProperty(item, "title", JsonValueKind.String);
            _ = JsonContractAssertions.AssertProperty(item, "detail", JsonValueKind.String);

            // ActionLabel/ActionDestinationId/ActionRoute are all optional; assert only their
            // JsonValueKind when present. The property-name allowlist below (not this loop
            // alone) is what actually catches a rename of one of these three fields, since a
            // rename simply makes TryGetProperty return false here rather than fail.
            foreach (string optionalStringProperty in new[] { "actionLabel", "actionDestinationId", "actionRoute" })
            {
                if (item.TryGetProperty(optionalStringProperty, out JsonElement optionalValue))
                {
                    Assert.Equal(JsonValueKind.String, optionalValue.ValueKind);
                }
            }

            AssertKnownPropertyNames(item, KnownAttentionProperties, "attention[]");
        }

        // The forced health-check and printer-connectivity attention items are real, distinct
        // AttentionSeverity values (Error and Warning respectively) carrying actionDestinationId
        // and actionRoute respectively, so both optional action-field variants get genuine,
        // non-vacuous coverage above rather than merely being reachable in principle.
        _ = attention.EnumerateArray().Any(HasActionDestinationId).Should().BeTrue(
            because: "the forced health-check attention item must carry actionDestinationId");
        _ = attention.EnumerateArray().Any(HasActionRoute).Should().BeTrue(
            because: "the forced printer-connectivity attention item must carry actionRoute");

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "admin-overview/overview.live-shape.json",
            endpoint: "GET /api/admin/overview",
            producingTest: $"{nameof(AdminOverviewContractTests)}.{nameof(GetOverview_RealHealthProbes_MatchesShapeAndExactEnumTokens)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    private static bool HasActionDestinationId(JsonElement element) => element.TryGetProperty("actionDestinationId", out _);

    private static bool HasActionRoute(JsonElement element) => element.TryGetProperty("actionRoute", out _);

    /// <summary>
    /// Asserts every property name actually present on <paramref name="element"/> is a member
    /// of <paramref name="knownProperties"/>. This is the real defence against a rename of an
    /// OPTIONAL field: a conditional presence check (<c>TryGetProperty</c>) on the old name
    /// simply no-ops when a property has been renamed away, since the property is legitimately
    /// allowed to be absent — it does not detect that the wire now emits a DIFFERENT,
    /// unexpected name in its place. Enumerating the actual property set closes that gap.
    /// </summary>
    private static void AssertKnownPropertyNames(JsonElement element, HashSet<string> knownProperties, string context)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            _ = knownProperties.Should().Contain(property.Name,
                because: $"'{context}' must not emit an unrecognized property (rename or genuinely new field) without updating this allowlist");
        }
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
