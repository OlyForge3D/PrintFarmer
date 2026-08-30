using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Testing.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the print-queue overview family (<c>GET /api/job-queue</c>).
/// Issue #2238: fixtures are produced by a real <c>WebApplicationFactory</c> HTTP round trip
/// through the actual registered MVC <c>JsonSerializerOptions</c>
/// (<c>src/api/Startup/ControllerStartup.cs</c>), never a hand-built CLR object.
/// </summary>
public sealed class PrintQueueContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Empty-collection variant: no printers seeded, so <c>JobQueueService.GetQueueOverviewAsync</c>
    /// returns a genuinely empty list — the endpoint returns an empty JSON array, not a missing
    /// key or null.
    /// </summary>
    [Fact]
    public async Task GetQueue_NoPrinters_ReturnsEmptyCollection()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-queue-empty",
            email: "wire-contract-queue-empty@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/job-queue");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        _ = document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        _ = document.RootElement.GetArrayLength().Should().Be(0);

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-queue/queue.empty-collection.json",
            endpoint: "GET /api/job-queue",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetQueue_NoPrinters_ReturnsEmptyCollection)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    /// <summary>
    /// Populated + missing-key variant: seeds a single available printer (via
    /// <see cref="AppDbContext"/>, the same real EF Core store the production
    /// <c>JobQueueService</c> reads from) with a populated <c>supportedMaterials</c> collection
    /// but no <c>NozzleModel</c> — so <c>nozzleDiameter</c> resolves to <see langword="null"/>
    /// and, per <c>ControllerStartup</c>'s <c>WhenWritingNull</c> policy, is omitted from the
    /// wire payload entirely (missing key, not explicit null). <c>modelAliases</c>, by contrast,
    /// sources from an EF Core collection navigation property (<c>printer.Model.Aliases</c>)
    /// that materializes as an empty (never-null) list when no <c>ModelAlias</c> rows are
    /// seeded — real production evidence that "no aliases" and "no nozzle model" take two
    /// different wire shapes (empty array vs. missing key) for what looks like the same
    /// "nothing was seeded" scenario, depending on whether the source is a scalar navigation
    /// (<see langword="null"/>-able) or a collection navigation (empty-but-present).
    /// </summary>
    [Fact]
    public async Task GetQueue_PopulatedPrinter_MatchesCorpus()
    {
        Guid printerId = await SeedAvailablePrinterAsync();

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-queue-populated",
            email: "wire-contract-queue-populated@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/job-queue");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        _ = root.ValueKind.Should().Be(JsonValueKind.Array);
        _ = root.GetArrayLength().Should().Be(1);
        JsonElement entry = root[0];

        JsonContractAssertions.AssertMissingKey(entry, "nozzleDiameter");
        JsonContractAssertions.AssertEmptyCollection(entry, "modelAliases");
        JsonContractAssertions.AssertMissingKey(entry, "currentJobId");
        JsonContractAssertions.AssertMissingKey(entry, "currentJobName");
        JsonElement supportedMaterials = JsonContractAssertions.AssertNonEmptyCollection(entry, "supportedMaterials");
        _ = supportedMaterials.GetArrayLength().Should().Be(2);
        _ = JsonContractAssertions.AssertProperty(entry, "isAvailable", JsonValueKind.True);
        _ = JsonContractAssertions.AssertProperty(entry, "queuedJobsCount", JsonValueKind.Number);

        var volatilePaths = new HashSet<string> { "$[0].printerId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-queue/queue.populated.json",
            endpoint: "GET /api/job-queue",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetQueue_PopulatedPrinter_MatchesCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);

        _ = printerId.Should().NotBe(Guid.Empty);
    }

    private async Task<Guid> SeedAvailablePrinterAsync()
    {
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _ = db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Wire Contract Manufacturer",
        });
        _ = db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Wire Contract Model",
        });
        _ = db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Wire Contract Queue Printer",
            ServerUrl = "http://10.0.0.51",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true,
            IsAvailable = true,
            Toolheads =
            [
                new Toolhead
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    Name = "T0",
                    Index = 0,
                    IsPrimary = true,
                    ToolheadType = ToolheadType.Physical,
                    SupportedMaterials = ["PLA", "PETG"],
                },
            ],
        });

        _ = await db.SaveChangesAsync();
        return printerId;
    }
}
