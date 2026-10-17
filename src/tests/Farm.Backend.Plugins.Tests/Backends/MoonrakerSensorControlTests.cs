using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>Issue #3407: targeted Moonraker sensor/fan writes re-validate against the live printer.</summary>
public sealed class MoonrakerSensorControlTests
{
    private const string BaseUrl = "http://sensor-fixture.invalid";
    private static readonly PrinterCredential Credential = PrinterCredential.FromApiKey("fixture-key");

    private static MoonrakerClient CreateClient(HttpClient http) =>
        new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

    [Fact]
    public void MoonrakerClient_ImplementsSensorControlContract()
    {
        typeof(ISupportsSensorControl).IsAssignableFrom(typeof(MoonrakerClient)).Should().BeTrue();
    }

    [Theory]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 40, "M106 S102")]
    [InlineData("fan_generic aux", PrinterSensorQuantity.FanSpeed, 25, "SET_FAN_SPEED FAN=aux SPEED=0.25")]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 50, "SET_TEMPERATURE_FAN_TARGET temperature_fan=exhaust_fan target=50")]
    [InlineData("heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, 55.5, "SET_HEATER_TEMPERATURE HEATER=chamber_heater TARGET=55.5")]
    public async Task SetSensorValueAsync_SupportedWrite_SendsExactCommandWithApiKey(
        string id, PrinterSensorQuantity quantity, double value, string expected)
    {
        using var handler = new SensorHandler();
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(BaseUrl, id, quantity, value, Credential);

        accepted.Should().BeTrue();
        handler.Scripts.Should().Equal(expected);
        handler.ApiKeys.Should().OnlyContain(key => key == "fixture-key");
        handler.Requests.Should().StartWith("GET /printer/objects/list");
    }

    [Fact]
    public async Task SetSensorValueAsync_TemperatureTarget_ReadsLiveConfigBounds()
    {
        using var handler = new SensorHandler { HeaterMaxTemp = 50 };
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(
            BaseUrl, "heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, 55.5, Credential);

        accepted.Should().BeFalse();
        handler.Requests.Should().Equal("GET /printer/objects/list", "GET /printer/objects/query?configfile=settings");
        handler.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetSensorValueAsync_ObjectNoLongerListed_SendsNoGcode()
    {
        using var handler = new SensorHandler { Objects = ["fan", "extruder"] };
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(BaseUrl, "fan_generic aux", PrinterSensorQuantity.FanSpeed, 50, Credential);

        accepted.Should().BeFalse();
        handler.Scripts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("fan_generic aux\nM112")]
    [InlineData("fan_generic aux SPEED=1")]
    [InlineData("")]
    public async Task SetSensorValueAsync_UnsafeId_MakesNoRequests(string id)
    {
        using var handler = new SensorHandler();
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(BaseUrl, id, PrinterSensorQuantity.FanSpeed, 50, Credential);

        accepted.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("heater_fan hotend_fan", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("extruder", PrinterSensorQuantity.TargetTemperature, 200)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 150)]
    public async Task SetSensorValueAsync_ReadOnlyOrOutOfRange_SendsNoGcode(string id, PrinterSensorQuantity quantity, double value)
    {
        using var handler = new SensorHandler();
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(BaseUrl, id, quantity, value, Credential);

        accepted.Should().BeFalse();
        handler.Scripts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, """{"result":{"objects":"nope"}}""")]
    [InlineData(HttpStatusCode.OK, "<html>proxy login</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task SetSensorValueAsync_ObjectListUnavailableOrMalformed_SendsNoGcode(HttpStatusCode status, string body)
    {
        using var handler = new SensorHandler { ObjectsStatus = status, ObjectsBody = body };
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(BaseUrl, "fan", PrinterSensorQuantity.FanSpeed, 50, Credential);

        accepted.Should().BeFalse();
        handler.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetSensorValueAsync_ConfigQueryFails_SendsNoGcode()
    {
        using var handler = new SensorHandler { SettingsStatus = HttpStatusCode.InternalServerError };
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(
            BaseUrl, "temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 40, Credential);

        accepted.Should().BeFalse();
        handler.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetSensorValueAsync_ConfigQueryNotJson_SendsNoGcode()
    {
        using var handler = new SensorHandler { SettingsBody = "<html>gateway error</html>" };
        using var http = new HttpClient(handler);

        bool accepted = await CreateClient(http).SetSensorValueAsync(
            BaseUrl, "temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 40, Credential);

        accepted.Should().BeFalse();
        handler.Scripts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "M106 S0")]
    [InlineData(50, "M106 S128")]
    [InlineData(100, "M106 S255")]
    public async Task SetFanSpeedAsync_LegacyPartFan_UnchangedCommand(int percent, string expected)
    {
        using var handler = new SensorHandler();
        using var http = new HttpClient(handler);

        (await CreateClient(http).SetFanSpeedAsync(BaseUrl, percent, Credential)).Should().BeTrue();

        handler.Scripts.Should().Equal(expected);
        handler.Requests.Should().Equal("POST /printer/gcode/script");
    }

    private sealed class SensorHandler : HttpMessageHandler
    {
        public string[] Objects { get; init; } =
            ["fan", "extruder", "heater_fan hotend_fan", "fan_generic aux", "temperature_fan exhaust_fan", "heater_generic chamber_heater"];

        public HttpStatusCode ObjectsStatus { get; init; } = HttpStatusCode.OK;

        public string? ObjectsBody { get; init; }

        public HttpStatusCode SettingsStatus { get; init; } = HttpStatusCode.OK;

        public string? SettingsBody { get; init; }

        public double HeaterMaxTemp { get; init; } = 80;

        public List<string> Requests { get; } = [];

        public List<string> Scripts { get; } = [];

        public List<string?> ApiKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add($"{request.Method} {pathAndQuery}");
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? keys.Single() : null);

            switch (pathAndQuery)
            {
                case "/printer/objects/list":
                    return Json(ObjectsStatus, ObjectsBody ?? JsonSerializer.Serialize(new { result = new { objects = Objects } }));
                case "/printer/objects/query?configfile=settings":
                    string settings = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $$$$$$"""
                        {"result":{"status":{"configfile":{"settings":{
                          "temperature_fan exhaust_fan":{"min_temp":0,"max_temp":70},
                          "heater_generic chamber_heater":{"min_temp":0,"max_temp":{{{{{{HeaterMaxTemp}}}}}}}
                        }}}}}
                        """);
                    return Json(SettingsStatus, SettingsBody ?? settings);
                case "/printer/gcode/script":
                    using (JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        Scripts.Add(doc.RootElement.GetProperty("script").GetString()!);
                    }

                    return Json(HttpStatusCode.OK, """{"result":"ok"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
