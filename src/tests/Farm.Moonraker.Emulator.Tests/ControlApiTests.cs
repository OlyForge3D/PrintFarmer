using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class ControlApiGatingTests : IClassFixture<DefaultDisabledControlApiFactory>
{
    private readonly DefaultDisabledControlApiFactory _factory;

    public ControlApiGatingTests(DefaultDisabledControlApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/__emulator/printer")]
    [InlineData("/__emulator/printers")]
    [InlineData("/__emulator/rules")]
    [InlineData("/__emulator/time")]
    public async Task ControlApi_DisabledByDefault_Returns404(string path)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RootResetAlias_DisabledByDefault_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/__emulator/reset", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class ControlApiScenarioAndTimeTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public ControlApiScenarioAndTimeTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task Printer_ReturnsSummaryForThisProcessInstance()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/__emulator/printer");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetString().Should().Be("ready");
        doc.RootElement.GetProperty("name").GetString().Should().Be("moonraker-ready");
    }

    [Fact]
    public async Task SwitchScenario_ChangesKlippyStateAndPrintState()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage switchResponse = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Printing"}"""));
        switchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("printing");

        using HttpResponseMessage reset = await client.PostAsync("/__emulator/printer/reset", content: null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownScenarioName_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"NotARealScenario"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Ensure the bad request never actually mutated the running scenario.
        using HttpResponseMessage query = await client.GetAsync("/printer/info");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("state").GetString().Should().Be("ready");
    }

    [Fact]
    public async Task TimeAdvance_ProgressesPrintingPrinterDeterministically()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage scenario = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Printing"}"""));
        scenario.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage before = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        double durationBefore = beforeDoc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats")
            .GetProperty("print_duration").GetDouble();

        using HttpResponseMessage advance = await client.PostAsync(
            "/__emulator/time/advance",
            TestRequests.Json("""{"seconds":120}"""));
        advance.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage after = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        double durationAfter = afterDoc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats")
            .GetProperty("print_duration").GetDouble();

        durationAfter.Should().BeGreaterThan(durationBefore);
        (durationAfter - durationBefore).Should().BeApproximately(120, 0.01);

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task TimeReset_RestoresVirtualClockToEpoch()
    {
        using HttpClient client = _factory.CreateClient();
        await client.PostAsync("/__emulator/time/advance", TestRequests.Json("""{"seconds":500}"""));

        using HttpResponseMessage reset = await client.PostAsync("/__emulator/time/reset", content: null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage time = await client.GetAsync("/__emulator/time");
        using JsonDocument doc = JsonDocument.Parse(await time.Content.ReadAsStringAsync());
        DateTimeOffset virtualTime = doc.RootElement.GetProperty("virtualTime").GetDateTimeOffset();
        virtualTime.Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task TimeReset_DuringActivePrint_ImmediatelyPublishesZeroTelemetry()
    {
        using HttpClient client = _factory.CreateClient();
        (await client.PostAsync(
            "/__emulator/time/advance",
            TestRequests.Json("""{"seconds":120}"""))).EnsureSuccessStatusCode();
        (await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""))).EnsureSuccessStatusCode();
        (await client.PostAsync("/__emulator/time/reset", content: null)).EnsureSuccessStatusCode();

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument queryDoc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        JsonElement stats = queryDoc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats");
        stats.GetProperty("print_duration").GetDouble().Should().Be(0);
        stats.GetProperty("total_duration").GetDouble().Should().Be(0);
        stats.GetProperty("filament_used").GetDouble().Should().Be(0);

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task TimeAdvance_NegativeSeconds_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/time/advance",
            TestRequests.Json("""{"seconds":-5}"""));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Printers_RootAlias_ReturnsArrayContainingThisProcessInstance()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/__emulator/printers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);

        JsonElement entry = doc.RootElement[0];
        entry.GetProperty("id").GetString().Should().Be("ready");
        entry.GetProperty("name").GetString().Should().Be("moonraker-ready");
    }

    [Fact]
    public async Task Printers_RootAlias_MatchesSingularPrinterEndpoint()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage singular = await client.GetAsync("/__emulator/printer");
        using HttpResponseMessage plural = await client.GetAsync("/__emulator/printers");

        using JsonDocument singularDoc = JsonDocument.Parse(await singular.Content.ReadAsStringAsync());
        using JsonDocument pluralDoc = JsonDocument.Parse(await plural.Content.ReadAsStringAsync());

        pluralDoc.RootElement[0].GetProperty("id").GetString()
            .Should().Be(singularDoc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task RootReset_AliasBehavesLikePrinterReset()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage scenario = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Printing"}"""));
        scenario.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage reset = await client.PostAsync("/__emulator/reset", content: null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument resetDoc = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
        resetDoc.RootElement.GetProperty("id").GetString().Should().Be("ready");
        resetDoc.RootElement.GetProperty("printState").GetString().Should().Be("standby");

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("standby");
    }

    [Fact]
    public async Task RootReset_RestoresFilesHistorySpoolmanAndMmuFixtures()
    {
        using HttpClient client = _factory.CreateClient();

        (await client.DeleteAsync("/server/files/gcodes/benchy.gcode")).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/server/history/job?uid=seed0001")).EnsureSuccessStatusCode();
        (await client.PostAsync(
            "/server/spoolman/spool_id",
            TestRequests.Json("""{"spool_id":2}"""))).EnsureSuccessStatusCode();
        (await client.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"Afc"}"""))).EnsureSuccessStatusCode();

        using HttpResponseMessage reset = await client.PostAsync("/__emulator/reset", content: null);
        reset.EnsureSuccessStatusCode();

        using JsonDocument files = JsonDocument.Parse(await client.GetStringAsync("/server/files/list?root=gcodes"));
        files.RootElement.GetProperty("result").EnumerateArray()
            .Select(file => file.GetProperty("path").GetString())
            .Should().Equal("benchy.gcode");

        using JsonDocument history = JsonDocument.Parse(await client.GetStringAsync("/server/history/list?limit=100"));
        JsonElement historyResult = history.RootElement.GetProperty("result");
        historyResult.GetProperty("count").GetInt32().Should().Be(1);
        JsonElement seededJob = historyResult.GetProperty("jobs").EnumerateArray().Single();
        seededJob.GetProperty("job_id").GetString().Should().Be("seed0001");
        seededJob.GetProperty("filename").GetString().Should().Be("calibration_cube.gcode");

        using JsonDocument totals = JsonDocument.Parse(await client.GetStringAsync("/server/history/totals"));
        JsonElement jobTotals = totals.RootElement.GetProperty("result").GetProperty("job_totals");
        jobTotals.GetProperty("total_jobs").GetDouble().Should().Be(1);
        jobTotals.GetProperty("total_print_time").GetDouble().Should().Be(3550);
        jobTotals.GetProperty("total_filament_used").GetDouble().Should().Be(12.4);

        using JsonDocument spool = JsonDocument.Parse(await client.GetStringAsync("/server/spoolman/spool_id"));
        spool.RootElement.GetProperty("result").GetProperty("spool_id").GetInt32().Should().Be(1);

        using JsonDocument mmu = JsonDocument.Parse(await client.GetStringAsync("/__emulator/printer/mmu"));
        mmu.RootElement.GetProperty("mode").GetString().Should().Be("None");
    }
}
