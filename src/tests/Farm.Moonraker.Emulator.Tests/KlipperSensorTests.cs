using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Moonraker.Emulator.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class KlipperSensorTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory factory;

    public KlipperSensorTests(ReadyPrinterFactory factory) => this.factory = factory;

    [Fact]
    public async Task ObjectsQuery_PostBody_HonorsObjectAndFieldSelections()
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        using HttpResponseMessage response = await client.PostAsync("/printer/objects/query", TestRequests.Json(
            """{"objects":{"extruder":["temperature","missing"],"heater_generic chamber":null,"unknown":null,"toolhead":[]}}"""));
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement status = document.RootElement.GetProperty("result").GetProperty("status");
        status.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("extruder", "heater_generic chamber", "toolhead");
        status.GetProperty("extruder").EnumerateObject().Select(property => property.Name).Should().Equal("temperature");
        AssertJson("""{"temperature":24,"target":0,"power":0}""", status.GetProperty("heater_generic chamber"));
        AssertJson("{}", status.GetProperty("toolhead"));
    }

    [Theory]
    [InlineData("""{"objects":{}}""", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("""{"objects":null}""", HttpStatusCode.BadRequest)]
    public async Task ObjectsQuery_EmptyOrMissingPostSelection_DoesNotQueryAllObjects(string body, HttpStatusCode expected)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/printer/objects/query", TestRequests.Json(body));
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertJson("{}", document.RootElement.GetProperty("result").GetProperty("status"));
        }
    }

    [Fact]
    public async Task FourToolPrinter_RealClientQueriesAndCommands_KeepEachHeaterIndependentAndShutdownAll()
    {
        using HttpClient http = factory.CreateClient();
        await ResetAsync(http);
        using HttpResponseMessage mode = await http.PostAsync("/__emulator/printer/mmu", TestRequests.Json("""{"mode":"SnapmakerU1"}"""));
        mode.EnsureSuccessStatusCode();
        try
        {
            var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
            string root = http.BaseAddress!.AbsoluteUri;
            for (int index = 0; index < 4; index++)
            {
                string id = index == 0 ? "extruder" : $"extruder{index}";
                (await client.SetSensorValueAsync(root, id, PrinterSensorQuantity.TargetTemperature, 210 + index, null)).Should().BeTrue();
            }

            using HttpResponseMessage select = await http.PostAsync("/printer/gcode/script", TestRequests.Json("""{"script":"T3"}"""));
            select.EnsureSuccessStatusCode();
            PrinterCompositeStatus status = await client.GetCompositeStatusAsync(root, null);
            status.ActiveExtruderId.Should().Be("extruder3");
            status.HotendTarget.Should().Be(213);
            status.HotendTemp.Should().BeApproximately(26.4, 0.0001);
            Assert.NotNull(status.Sensors);
            status.Sensors.Where(sensor => sensor.Kind == PrinterSensorKind.Nozzle)
                .Select(sensor => sensor.Id).Should().BeEquivalentTo("extruder", "extruder1", "extruder2", "extruder3");

            var breakers = new Mock<ICircuitBreakerService>();
            breakers.Setup(service => service.GetCircuitBreaker(It.IsAny<string>(), null, null, null))
                .Returns(new CircuitBreaker());
            var statusClient = new MoonrakerStatusClient(
                client, breakers.Object,
                new ManagedSpoolProviderHelper(Mock.Of<ISpoolmanStatusCache>(), NullLogger<ManagedSpoolProviderHelper>.Instance),
                NullLogger<MoonrakerStatusClient>.Instance);
            PrinterStatusDto polled = await statusClient.GetPrinterStatusAsync(
                new Printer { Id = Guid.NewGuid(), Name = "Four tool", ServerUrl = root, BackendPort = 80 },
                CancellationToken.None);
            polled.ActiveExtruderId.Should().Be("extruder3");
            polled.Sensors.Should().BeEquivalentTo(status.Sensors, options => options.Excluding(sensor => sensor.LastUpdated));
            polled.Sensors.Should().OnlyContain(sensor => sensor.LastUpdated.HasValue);
            polled.HotendTemp.Should().BeApproximately(26.4, 0.0001);

            (await client.SetTempsAsync(root, 220.5, null)).Should().BeTrue();
            PrinterCompositeStatus edited = await client.GetCompositeStatusAsync(root, null);
            for (int attempt = 0; attempt < 10 && edited.HotendTarget != 220.5; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                edited = await client.GetCompositeStatusAsync(root, null);
            }

            edited.HotendTarget.Should().Be(220.5);
            Assert.NotNull(edited.Sensors);
            edited.Sensors.Single(sensor => sensor.Id == "extruder").Readings
                .Single(reading => reading.Quantity == PrinterSensorQuantity.TargetTemperature).Value.Number.Should().Be(210);
            (await client.SetSensorValueAsync(root, "extruder3", PrinterSensorQuantity.TargetTemperature, 5, null)).Should().BeFalse();
            (await client.SetSensorValueAsync(root, "extruder3", PrinterSensorQuantity.TargetTemperature, 271, null)).Should().BeFalse();
            (await client.SetSensorValueAsync(root, "extruder3", PrinterSensorQuantity.TargetTemperature, 0, null)).Should().BeTrue();
            (await client.SetSensorValueAsync(root, "heater_generic chamber", PrinterSensorQuantity.TargetTemperature, 40, null)).Should().BeTrue();
            (await client.SetTempsAsync(root, 0, 0)).Should().BeTrue();
            PrinterCompositeStatus ordinaryZeros = await client.GetCompositeStatusAsync(root, null);
            Assert.NotNull(ordinaryZeros.Sensors);
            ordinaryZeros.Sensors.Single(sensor => sensor.Id == "extruder1").Readings
                .Single(reading => reading.Quantity == PrinterSensorQuantity.TargetTemperature).Value.Number.Should().Be(211);
            ordinaryZeros.Sensors.Single(sensor => sensor.Id == "heater_generic chamber").Readings
                .Single(reading => reading.Quantity == PrinterSensorQuantity.TargetTemperature).Value.Number.Should().Be(40);

            (await client.TurnOffHeatersAsync(root, null)).Should().BeTrue();
            PrinterCompositeStatus cooled = await client.GetCompositeStatusAsync(root, null);
            Assert.NotNull(cooled.Sensors);
            cooled.Sensors.Where(sensor => sensor.Kind is PrinterSensorKind.Nozzle or PrinterSensorKind.Bed or PrinterSensorKind.Chamber)
                .SelectMany(sensor => sensor.Readings.Where(reading => reading.Quantity == PrinterSensorQuantity.TargetTemperature))
                .Should().NotBeEmpty().And.OnlyContain(reading => reading.Value.Number == 0);
        }
        finally
        {
            using HttpResponseMessage restore = await http.PostAsync("/__emulator/printer/mmu", TestRequests.Json("""{"mode":"None"}"""));
            restore.EnsureSuccessStatusCode();
            await ResetAsync(http);
        }
    }

    public static TheoryData<string, string, string> SensorCases => new()
    {
        { "temperature_sensor endstop", """{"temperature":23.4,"measured_min_temp":23.4,"measured_max_temp":23.4}""", """{"temperature":32.5}""" },
        { "temperature_fan chamber_heater", """{"temperature":24,"target":0,"speed":0,"rpm":0}""", """{"temperature":35,"speed":0.7,"rpm":2100}""" },
        { "heater_generic chamber", """{"temperature":24,"target":0,"power":0}""", """{"temperature":45,"power":0.6}""" },
        { "heater_fan hotend", """{"speed":0,"rpm":null}""", """{"speed":1}""" },
        { "controller_fan electronics", """{"speed":0,"rpm":null}""", """{"speed":0.4}""" },
        { "fan_generic auxiliary", """{"speed":0,"rpm":null}""", """{"speed":0.5}""" },
        { "filament_switch_sensor runout", """{"enabled":true,"filament_detected":true}""", """{"filament_detected":false}""" },
        { "filament_motion_sensor encoder", """{"enabled":true,"filament_detected":true}""", """{"filament_detected":false}""" },
    };

    [Theory]
    [MemberData(nameof(SensorCases))]
    public async Task ObjectsQuery_StandardObject_ReportsExactInitialFieldsAndTypes(string name, string expected, string unusedUpdate)
    {
        _ = unusedUpdate;
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        using JsonDocument listed = JsonDocument.Parse(await client.GetStringAsync("/printer/objects/list"));
        listed.RootElement.GetProperty("result").GetProperty("objects").EnumerateArray()
            .Select(item => item.GetString()).Should().Contain(name);

        using JsonDocument rest = JsonDocument.Parse(await client.GetStringAsync($"/printer/objects/query?{Uri.EscapeDataString(name)}"));
        JsonElement restStatus = rest.RootElement.GetProperty("result").GetProperty("status").GetProperty(name);
        AssertJson(expected, restStatus);

        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, Request("printer.objects.query", name));
        using JsonDocument rpc = await ReceiveAsync(socket);
        AssertJson(expected, rpc.RootElement.GetProperty("result").GetProperty("status").GetProperty(name));
    }

    [Theory]
    [MemberData(nameof(SensorCases))]
    public async Task ObjectsSubscribe_InjectedReading_EmitsChangedFieldsOnceAtVirtualTime(string name, string initial, string update)
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, Request("printer.objects.subscribe", name));
        using JsonDocument subscribed = await ReceiveAsync(socket);
        AssertJson(initial, subscribed.RootElement.GetProperty("result").GetProperty("status").GetProperty(name));
        double initialTime = subscribed.RootElement.GetProperty("result").GetProperty("eventtime").GetDouble();

        using HttpResponseMessage advance = await client.PostAsync("/__emulator/time/advance", TestRequests.Json("""{"seconds":5}"""));
        advance.EnsureSuccessStatusCode();
        using HttpResponseMessage changed = await InjectAsync(client, name, update);
        changed.EnsureSuccessStatusCode();
        using JsonDocument notification = await ReceiveAsync(socket);
        notification.RootElement.GetProperty("method").GetString().Should().Be("notify_status_update");
        JsonElement parameters = notification.RootElement.GetProperty("params");
        parameters[1].GetDouble().Should().Be(initialTime + 5);
        JsonElement delta = parameters[0];
        delta.EnumerateObject().Select(property => property.Name).Should().Equal(name);
        string expectedDelta = name == "temperature_sensor endstop"
            ? """{"temperature":32.5,"measured_max_temp":32.5}"""
            : update;
        AssertJson(expectedDelta, delta.GetProperty(name));

        using HttpResponseMessage unchanged = await InjectAsync(client, name, update);
        unchanged.EnsureSuccessStatusCode();
        // A query on the same connection is a deterministic barrier: a duplicate
        // notification would arrive before this response and fail the id assertion.
        await SendAsync(socket, Request("printer.objects.query", name));
        using JsonDocument queried = await ReceiveAsync(socket);
        queried.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        JsonElement current = queried.RootElement.GetProperty("result").GetProperty("status").GetProperty(name);
        using JsonDocument expectedUpdates = JsonDocument.Parse(update);
        foreach (JsonProperty property in expectedUpdates.RootElement.EnumerateObject())
        {
            AssertJson(property.Value.GetRawText(), current.GetProperty(property.Name));
        }
    }

    [Theory]
    [InlineData("SET_FAN_SPEED FAN=auxiliary SPEED=0.35", "fan_generic auxiliary", "speed", "0.35")]
    [InlineData("SET_FAN_SPEED FAN=auxiliary SPEED=1.1", "fan_generic auxiliary", "speed", "1")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=chamber TARGET=50", "heater_generic chamber", "target", "50")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=extruder TARGET=200", "extruder", "target", "200")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=heater_bed TARGET=60", "heater_bed", "target", "60")]
    [InlineData("SET_HEATER_TEMPERATURE heater=extruder target=195", "extruder", "target", "195")]
    [InlineData("SET_TEMPERATURE_FAN_TARGET temperature_fan=chamber_heater target=40", "temperature_fan chamber_heater", "target", "40")]
    [InlineData("SET_FILAMENT_SENSOR SENSOR=runout ENABLE=0", "filament_switch_sensor runout", "enabled", "false")]
    [InlineData("SET_FILAMENT_SENSOR SENSOR=encoder ENABLE=0", "filament_motion_sensor encoder", "enabled", "false")]
    public async Task SendGcode_SupportedSensorCommand_BroadcastsAndResets(string command, string name, string field, string expected)
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "printer.objects.subscribe",
            id = 1,
            @params = new { objects = new Dictionary<string, string[]> { [name] = [field] } },
        }));
        using JsonDocument initial = await ReceiveAsync(socket);
        JsonElement original = initial.RootElement.GetProperty("result").GetProperty("status").GetProperty(name).GetProperty(field);

        using HttpResponseMessage result = await client.PostAsync("/printer/gcode/script", TestRequests.Json(JsonSerializer.Serialize(new { script = command })));
        result.EnsureSuccessStatusCode();
        using JsonDocument changed = await ReceiveAsync(socket);
        AssertJson($$"""{"{{field}}":{{expected}}}""", changed.RootElement.GetProperty("params")[0].GetProperty(name));

        await ResetAsync(client);
        using JsonDocument reset = await ReceiveAsync(socket);
        AssertJson(original.GetRawText(), reset.RootElement.GetProperty("params")[0].GetProperty(name).GetProperty(field));
    }

    [Theory]
    [InlineData("temperature_sensor endstop", """{"temperature":35,"target":50}""")]
    [InlineData("heater_generic chamber", """{"temperature":45,"power":1.1}""")]
    [InlineData("heater_fan hotend", """{"speed":-0.1}""")]
    [InlineData("filament_switch_sensor runout", """{"filament_detected":0}""")]
    [InlineData("filament_motion_sensor encoder", """{"enabled":"false"}""")]
    [InlineData("temperature_fan chamber_heater", """{"rpm":-1}""")]
    [InlineData("temperature_fan chamber_heater", """{"rpm":null}""")]
    [InlineData("heater_fan hotend", """{"rpm":2100}""")]
    [InlineData("controller_fan electronics", """{"rpm":2100}""")]
    [InlineData("fan_generic auxiliary", """{"rpm":2100}""")]
    [InlineData("unknown sensor", """{"temperature":25}""")]
    public async Task UpdateSensor_InvalidReading_RejectsEntireMutation(string name, string fields)
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        string before = await client.GetStringAsync($"/printer/objects/query?{Uri.EscapeDataString(name)}");
        using HttpResponseMessage result = await InjectAsync(client, name, fields);
        result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await result.Content.ReadAsStringAsync()).Should().Contain("message");
        (await client.GetStringAsync($"/printer/objects/query?{Uri.EscapeDataString(name)}")).Should().Be(before);
    }

    [Theory]
    [InlineData("SET_FAN_SPEED FAN=hotend SPEED=0.5")]
    [InlineData("SET_FAN_SPEED FAN=auxiliary SPEED=-0.1")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=chamber TARGET=101")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=extruder TARGET=NaN")]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=heater_bed TARGET=-1")]
    [InlineData("SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=NaN")]
    [InlineData("SET_FILAMENT_SENSOR SENSOR=runout ENABLE=2")]
    public async Task SendGcode_InvalidSensorCommand_ReturnsExplicitError(string command)
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        using HttpResponseMessage response = await client.PostAsync("/printer/gcode/script", TestRequests.Json(JsonSerializer.Serialize(new { script = command })));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("NOTHEATER=extruder TARGET=200")]
    [InlineData("HEATER=extruder BADTARGET=200")]
    [InlineData("HEATER=heater_bed BADTARGET=60")]
    [InlineData("NOTHEATER=heater_bed TARGET=60")]
    [InlineData("HEATER=extruder TARGET=200 target=210")]
    [InlineData("HEATER=heater_bed heater=extruder TARGET=60")]
    [InlineData("HEATER=extruder TARGET=200 UNKNOWN=1")]
    [InlineData("HEATER=heater_bed TARGET=60 UNKNOWN=1")]
    public async Task SendGcode_MalformedHeaterParameters_RejectsWithoutMutatingAnyHeater(string parameters)
    {
        using HttpClient client = factory.CreateClient();
        await ResetAsync(client);
        const string query = "/printer/objects/query?extruder&heater_bed&heater_generic%20chamber";
        string before = await client.GetStringAsync(query);
        using HttpResponseMessage response = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json(JsonSerializer.Serialize(new { script = $"SET_HEATER_TEMPERATURE {parameters}" })));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("message");
        (await client.GetStringAsync(query)).Should().Be(before);
    }

    [Fact]
    public void BuildObjectsSnapshot_IndependentPrintersAndClock_DoNotChangeOrShareSensorReadings()
    {
        var first = new PrinterAggregate { Id = "first", Name = "First" };
        var second = new PrinterAggregate { Id = "second", Name = "Second" };
        using JsonDocument update = JsonDocument.Parse("""{"temperature":12}""");
        first.UpdateSensor("temperature_sensor endstop", update.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));
        first.AdvanceTime(TimeSpan.FromSeconds(100));
        var status = (Dictionary<string, object?>)first.BuildObjectsSnapshot()["temperature_sensor endstop"];
        status["temperature"].Should().Be(12.0);
        status["measured_min_temp"].Should().Be(12.0);
        status["measured_max_temp"].Should().Be(23.4);
        status["temperature"] = 999.0;
        ((Dictionary<string, object?>)first.BuildObjectsSnapshot()["temperature_sensor endstop"])["temperature"].Should().Be(12.0);
        ((Dictionary<string, object?>)second.BuildObjectsSnapshot()["temperature_sensor endstop"])["temperature"].Should().Be(23.4);
        first.SendGcode("FIRMWARE_RESTART");
        ((Dictionary<string, object?>)first.BuildObjectsSnapshot()["temperature_sensor endstop"])["temperature"].Should().Be(23.4);
    }

    [Theory]
    [InlineData("0", 23.4)]
    [InlineData("-0.0", 23.4)]
    [InlineData("0.000000001", 0.000000001)]
    [InlineData("-0.000000001", -0.000000001)]
    public void UpdateSensor_ZeroSentinelAndNearZeroReadings_PreserveKlipperExtrema(string reading, double expectedMinimum)
    {
        var printer = new PrinterAggregate { Id = "sensor", Name = "Sensor" };
        using JsonDocument update = JsonDocument.Parse($$"""{"temperature":{{reading}}}""");
        printer.UpdateSensor("temperature_sensor endstop", update.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));

        var status = (Dictionary<string, object?>)printer.BuildObjectsSnapshot()["temperature_sensor endstop"];
        status["temperature"].Should().Be(double.Parse(reading, System.Globalization.CultureInfo.InvariantCulture));
        status["measured_min_temp"].Should().Be(expectedMinimum);
        status["measured_max_temp"].Should().Be(23.4);
    }

    [Theory]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=chamber TARGET=0", "heater_generic chamber", 0)]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=chamber TARGET=100", "heater_generic chamber", 100)]
    [InlineData("SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=0", "temperature_fan chamber_heater", 0)]
    [InlineData("SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=-255", "temperature_fan chamber_heater", -255)]
    [InlineData("SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=256", "temperature_fan chamber_heater", 256)]
    public void SendGcode_ZeroAndConfiguredTargetBounds_AcceptsTarget(string command, string name, double target)
    {
        var printer = new PrinterAggregate { Id = "target", Name = "Target" };
        printer.SendGcode(command);

        var status = (Dictionary<string, object?>)printer.BuildObjectsSnapshot()[name];
        status["target"].Should().Be(target);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0.0", true)]
    [InlineData("0.000000001", false)]
    [InlineData("-0.000000001", false)]
    public void SendGcode_PositiveMinimumHeater_OnlyExactZeroIsOff(string target, bool accepted)
    {
        var printer = new PrinterAggregate { Id = "target", Name = "Target" };
        printer.SetMmuMode(MmuMode.SnapmakerU1);
        printer.SendGcode("SET_HEATER_TEMPERATURE HEATER=extruder1 TARGET=210");
        Action write = () => printer.SendGcode($"SET_HEATER_TEMPERATURE HEATER=extruder1 TARGET={target}");

        if (accepted)
        {
            write.Should().NotThrow();
        }
        else
        {
            write.Should().Throw<GcodeParameterException>();
        }

        var status = (Dictionary<string, object?>)printer.BuildObjectsSnapshot()["extruder1"];
        status["target"].Should().Be(accepted ? 0d : 210d);
    }

    [Fact]
    public async Task ObjectsQuery_ConfiguredTargets_ExposeFiniteBoundsAndHeaterInventory()
    {
        using HttpClient client = factory.CreateClient();
        using JsonDocument response = JsonDocument.Parse(await client.GetStringAsync("/printer/objects/query?configfile&heaters"));
        JsonElement status = response.RootElement.GetProperty("result").GetProperty("status");
        JsonElement settings = status.GetProperty("configfile").GetProperty("settings");
        settings.GetProperty("heater_generic chamber").GetProperty("max_temp").GetDouble().Should().Be(100);
        settings.GetProperty("temperature_fan chamber_heater").GetProperty("max_temp").GetDouble().Should().Be(256);
        status.GetProperty("heaters").GetProperty("available_heaters").EnumerateArray()
            .Select(item => item.GetString()).Should().Contain("heater_generic chamber");
    }

    private async Task<WebSocket> ConnectAsync() =>
        await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);

    private static string Request(string method, string name) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        method,
        id = 1,
        @params = new { objects = new Dictionary<string, object?> { [name] = null } },
    });

    private static Task<HttpResponseMessage> InjectAsync(HttpClient client, string name, string fields) =>
        client.PostAsync("/__emulator/printer/sensor", TestRequests.Json($$"""{"objectName":{{JsonSerializer.Serialize(name)}},"fields":{{fields}}}"""));

    private static async Task ResetAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/__emulator/printer/reset", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SendAsync(WebSocket socket, string payload) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[32768];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }

    private static void AssertJson(string expected, JsonElement actual)
    {
        using JsonDocument document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual), $"Expected {expected}, received {actual.GetRawText()}");
    }
}
