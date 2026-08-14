using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Exercises the real, unchanged <see cref="MoonrakerClient"/> from
/// <c>Farm.Backend.Plugin.Moonraker</c> against a genuinely network-listening emulator
/// instance (see <see cref="RealEmulatorHost"/>) — not the emulator's own test suite
/// asserting against its own HTTP handlers, but the actual production backend client
/// PrintFarmer ships, proving it round-trips correctly against the emulator's wire
/// format for the REST surfaces it consumes.
/// </summary>
public sealed class RealMoonrakerClientIntegrationTests : IClassFixture<RealEmulatorHost>, IAsyncLifetime
{
    private readonly RealEmulatorHost _host;
    private readonly HttpClient _http = new();
    private MoonrakerClient _client = null!;

    public RealMoonrakerClientIntegrationTests(RealEmulatorHost host) => _host = host;

    public Task InitializeAsync()
    {
        _client = new MoonrakerClient(_http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        return _host.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStatusAsync_ReadyScenario_ReportsOnlineAndReadyState()
    {
        PrinterStatus status = await _client.GetStatusAsync(_host.BaseUrl);

        status.IsOnline.Should().BeTrue();
        status.State.Should().Be("ready");
    }

    [Fact]
    public async Task GetPrinterInfoAsync_ReturnsEmulatorHostnameAndReadyState()
    {
        MoonrakerPrinterInfo? info = await _client.GetPrinterInfoAsync(_host.BaseUrl);

        info.Should().NotBeNull();
        info!.State.Should().Be("ready");
        info.Hostname.Should().Be("moonraker-real-ready");
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WhilePrinting_ReportsJobNameAndProgress()
    {
        await SwitchScenarioAsync("Printing");
        await AdvanceTimeAsync(60);

        PrinterCompositeStatus status = await _client.GetCompositeStatusAsync(_host.BaseUrl);

        status.IsOnline.Should().BeTrue();
        status.State.Should().Be("printing");
        status.JobName.Should().Be("benchy.gcode");
        status.Progress.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WhilePaused_RetainsJobNameProgressAndDuration()
    {
        await SwitchScenarioAsync("Paused");

        PrinterCompositeStatus status = await _client.GetCompositeStatusAsync(_host.BaseUrl);
        PrinterJob? job = await _client.GetJobAsync(_host.BaseUrl);

        status.IsOnline.Should().BeTrue();
        status.State.Should().Be("paused");
        status.JobName.Should().Be("benchy.gcode");
        status.Progress.Should().Be(20);
        job.Should().NotBeNull();
        job!.PrintDurationSeconds.Should().Be(120);
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WhilePrinting_ThumbnailUrlResolvesToRealPngBytes()
    {
        // Guards against the thumbnail wire-path mismatch: MoonrakerClient.GetJobAsync builds a
        // print job's thumbnail URL as {baseUrl}/server/files/gcodes/{relative_path} (the generic
        // gcode-root download route, not the dedicated server/files/thumbs/{file} route), using
        // the seeded metadata's "thumbs/benchy-32x32.png" relative_path. That URL must actually
        // resolve to real PNG bytes rather than 404ing.
        await SwitchScenarioAsync("Printing");

        PrinterCompositeStatus status = await _client.GetCompositeStatusAsync(_host.BaseUrl);
        status.ThumbnailUrl.Should().NotBeNullOrEmpty();
        status.ThumbnailUrl.Should().Contain("/server/files/gcodes/");

        using HttpResponseMessage thumbnail = await _http.GetAsync(status.ThumbnailUrl);
        thumbnail.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        thumbnail.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        byte[] bytes = await thumbnail.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
    }

    [Fact]
    public async Task GetFileMetadataAsync_ForSeededFile_ReturnsSlicerAndThumbnails()
    {
        GCodeMetadata? metadata = await _client.GetFileMetadataAsync(_host.BaseUrl, "benchy.gcode");

        metadata.Should().NotBeNull();
        metadata!.Slicer.Should().Be("OrcaSlicer");
        metadata.Thumbnails.Should().NotBeEmpty();
        metadata.ObjectInfo.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PrintControl_PauseThenResumeThenCancel_AllSucceedThroughRealClient()
    {
        await SwitchScenarioAsync("Printing");

        (await _client.PauseAsync(_host.BaseUrl)).Should().BeTrue();

        PrinterStatus paused = await _client.GetStatusAsync(_host.BaseUrl);
        paused.IsOnline.Should().BeTrue();

        (await _client.ResumeAsync(_host.BaseUrl)).Should().BeTrue();
        (await _client.CancelPrintAsync(_host.BaseUrl)).Should().BeTrue();

        PrinterJob? job = await _client.GetJobAsync(_host.BaseUrl);
        job.Should().NotBeNull();
        job!.PrintState.Should().NotBe("printing");
    }

    [Fact]
    public async Task StartPrintAsync_ExistingSeededFile_TransitionsToPrinting()
    {
        (await _client.StartPrintAsync(_host.BaseUrl, "benchy.gcode")).Should().BeTrue();

        PrinterJob? job = await _client.GetJobAsync(_host.BaseUrl);
        job.Should().NotBeNull();
        job!.PrintState.Should().Be("printing");
    }

    [Fact]
    public async Task StartPrintAsync_UnknownFilename_ReturnsFalseNotFabricatedSuccess()
    {
        // Guards against the historic "any filename succeeds" bug: the real client must see this
        // as a failure (non-success HTTP status), and print state must not have changed.
        (await _client.StartPrintAsync(_host.BaseUrl, "does-not-exist.gcode")).Should().BeFalse();

        PrinterStatus status = await _client.GetStatusAsync(_host.BaseUrl);
        status.State.Should().NotBe("printing");
    }

    [Fact]
    public async Task GetHistoryListAsync_AndGetHistoryTotalsAsync_ReturnSeededData()
    {
        HistoryListResponse? history = await _client.GetHistoryListAsync(_host.BaseUrl, limit: 5, start: 0);
        history.Should().NotBeNull();
        history!.Jobs.Should().Contain(j => j.JobId == "seed0001");

        HistoryTotals? totals = await _client.GetHistoryTotalsAsync(_host.BaseUrl);
        totals.Should().NotBeNull();
        totals!.JobTotals.TotalJobs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Webcam_ConfiguredCameraUrls_ResolvesSeededWebcam()
    {
        (string? streamUrl, string? snapshotUrl) = await _client.GetConfiguredCameraUrlsAsync(_host.BaseUrl);

        streamUrl.Should().NotBeNullOrEmpty();
        snapshotUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Spoolman_StatusAndActiveSpoolRoundTrip_WorkThroughRealClient()
    {
        SpoolmanStatus? status = await _client.GetSpoolmanStatusAsync(_host.BaseUrl);
        status.Should().NotBeNull();
        status!.SpoolmanConnected.Should().BeTrue();

        int? activeSpool = await _client.GetSpoolmanActiveSpoolAsync(_host.BaseUrl);
        activeSpool.Should().Be(1);

        (await _client.SetSpoolmanActiveSpoolAsync(_host.BaseUrl, 2)).Should().BeTrue();
        (await _client.GetSpoolmanActiveSpoolAsync(_host.BaseUrl)).Should().Be(2);

        // Restore for other tests in this class.
        await _client.SetSpoolmanActiveSpoolAsync(_host.BaseUrl, 1);
    }

    [Fact]
    public async Task EmergencyStopAsync_ThenFirmwareRestartAsync_RecoversPrinter()
    {
        (await _client.EmergencyStopAsync(_host.BaseUrl)).Should().BeTrue();

        PrinterStatus stopped = await _client.GetStatusAsync(_host.BaseUrl);
        stopped.State.Should().Be("shutdown");

        (await _client.FirmwareRestartAsync(_host.BaseUrl)).Should().BeTrue();

        PrinterStatus recovered = await _client.GetStatusAsync(_host.BaseUrl);
        recovered.IsOnline.Should().BeTrue();
        recovered.State.Should().Be("ready");
    }

    [Fact]
    public async Task SetTempsAsync_SetsExtruderAndBedTargets()
    {
        (await _client.SetTempsAsync(_host.BaseUrl, hotend: 200, bed: 55)).Should().BeTrue();

        PrinterCompositeStatus status = await _client.GetCompositeStatusAsync(_host.BaseUrl);
        status.HotendTarget.Should().Be(200);
        status.BedTarget.Should().Be(55);
    }

    [Fact]
    public async Task Homing_SendHomeThenPartialHomeAxes_UpdateHomedAxes()
    {
        // Start from a known "not homed" baseline the same way a real MCU reboot would.
        (await _client.FirmwareRestartAsync(_host.BaseUrl)).Should().BeTrue();
        (await QueryHomedAxesAsync()).Should().BeEmpty();

        (await _client.SendHomeAsync(_host.BaseUrl)).Should().BeTrue();
        (await QueryHomedAxesAsync()).Should().Be("xyz");

        (await _client.FirmwareRestartAsync(_host.BaseUrl)).Should().BeTrue();
        (await _client.HomeXYAsync(_host.BaseUrl)).Should().BeTrue();
        (await QueryHomedAxesAsync()).Should().Be("xy");

        (await _client.HomeZAsync(_host.BaseUrl)).Should().BeTrue();
        (await QueryHomedAxesAsync()).Should().Be("xyz");
    }

    [Fact]
    public async Task MoveAsync_RelativeMove_UpdatesPositionByDelta()
    {
        double[] before = await QueryPositionAsync();

        (await _client.MoveAsync(_host.BaseUrl, x: 5, y: -2.5, z: 0.2, f: 3000)).Should().BeTrue();

        double[] after = await QueryPositionAsync();
        after[0].Should().BeApproximately(before[0] + 5, 0.001);
        after[1].Should().BeApproximately(before[1] - 2.5, 0.001);
        after[2].Should().BeApproximately(before[2] + 0.2, 0.001);
    }

    [Fact]
    public async Task MoveToAsync_AbsoluteMove_SetsPositionDirectly()
    {
        (await _client.MoveToAsync(_host.BaseUrl, x: 33, y: 44, z: 6, f: 1500)).Should().BeTrue();

        double[] position = await QueryPositionAsync();
        position[0].Should().BeApproximately(33, 0.001);
        position[1].Should().BeApproximately(44, 0.001);
        position[2].Should().BeApproximately(6, 0.001);
    }

    [Fact]
    public async Task AcknowledgedNoOpCommands_DisableMotorsLoadUnloadChangeFilament_AllReturnSuccess()
    {
        // Documented fidelity boundary (see PrinterAggregate.SendGcode): these commands
        // are accepted end to end through the real client but intentionally leave no
        // additional observable emulator state to assert against today.
        (await _client.DisableMotorsAsync(_host.BaseUrl)).Should().BeTrue();
        (await _client.LoadFilamentAsync(_host.BaseUrl)).Should().BeTrue();
        (await _client.UnloadFilamentAsync(_host.BaseUrl)).Should().BeTrue();
        (await _client.ChangeFilamentAsync(_host.BaseUrl)).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryLifecycle_CreateListDelete_RoundTripsThroughRealClient()
    {
        // Guards against the historic "fabricated success" bug: create/delete must actually
        // mutate VirtualFileSystem, not just echo back a success envelope.
        DirectoryCreateResponse? created = await _client.CreateDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dir");
        created.Should().NotBeNull();
        created!.Item.Path.Should().Be("real-client-dir");
        created.Action.Should().Be("create_dir");

        MoonrakerDirectoryInfo? emptyListing = await _client.GetDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dir");
        emptyListing.Should().NotBeNull();
        emptyListing!.Dirs.Should().BeEmpty();
        emptyListing.Files.Should().BeEmpty();

        using MemoryStream listingContent = new("; listed\nG28\n"u8.ToArray());
        (await _client.UploadFileAsync(_host.BaseUrl, "gcodes", "real-client-dir/listed.gcode", listingContent)).Should().NotBeNull();
        MoonrakerDirectoryInfo? populatedListing = await _client.GetDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dir");
        populatedListing!.Files.Select(file => file.Path).Should().Contain("real-client-dir/listed.gcode");

        MoonrakerDirectoryInfo? rootListing = await _client.GetDirectoryAsync(_host.BaseUrl, "gcodes");
        rootListing.Should().NotBeNull();
        rootListing!.Dirs.Select(d => d.Dirname).Should().Contain("real-client-dir");

        (await _client.DeleteFileOrDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dir", force: true)).Should().BeTrue();

        MoonrakerDirectoryInfo? afterDelete = await _client.GetDirectoryAsync(_host.BaseUrl, "gcodes");
        afterDelete!.Dirs.Select(d => d.Dirname).Should().NotContain("real-client-dir");
    }

    [Fact]
    public async Task CreateDirectory_AlreadyExists_RealClientReturnsNull()
    {
        (await _client.CreateDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dup-dir")).Should().NotBeNull();

        (await _client.CreateDirectoryAsync(_host.BaseUrl, "gcodes/real-client-dup-dir")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteDirectory_NonEmptyWithoutForce_RealClientReturnsFalse_ThenForceSucceeds()
    {
        (await _client.CreateDirectoryAsync(_host.BaseUrl, "gcodes/real-client-nonempty-dir")).Should().NotBeNull();

        using MemoryStream contentStream = new("; nested\nG28\n"u8.ToArray());
        (await _client.UploadFileAsync(_host.BaseUrl, "gcodes", "real-client-nonempty-dir/inner.gcode", contentStream)).Should().NotBeNull();

        (await _client.DeleteFileOrDirectoryAsync(_host.BaseUrl, "gcodes/real-client-nonempty-dir", force: false)).Should().BeFalse();

        (await _client.DeleteFileOrDirectoryAsync(_host.BaseUrl, "gcodes/real-client-nonempty-dir", force: true)).Should().BeTrue();

        MoonrakerDirectoryInfo? afterDelete = await _client.GetDirectoryAsync(_host.BaseUrl, "gcodes");
        afterDelete!.Dirs.Select(d => d.Dirname).Should().NotContain("real-client-nonempty-dir");
    }

    [Fact]
    public async Task SendGcodeAsync_MmuChangeTool_HappyHare_TransitionsFixtureState()
    {
        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"HappyHare"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            (await _client.SendGcodeAsync(_host.BaseUrl, "MMU_CHANGE_TOOL TOOL=2")).Should().BeTrue();

            using HttpResponseMessage query = await _http.GetAsync($"{_host.BaseUrl}/printer/objects/query?mmu");
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            System.Text.Json.JsonElement mmu = doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("mmu");
            mmu.GetProperty("tool").GetInt32().Should().Be(2);
            mmu.GetProperty("filament").GetString().Should().Be("Loaded");

            // Guards the out-of-bounds case too: the real client must see this as a failure.
            (await _client.SendGcodeAsync(_host.BaseUrl, "MMU_CHANGE_TOOL TOOL=99")).Should().BeFalse();
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task SendGcodeAsync_QidiboxLoad_TransitionsFixtureState()
    {
        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"Qidibox"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            (await _client.SendGcodeAsync(_host.BaseUrl, "T2")).Should().BeTrue();

            using HttpResponseMessage query = await _http.GetAsync($"{_host.BaseUrl}/printer/objects/query?save_variables");
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("save_variables")
                .GetProperty("variables").GetProperty("last_load_slot").GetString().Should().Be("slot2");
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task SendGcodeAsync_AfcChangeTool_TransitionsFixtureState()
    {
        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"Afc"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            (await _client.SendGcodeAsync(_host.BaseUrl, "CHANGE_TOOL LANE=lane3")).Should().BeTrue();

            using HttpResponseMessage query = await _http.GetAsync($"{_host.BaseUrl}/printer/objects/query?AFC");
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("AFC")
                .GetProperty("current_load").GetString().Should().Be("lane3");

            // Guards the unknown-lane case too.
            (await _client.SendGcodeAsync(_host.BaseUrl, "CHANGE_TOOL LANE=doesnotexist")).Should().BeFalse();
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    private async Task<string> QueryHomedAxesAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync($"{_host.BaseUrl}/printer/objects/query?toolhead=homed_axes");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("toolhead").GetProperty("homed_axes").GetString()!;
    }

    private async Task<double[]> QueryPositionAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync($"{_host.BaseUrl}/printer/objects/query?toolhead=position");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("toolhead").GetProperty("position")
            .EnumerateArray().Select(e => e.GetDouble()).ToArray();
    }

    private async Task SwitchScenarioAsync(string scenario)
    {
        using HttpResponseMessage response = await _host.ControlClient.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json($$"""{"scenario":"{{scenario}}"}"""));
        response.EnsureSuccessStatusCode();
    }

    private async Task AdvanceTimeAsync(double seconds)
    {
        using HttpResponseMessage response = await _host.ControlClient.PostAsync(
            "/__emulator/time/advance",
            TestRequests.Json($$"""{"seconds":{{seconds}}}"""));
        response.EnsureSuccessStatusCode();
    }
}
