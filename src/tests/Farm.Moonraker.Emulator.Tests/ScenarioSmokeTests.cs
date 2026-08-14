using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Verifies each seed scenario boots into the expected state purely from
/// configuration (<c>Emulator:Scenario</c>), the way four separate Compose services
/// would each start their own emulator process instance.
/// </summary>
public sealed class ScenarioSmokeTests :
    IClassFixture<ReadyPrinterFactory>,
    IClassFixture<PrintingPrinterFactory>,
    IClassFixture<PausedPrinterFactory>,
    IClassFixture<ShutdownPrinterFactory>
{
    private readonly ReadyPrinterFactory _ready;
    private readonly PrintingPrinterFactory _printing;
    private readonly PausedPrinterFactory _paused;
    private readonly ShutdownPrinterFactory _shutdown;

    public ScenarioSmokeTests(
        ReadyPrinterFactory ready,
        PrintingPrinterFactory printing,
        PausedPrinterFactory paused,
        ShutdownPrinterFactory shutdown)
    {
        _ready = ready;
        _printing = printing;
        _paused = paused;
        _shutdown = shutdown;
    }

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        using HttpClient client = _ready.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    [Theory]
    [InlineData("ready", "ready")]
    [InlineData("printing", "ready")]
    [InlineData("paused", "ready")]
    [InlineData("shutdown", "shutdown")]
    public async Task PrinterInfo_ReportsKlippyStateForItsConfiguredScenario(string scenarioKey, string expectedKlippyState)
    {
        // printer/info reports Klippy's connection state, not the active print job's
        // state — Klippy stays "ready" while printing/paused; only the shutdown
        // scenario disconnects Klippy itself. Print job state is under print_stats,
        // exercised separately via printer/objects/query in PrintControlTests.
        using HttpClient client = FactoryFor(scenarioKey).CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/printer/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("state").GetString().Should().Be(expectedKlippyState);
        doc.RootElement.GetProperty("result").GetProperty("hostname").GetString().Should().Be($"moonraker-{scenarioKey}");
    }

    [Theory]
    [InlineData("ready", "standby")]
    [InlineData("printing", "printing")]
    [InlineData("paused", "paused")]
    public async Task ServerInfo_AndObjectsQuery_ReflectPrintJobStatePerScenario(string scenarioKey, string expectedPrintState)
    {
        using HttpClient client = FactoryFor(scenarioKey).CreateClient();

        using HttpResponseMessage serverInfoResponse = await client.GetAsync("/server/info");
        using JsonDocument serverDoc = JsonDocument.Parse(await serverInfoResponse.Content.ReadAsStringAsync());
        serverDoc.RootElement.GetProperty("result").GetProperty("klippy_connected").GetBoolean().Should().BeTrue();

        using HttpResponseMessage infoResponse = await client.GetAsync("/printer/objects/query?print_stats");
        infoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be(expectedPrintState);
    }

    [Fact]
    public async Task ShutdownScenario_RemainsConnectedAndObjectsQueryReportsShutdown()
    {
        using HttpClient client = _shutdown.CreateClient();

        using HttpResponseMessage serverInfoResponse = await client.GetAsync("/server/info");
        using JsonDocument serverDoc = JsonDocument.Parse(await serverInfoResponse.Content.ReadAsStringAsync());
        serverDoc.RootElement.GetProperty("result").GetProperty("klippy_connected").GetBoolean().Should().BeTrue();
        serverDoc.RootElement.GetProperty("result").GetProperty("klippy_state").GetString().Should().Be("shutdown");

        using HttpResponseMessage response = await client.GetAsync("/printer/objects/query?print_stats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument queryDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        queryDoc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats")
            .GetProperty("state").GetString().Should().Be("error");
    }

    private EmulatorFactory FactoryFor(string scenarioKey) => scenarioKey switch
    {
        "ready" => _ready,
        "printing" => _printing,
        "paused" => _paused,
        "shutdown" => _shutdown,
        _ => throw new ArgumentOutOfRangeException(nameof(scenarioKey), scenarioKey, "Unknown scenario key."),
    };
}
