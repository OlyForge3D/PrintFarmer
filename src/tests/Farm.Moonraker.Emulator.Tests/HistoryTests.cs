using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class HistoryTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public HistoryTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task List_ReturnsSeededJobAndRespectsLimit()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/history/list?limit=1&start=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement result = doc.RootElement.GetProperty("result");
        result.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        result.GetProperty("jobs").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Job_ForKnownUid_ReturnsEntry()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/history/job?uid=seed0001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("filename").GetString().Should().Be("calibration_cube.gcode");
    }

    [Fact]
    public async Task Job_ForUnknownUid_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/history/job?uid=does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteJob_RemovesEntryFromSubsequentList()
    {
        // Record a fresh job of our own (rather than deleting the shared "seed0001"
        // entry other tests in this class depend on) by driving a real
        // start -> cancel transition through the print-control endpoints, which
        // appends a history entry with a random job_id the same way a real print
        // would. Reset to "Ready" first so the print starts from a clean baseline,
        // then advance virtual time *before* starting the print (StartPrint captures
        // Clock.UtcNow as the job's start_time) so this job's start_time is strictly
        // later than the seeded "seed0001" entry's — otherwise both would tie on
        // start_time and "order=desc" would not reliably surface this job first below.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage reset = await client.PostAsync("/__emulator/printer/scenario", TestRequests.Json("""{"scenario":"Ready"}"""));
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage advance = await client.PostAsync(
            "/__emulator/time/advance",
            TestRequests.Json("""{"seconds":5}"""));
        advance.StatusCode.Should().Be(HttpStatusCode.OK);

        // print/start now validates the filename exists in the virtual gcodes root (see
        // PrinterAggregate.StartPrint), so seed one through the real upload route first.
        await TestRequests.EnsureGcodeFileExistsAsync(client, "delete-me.gcode");

        using HttpResponseMessage start = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json("""{"filename":"delete-me.gcode"}"""));
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage cancel = await client.PostAsync("/printer/print/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage list = await client.GetAsync("/server/history/list?order=desc&limit=1");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        string jobId = listDoc.RootElement.GetProperty("result").GetProperty("jobs")[0].GetProperty("job_id").GetString()!;
        jobId.Should().NotBe("seed0001");

        using HttpResponseMessage delete = await client.DeleteAsync($"/server/history/job?uid={jobId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage getAfter = await client.GetAsync($"/server/history/job?uid={jobId}");
        getAfter.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Restore ready/standby so other tests in this class see a clean job state.
        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task Totals_ThenReset_ClearsAggregates()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage before = await client.GetAsync("/server/history/totals");
        using JsonDocument beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        beforeDoc.RootElement.GetProperty("result").GetProperty("job_totals").GetProperty("total_jobs").GetDouble()
            .Should().BeGreaterThan(0);

        using HttpResponseMessage reset = await client.PostAsync("/server/history/reset_totals", content: null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage after = await client.GetAsync("/server/history/totals");
        using JsonDocument afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        afterDoc.RootElement.GetProperty("result").GetProperty("job_totals").GetProperty("total_jobs").GetDouble().Should().Be(0);

        (await client.PostAsync("/__emulator/reset", content: null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NewlyRecordedJobs_UseDeterministicMonotonicIds_NotRandomGuids()
    {
        // Job ids for newly recorded jobs must be reproducible ("job-NNNN", strictly
        // increasing) rather than random GUID fragments, so API/UI assertions relying on
        // a specific job's id across a fixed sequence of operations are not flaky.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage reset = await client.PostAsync("/__emulator/printer/scenario", TestRequests.Json("""{"scenario":"Ready"}"""));
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        string firstJobId = await StartAndCancelAsync(client, "deterministic-1.gcode");
        string secondJobId = await StartAndCancelAsync(client, "deterministic-2.gcode");

        Match firstMatch = Regex.Match(firstJobId, "^job-(\\d{4})$");
        Match secondMatch = Regex.Match(secondJobId, "^job-(\\d{4})$");
        firstMatch.Success.Should().BeTrue($"'{firstJobId}' should match the deterministic job-NNNN format");
        secondMatch.Success.Should().BeTrue($"'{secondJobId}' should match the deterministic job-NNNN format");

        int firstSequence = int.Parse(firstMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        int secondSequence = int.Parse(secondMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        secondSequence.Should().Be(firstSequence + 1);

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    private static async Task<string> StartAndCancelAsync(HttpClient client, string filename)
    {
        // print/start now validates the filename exists in the virtual gcodes root (see
        // PrinterAggregate.StartPrint), so seed one through the real upload route first.
        await TestRequests.EnsureGcodeFileExistsAsync(client, filename);

        using HttpResponseMessage start = await client.PostAsync(
            "/printer/print/start",
            TestRequests.Json($$"""{"filename":"{{filename}}"}"""));
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage cancel = await client.PostAsync("/printer/print/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        // Match by filename rather than "order=desc&limit=1": entries recorded back to
        // back can tie on start_time (the virtual clock only advances explicitly), and a
        // stable sort keeps tied entries in original insertion order, so the newest job
        // is not guaranteed to sort first. Filename is unique per call in this test.
        using HttpResponseMessage list = await client.GetAsync("/server/history/list?limit=100");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement job = listDoc.RootElement.GetProperty("result").GetProperty("jobs")
            .EnumerateArray()
            .Single(j => j.GetProperty("filename").GetString() == filename);
        return job.GetProperty("job_id").GetString()!;
    }
}
