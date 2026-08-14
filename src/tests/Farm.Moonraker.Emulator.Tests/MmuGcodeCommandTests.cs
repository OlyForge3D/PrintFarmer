using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Direct (emulator-only) coverage of the MMU-specific gcode macros
/// <c>PrintersController</c>'s <c>MmuControlBox</c> UI sends through the same
/// <c>/printer/gcode/script</c> endpoint as every other consumed command: Happy Hare's
/// <c>MMU_CHANGE_TOOL</c>/<c>MMU_SELECT_TOOL</c>/<c>MMU_LOAD</c>/<c>MMU_EJECT</c>/
/// <c>MMU_HOME</c>/<c>MMU_RECOVER</c>, Qidibox's <c>Tn</c>/<c>UNLOAD_Tn</c>/<c>EJECT_Tn</c>,
/// and AFC's <c>CHANGE_TOOL LANE=</c>/<c>TOOL_UNLOAD LANE=</c>. These must produce real,
/// observable fixture transitions — not acknowledged no-ops — and reject
/// out-of-bounds/unknown-lane/wrong-mode parameters with a 400 instead of silently
/// succeeding.
/// </summary>
public sealed class MmuGcodeCommandTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public MmuGcodeCommandTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientWithMmuModeAsync(string mode)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json($$"""{"mode":"{{mode}}"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static Task<HttpResponseMessage> SendScriptAsync(HttpClient client, string script) =>
        client.PostAsync("/printer/gcode/script", TestRequests.Json(JsonSerializer.Serialize(new { script })));

    private static async Task<JsonElement> QueryObjectsAsync(HttpClient client, string query)
    {
        using HttpResponseMessage response = await client.GetAsync($"/printer/objects/query?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("status").Clone();
    }

    // ---- Happy Hare ----

    [Fact]
    public async Task MmuChangeTool_ValidTool_SelectsGateAndLoadsFilament()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_CHANGE_TOOL TOOL=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement mmu = (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu");
        mmu.GetProperty("tool").GetInt32().Should().Be(2);
        mmu.GetProperty("gate").GetInt32().Should().Be(2);
        mmu.GetProperty("filament").GetString().Should().Be("Loaded");
    }

    [Fact]
    public async Task MmuChangeTool_OutOfBoundsTool_Returns400AndLeavesStateUnchanged()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");
        int before = (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu").GetProperty("tool").GetInt32();

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_CHANGE_TOOL TOOL=99");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        JsonElement mmu = (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu");
        mmu.GetProperty("tool").GetInt32().Should().Be(before);
    }

    [Fact]
    public async Task MmuSelectTool_ValidTool_SelectsGateWithoutLoadingFilament()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_SELECT_TOOL TOOL=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement mmu = (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu");
        mmu.GetProperty("tool").GetInt32().Should().Be(1);
        mmu.GetProperty("gate").GetInt32().Should().Be(1);
        mmu.GetProperty("filament").GetString().Should().Be("Unloaded");
    }

    [Fact]
    public async Task MmuLoad_TransitionsFilamentStateToLoaded()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");
        (await SendScriptAsync(client, "MMU_SELECT_TOOL TOOL=0")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_LOAD");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu").GetProperty("filament")
            .GetString().Should().Be("Loaded");
    }

    [Fact]
    public async Task MmuEject_UnloadsFilamentAndClearsActiveGate()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");
        (await SendScriptAsync(client, "MMU_CHANGE_TOOL TOOL=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_EJECT");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement mmu = (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu");
        mmu.GetProperty("filament").GetString().Should().Be("Unloaded");
        mmu.GetProperty("tool").GetInt32().Should().Be(-1);
        mmu.GetProperty("gate").GetInt32().Should().Be(-1);
    }

    [Fact]
    public async Task MmuHome_SetsIsHomedTrue()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_HOME");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu").GetProperty("is_homed")
            .GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task MmuRecover_SetsActionIdle()
    {
        using HttpClient client = await ClientWithMmuModeAsync("HappyHare");

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_RECOVER");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "mmu")).GetProperty("mmu").GetProperty("action")
            .GetString().Should().Be("Idle");
    }

    [Fact]
    public async Task MmuChangeTool_WrongMmuMode_Returns400()
    {
        // No MMU attached at all — the command must fail loudly rather than silently succeed
        // with no observable effect. Explicitly set "None" first: this factory is shared across
        // tests in this class, so a prior test may have left a different mode active.
        using HttpClient client = await ClientWithMmuModeAsync("None");

        using HttpResponseMessage response = await SendScriptAsync(client, "MMU_CHANGE_TOOL TOOL=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Qidibox ----

    [Fact]
    public async Task QidiboxLoad_ValidSlot_SetsLastLoadSlot()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Qidibox");

        using HttpResponseMessage response = await SendScriptAsync(client, "T2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "save_variables")).GetProperty("save_variables")
            .GetProperty("variables").GetProperty("last_load_slot").GetString().Should().Be("slot2");
    }

    [Fact]
    public async Task QidiboxLoad_OutOfBoundsSlot_Returns400()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Qidibox");

        using HttpResponseMessage response = await SendScriptAsync(client, "T99");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QidiboxUnload_CurrentlyLoadedSlot_ClearsLastLoadSlot()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Qidibox");
        (await SendScriptAsync(client, "T1")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await SendScriptAsync(client, "UNLOAD_T1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "save_variables")).GetProperty("save_variables")
            .GetProperty("variables").GetProperty("last_load_slot").GetString().Should().Be("slot-1");
    }

    [Fact]
    public async Task QidiboxEject_MarksSlotEmptyAndClearsLoadIfActive()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Qidibox");
        (await SendScriptAsync(client, "T0")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await SendScriptAsync(client, "EJECT_T0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "save_variables&box_stepper%20slot0");
        status.GetProperty("save_variables").GetProperty("variables").GetProperty("last_load_slot")
            .GetString().Should().Be("slot-1");
        status.GetProperty("box_stepper slot0").GetProperty("runout_button").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task QidiboxCommands_WrongMmuMode_Returns400()
    {
        using HttpClient client = await ClientWithMmuModeAsync("None");

        using HttpResponseMessage response = await SendScriptAsync(client, "T0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- AFC ----

    [Fact]
    public async Task AfcChangeTool_KnownLane_SetsCurrentLoad()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Afc");

        using HttpResponseMessage response = await SendScriptAsync(client, "CHANGE_TOOL LANE=lane2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "AFC")).GetProperty("AFC").GetProperty("current_load")
            .GetString().Should().Be("lane2");
    }

    [Fact]
    public async Task AfcChangeTool_UnknownLane_Returns400()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Afc");

        using HttpResponseMessage response = await SendScriptAsync(client, "CHANGE_TOOL LANE=doesnotexist");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AfcToolUnload_CurrentlyLoadedLane_ClearsCurrentLoad()
    {
        using HttpClient client = await ClientWithMmuModeAsync("Afc");
        (await SendScriptAsync(client, "CHANGE_TOOL LANE=lane3")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await SendScriptAsync(client, "TOOL_UNLOAD LANE=lane3");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await QueryObjectsAsync(client, "AFC")).GetProperty("AFC").GetProperty("current_load")
            .ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AfcCommands_WrongMmuMode_Returns400()
    {
        using HttpClient client = await ClientWithMmuModeAsync("None");

        using HttpResponseMessage response = await SendScriptAsync(client, "CHANGE_TOOL LANE=lane1");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
