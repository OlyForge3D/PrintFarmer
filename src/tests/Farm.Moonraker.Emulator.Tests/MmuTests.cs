using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Contract tests for the MMU/filament-changer fixture: control-API gating and mode
/// switching, and the exact wire shapes <see cref="Farm.Moonraker.Emulator.Domain.MmuMode.HappyHare"/>,
/// <see cref="Farm.Moonraker.Emulator.Domain.MmuMode.Afc"/>, and <see cref="Farm.Moonraker.Emulator.Domain.MmuMode.Qidibox"/> emit through
/// <c>printer/objects/list</c>/<c>printer/objects/query</c> (and, for Qidibox, the seeded
/// <c>server/files/config/officiall_filas_list.cfg</c> dictionary). See
/// <c>RealMoonrakerSubscriptionServiceIntegrationTests</c> for coverage of the same shapes
/// parsed end-to-end by the real, unchanged <c>MoonrakerSubscriptionService</c>.
/// </summary>
public sealed class MmuTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public MmuTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task SetMmuModeAsync(HttpClient client, string mode)
    {
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json($$"""{"mode":"{{mode}}"}"""));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DefaultMode_None_ObjectsSnapshotOmitsMmuAndAfc()
    {
        using HttpClient client = _factory.CreateClient();
        await SetMmuModeAsync(client, "None");

        using HttpResponseMessage response = await client.GetAsync("/printer/objects/list");
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] objects = doc.RootElement.GetProperty("result").GetProperty("objects")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

        objects.Should().NotContain("mmu");
        objects.Should().NotContain("AFC");
        objects.Should().NotContain("save_variables");
        objects.Should().NotContain(o => o != null && o.StartsWith("box_stepper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HappyHareMode_ObjectsQuery_UsesToolGateKeys_NotActiveToolActiveGate()
    {
        // Guards the exact bug reported: BuildObjectsSnapshot must emit "tool"/"gate" (the keys
        // MoonrakerSubscriptionService.HandleMmuUpdate actually reads), not "active_tool"/
        // "active_gate" — the latter would silently drop every MMU field on the real client.
        using HttpClient client = _factory.CreateClient();
        try
        {
            await SetMmuModeAsync(client, "HappyHare");

            using HttpResponseMessage listResponse = await client.GetAsync("/printer/objects/list");
            using JsonDocument listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            listDoc.RootElement.GetProperty("result").GetProperty("objects")
                .EnumerateArray().Select(e => e.GetString()).Should().Contain("mmu");

            using HttpResponseMessage response = await client.GetAsync("/printer/objects/query?mmu");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement mmu = doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("mmu");

            mmu.TryGetProperty("tool", out _).Should().BeTrue();
            mmu.TryGetProperty("gate", out _).Should().BeTrue();
            mmu.TryGetProperty("active_tool", out _).Should().BeFalse();
            mmu.TryGetProperty("active_gate", out _).Should().BeFalse();

            mmu.TryGetProperty("gate_spool_id", out JsonElement gateSpoolId).Should().BeTrue();
            gateSpoolId.EnumerateArray().Select(e => e.GetInt32()).Should().Equal(101, 102, -1, -1);
        }
        finally
        {
            await SetMmuModeAsync(client, "None");
        }
    }

    [Fact]
    public async Task AfcMode_ObjectsSnapshot_IncludesAfcAndPerLaneObjects()
    {
        using HttpClient client = _factory.CreateClient();
        try
        {
            await SetMmuModeAsync(client, "Afc");

            using HttpResponseMessage listResponse = await client.GetAsync("/printer/objects/list");
            using JsonDocument listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            string[] objects = listDoc.RootElement.GetProperty("result").GetProperty("objects")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            objects.Should().Contain("AFC");
            objects.Should().Contain("AFC_stepper lane1");
            objects.Should().NotContain("mmu");

            using HttpResponseMessage response = await client.GetAsync("/printer/objects/query?AFC&AFC_stepper%20lane1");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement status = doc.RootElement.GetProperty("result").GetProperty("status");

            JsonElement afc = status.GetProperty("AFC");
            afc.GetProperty("current_state").GetString().Should().Be("Idle");
            afc.GetProperty("lanes").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("lane1", "lane2", "lane3", "lane4");

            JsonElement lane1 = status.GetProperty("AFC_stepper lane1");
            lane1.GetProperty("material").GetString().Should().Be("PLA");
            lane1.GetProperty("spool_id").GetInt32().Should().Be(101);
            lane1.GetProperty("load_state").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await SetMmuModeAsync(client, "None");
        }
    }

    [Fact]
    public async Task SwitchingModes_RemovesThePreviousModesObjectsFromTheSnapshot()
    {
        using HttpClient client = _factory.CreateClient();
        try
        {
            await SetMmuModeAsync(client, "HappyHare");
            await SetMmuModeAsync(client, "Afc");

            using HttpResponseMessage response = await client.GetAsync("/printer/objects/list");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string[] objects = doc.RootElement.GetProperty("result").GetProperty("objects")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();

            objects.Should().Contain("AFC");
            objects.Should().NotContain("mmu");
        }
        finally
        {
            await SetMmuModeAsync(client, "None");
        }
    }

    [Fact]
    public async Task QidiboxMode_ObjectsSnapshot_IncludesBoxStepperAndSaveVariables()
    {
        using HttpClient client = _factory.CreateClient();
        try
        {
            await SetMmuModeAsync(client, "Qidibox");

            using HttpResponseMessage listResponse = await client.GetAsync("/printer/objects/list");
            using JsonDocument listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            string[] objects = listDoc.RootElement.GetProperty("result").GetProperty("objects")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            objects.Should().Contain("save_variables");
            objects.Should().Contain("box_stepper slot0");
            objects.Should().Contain("box_stepper slot3");
            objects.Should().NotContain("mmu");
            objects.Should().NotContain("AFC");

            using HttpResponseMessage response = await client.GetAsync(
                "/printer/objects/query?save_variables&box_stepper%20slot0&box_stepper%20slot2&box_stepper%20slot3");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement status = doc.RootElement.GetProperty("result").GetProperty("status");

            JsonElement variables = status.GetProperty("save_variables").GetProperty("variables");
            variables.GetProperty("box_count").GetInt32().Should().Be(1);
            variables.GetProperty("last_load_slot").GetString().Should().Be("slot0");
            variables.GetProperty("filament_slot0").GetInt32().Should().Be(1);
            variables.GetProperty("filament_slot1").GetInt32().Should().Be(2);
            variables.GetProperty("color_slot0").GetInt32().Should().Be(1);
            variables.GetProperty("color_slot1").GetInt32().Should().Be(2);

            status.GetProperty("box_stepper slot0").GetProperty("runout_button").GetInt32().Should().Be(0);
            status.GetProperty("box_stepper slot2").GetProperty("runout_button").GetInt32().Should().Be(1);
            status.GetProperty("box_stepper slot3").GetProperty("runout_button").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await SetMmuModeAsync(client, "None");
        }
    }

    [Fact]
    public async Task QidiboxDictionary_ServedThroughConfigRoot_MatchesSeededCodesAndParsesAsIni()
    {
        // The real MoonrakerSubscriptionService.FetchQidiboxDictionaryAsync fetches this exact
        // path with a raw HttpClient GET (not through MoonrakerClient's {"result": ...} envelope),
        // so the response body must be the bare INI text.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/config/officiall_filas_list.cfg");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("[colordict]");
        content.Should().Contain("1 = #FF0000");
        content.Should().Contain("2 = #00A0FF");
        content.Should().Contain("[fila1]");
        content.Should().Contain("filament = PLA");
        content.Should().Contain("[fila2]");
        content.Should().Contain("filament = PETG");
    }

    [Fact]
    public async Task QidiboxDictionary_UnknownConfigPath_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/config/does-not-exist.cfg");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SnapmakerU1Mode_ObjectsSnapshot_IncludesPrintTaskConfigAndActiveExtruder()
    {
        // Guards SnapmakerU1PrintTaskConfigParser's exact expected shape: toolhead.extruder
        // names the active *physical* toolhead (not an MMU virtual gate), and print_task_config
        // carries parallel filament_* arrays — no "mmu"/"AFC"/Qidibox objects should appear
        // alongside it, since a printer has either a Klipper MMU or U1's native toolheads.
        using HttpClient client = _factory.CreateClient();
        try
        {
            await SetMmuModeAsync(client, "SnapmakerU1");

            using HttpResponseMessage listResponse = await client.GetAsync("/printer/objects/list");
            using JsonDocument listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            string[] objects = listDoc.RootElement.GetProperty("result").GetProperty("objects")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            objects.Should().Contain("print_task_config");
            objects.Should().NotContain("mmu");
            objects.Should().NotContain("AFC");
            objects.Should().NotContain("save_variables");

            using HttpResponseMessage response = await client.GetAsync("/printer/objects/query?toolhead&print_task_config");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement status = doc.RootElement.GetProperty("result").GetProperty("status");

            status.GetProperty("toolhead").GetProperty("extruder").GetString().Should().Be("extruder1");

            JsonElement config = status.GetProperty("print_task_config");
            config.GetProperty("filament_exist").EnumerateArray().Select(e => e.GetBoolean())
                .Should().Equal(true, true, false, false);
            config.GetProperty("filament_type").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("PLA", "PETG", "NONE", "NONE");
            config.GetProperty("filament_color_rgba").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("FF0000FF", "00A0FFFF", "00000000", "00000000");
        }
        finally
        {
            await SetMmuModeAsync(client, "None");
        }
    }

    [Fact]
    public async Task UnknownMode_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"NotARealProtocol"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>Verifies the MMU control routes are gated exactly like every other <c>/__emulator/**</c> route.</summary>
public sealed class MmuControlApiGatingTests : IClassFixture<DefaultDisabledControlApiFactory>
{
    private readonly DefaultDisabledControlApiFactory _factory;

    public MmuControlApiGatingTests(DefaultDisabledControlApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task MmuRoute_DisabledByDefault_Returns404(string method)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/__emulator/printer/mmu");
        if (method == "POST")
        {
            request.Content = TestRequests.Json("""{"mode":"HappyHare"}""");
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
