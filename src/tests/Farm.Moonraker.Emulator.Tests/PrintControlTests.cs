using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class PrintControlTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public PrintControlTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientWithScenarioAsync(string scenario)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json($$"""{"scenario":"{{scenario}}"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task StartPrint_OnReadyPrinter_TransitionsToPrinting()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("printing");
    }

    [Fact]
    public async Task StartPrint_UnknownFilename_Returns404NotFabricatedSuccess()
    {
        // Real Moonraker/Klipper cannot start a print for a file that doesn't exist under the
        // gcodes root — the emulator must fail the same way rather than silently succeeding.
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"does-not-exist.gcode"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("WebRequestError");

        // The printer must remain in "standby", not "printing" — the rejected start must not have
        // mutated print state.
        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument queryDoc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        queryDoc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().NotBe("printing");
    }

    [Fact]
    public async Task StartPrint_WhileAlreadyPrinting_Returns409Busy()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task StartPrint_WhilePaused_Returns409WithoutReplacingActiveJob()
    {
        using HttpClient client = await ClientWithScenarioAsync("Paused");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats")
            .GetProperty("state").GetString().Should().Be("paused");
    }

    [Fact]
    public async Task Pause_WhilePrinting_TransitionsToPaused()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/pause", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("paused");
    }

    [Fact]
    public async Task Pause_WhenAlreadyPaused_IsIdempotentAndReturns200()
    {
        using HttpClient client = await ClientWithScenarioAsync("Paused");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/pause", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pause_WhenNotPrinting_Returns400()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/pause", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resume_WhilePaused_TransitionsToPrinting()
    {
        using HttpClient client = await ClientWithScenarioAsync("Paused");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/resume", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("printing");
    }

    [Fact]
    public async Task Resume_ThenAdvance_PreservesPausedTimeInTotalDuration()
    {
        using HttpClient client = await ClientWithScenarioAsync("Paused");
        (await client.PostAsync("/printer/print/resume", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/__emulator/time/advance", TestRequests.Json("""{"seconds":10}"""))).EnsureSuccessStatusCode();

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        JsonElement stats = doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats");
        stats.GetProperty("print_duration").GetDouble().Should().Be(130);
        stats.GetProperty("total_duration").GetDouble().Should().Be(140);
    }

    [Fact]
    public async Task Resume_WhileAlreadyPrinting_IsIdempotentAndReturns200()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/resume", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cancel_WhilePrinting_RecordsHistoryAndReturnsToStandby()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");

        using HttpResponseMessage beforeTotals = await client.GetAsync("/server/history/totals");
        using JsonDocument beforeDoc = JsonDocument.Parse(await beforeTotals.Content.ReadAsStringAsync());
        double before = beforeDoc.RootElement.GetProperty("result").GetProperty("job_totals").GetProperty("total_jobs").GetDouble();

        using HttpResponseMessage response = await client.PostAsync("/printer/print/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage afterTotals = await client.GetAsync("/server/history/totals");
        using JsonDocument afterDoc = JsonDocument.Parse(await afterTotals.Content.ReadAsStringAsync());
        afterDoc.RootElement.GetProperty("result").GetProperty("job_totals").GetProperty("total_jobs").GetDouble()
            .Should().Be(before + 1);
    }

    [Fact]
    public async Task Cancel_WhenAlreadyStandby_IsIdempotentAndReturns200()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        using HttpResponseMessage response = await client.PostAsync("/printer/print/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnyPrintCommand_WhenKlippyShutdown_Returns503()
    {
        using HttpClient client = await ClientWithScenarioAsync("Shutdown");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GcodeScript_HomingWhilePrinting_Returns400Busy()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"G28"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GcodeScript_ExcludeObject_MutatesExcludedObjectsList()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"EXCLUDE_OBJECT NAME=benchy_hull"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?exclude_object");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        JsonElement excludeObject = doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("exclude_object");
        excludeObject.GetProperty("excluded_objects").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("benchy_hull");
        excludeObject.GetProperty("current_object").GetString().Should().Be("benchy_hull");
    }
}
