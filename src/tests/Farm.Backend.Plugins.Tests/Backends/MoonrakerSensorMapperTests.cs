using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>Issue #3407: Klipper object → backend-neutral sensor mapping and the supported writes.</summary>
public sealed class MoonrakerSensorMapperTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] AllObjects =
    [
        "configfile", "webhooks", "toolhead", "print_stats", "system_stats", "gcode_macro PARK",
        "extruder", "extruder1", "heater_bed", "heater_generic chamber_heater",
        "temperature_sensor chamber", "temperature_sensor mcu_temp", "temperature_sensor raspberry_pi",
        "temperature_sensor ambient", "temperature_fan exhaust_fan", "fan", "heater_fan hotend_fan",
        "controller_fan board", "fan_generic aux", "filament_switch_sensor runout",
        "filament_motion_sensor encoder", "sht3x enclosure",
    ];

    private const string FullStatus =
        """
        {
          "configfile": { "settings": {
            "temperature_sensor mcu_temp": { "sensor_type": "temperature_mcu" },
            "temperature_sensor raspberry_pi": { "sensor_type": "temperature_host" },
            "temperature_sensor chamber": { "sensor_type": "BME280" },
            "temperature_fan exhaust_fan": { "sensor_type": "EPCOS 100K B57560G104F", "min_temp": 0, "max_temp": 70 },
            "heater_generic chamber_heater": { "sensor_type": "Generic 3950", "min_temp": -10, "max_temp": 80 }
          } },
          "webhooks": { "state": "ready" },
          "toolhead": { "homed_axes": "xyz", "position": [1, 2, 3, 0] },
          "print_stats": { "state": "standby" },
          "system_stats": { "sysload": 0.3, "cputime": 12.5 },
          "gcode_macro PARK": {},
          "extruder": { "temperature": 210.5, "target": 215, "power": 0.5, "pressure_advance": 0.04 },
          "extruder1": { "temperature": 25, "target": 0, "power": 0 },
          "heater_bed": { "temperature": 60.2, "target": 60, "power": 0.25 },
          "heater_generic chamber_heater": { "temperature": 35, "target": 40, "power": 1 },
          "temperature_sensor chamber": { "temperature": 34.5, "humidity": 40.5, "pressure": 1012.3, "gas": 52000 },
          "temperature_sensor mcu_temp": { "temperature": 45, "measured_min_temp": 30, "measured_max_temp": 50 },
          "temperature_sensor raspberry_pi": { "temperature": 52 },
          "temperature_sensor ambient": { "temperature": 22 },
          "temperature_fan exhaust_fan": { "temperature": 38, "target": 45, "speed": 0.4, "rpm": 1200 },
          "fan": { "speed": 0.5, "rpm": null },
          "heater_fan hotend_fan": { "speed": 1, "rpm": 6000 },
          "controller_fan board": { "speed": 0.3 },
          "fan_generic aux": { "speed": 0.75 },
          "filament_switch_sensor runout": { "filament_detected": true, "enabled": false },
          "filament_motion_sensor encoder": { "filament_detected": false, "enabled": true },
          "sht3x enclosure": { "temperature": 30, "humidity": 55 }
        }
        """;

    private static MoonrakerSensorTracker DiscoverAndApply(string status = FullStatus)
    {
        var tracker = new MoonrakerSensorTracker();
        tracker.Discover(AllObjects);
        using JsonDocument doc = JsonDocument.Parse(status);
        tracker.Apply(doc.RootElement, T0).Should().BeTrue();
        return tracker;
    }

    private static void Apply(MoonrakerSensorTracker tracker, string status, DateTime now)
    {
        using JsonDocument doc = JsonDocument.Parse(status);
        _ = tracker.Apply(doc.RootElement, now);
    }

    private static Dictionary<string, PrinterSensorDto> ById(MoonrakerSensorTracker tracker) =>
        tracker.Snapshot().ToDictionary(sensor => sensor.Id, StringComparer.Ordinal);

    private static IEnumerable<(PrinterSensorQuantity Quantity, object? Value, PrinterSensorUnit Unit)> Readings(PrinterSensorDto sensor) =>
        sensor.Readings.Select(r => (r.Quantity, r.Value.IsBoolean ? (object?)r.Value.Boolean : r.Value.Number, r.Unit));

    [Fact]
    public void Snapshot_FullKlipperStatus_MapsOnlySensorObjectsWithStableIds()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        tracker.Snapshot().Select(s => s.Id).Should().BeEquivalentTo(
            "extruder", "extruder1", "heater_bed", "heater_generic chamber_heater",
            "temperature_sensor chamber", "temperature_sensor mcu_temp", "temperature_sensor raspberry_pi",
            "temperature_sensor ambient", "temperature_fan exhaust_fan", "fan", "heater_fan hotend_fan",
            "controller_fan board", "fan_generic aux", "filament_switch_sensor runout",
            "filament_motion_sensor encoder", "sht3x enclosure");
        tracker.Snapshot().Should().OnlyContain(s => s.Available && s.LastUpdated == T0);
    }

    [Theory]
    [InlineData("extruder", PrinterSensorKind.Nozzle, null, "Extruder")]
    [InlineData("extruder1", PrinterSensorKind.Nozzle, null, "Extruder 1")]
    [InlineData("heater_bed", PrinterSensorKind.Bed, null, "Bed")]
    [InlineData("heater_generic chamber_heater", PrinterSensorKind.Chamber, null, "chamber_heater")]
    [InlineData("temperature_sensor chamber", PrinterSensorKind.Chamber, null, "chamber")]
    [InlineData("temperature_sensor mcu_temp", PrinterSensorKind.Mcu, null, "mcu_temp")]
    [InlineData("temperature_sensor raspberry_pi", PrinterSensorKind.Host, null, "raspberry_pi")]
    [InlineData("temperature_sensor ambient", PrinterSensorKind.Ambient, null, "ambient")]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorKind.Fan, PrinterFanRole.Chamber, "exhaust_fan")]
    [InlineData("fan", PrinterSensorKind.Fan, PrinterFanRole.PartCooling, "Part cooling fan")]
    [InlineData("heater_fan hotend_fan", PrinterSensorKind.Fan, PrinterFanRole.Hotend, "hotend_fan")]
    [InlineData("controller_fan board", PrinterSensorKind.Fan, PrinterFanRole.Controller, "board")]
    [InlineData("fan_generic aux", PrinterSensorKind.Fan, PrinterFanRole.Auxiliary, "aux")]
    [InlineData("filament_switch_sensor runout", PrinterSensorKind.FilamentRunout, null, "runout")]
    [InlineData("filament_motion_sensor encoder", PrinterSensorKind.FilamentRunout, null, "encoder")]
    [InlineData("sht3x enclosure", PrinterSensorKind.Chamber, null, "enclosure")]
    public void Snapshot_KlipperObject_ClassifiesKindRoleAndDisplayName(
        string id, PrinterSensorKind kind, PrinterFanRole? role, string displayName)
    {
        PrinterSensorDto sensor = ById(DiscoverAndApply())[id];

        sensor.Kind.Should().Be(kind);
        sensor.FanRole.Should().Be(role);
        sensor.DisplayName.Should().Be(displayName);
    }

    [Fact]
    public void Snapshot_HeatersAndFans_MapFieldsToScaledReadings()
    {
        Dictionary<string, PrinterSensorDto> sensors = ById(DiscoverAndApply());

        Readings(sensors["extruder"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 210.5, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.TargetTemperature, 215d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Power, 50d, PrinterSensorUnit.Percent));
        Readings(sensors["heater_generic chamber_heater"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 35d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.TargetTemperature, 40d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Power, 100d, PrinterSensorUnit.Percent));
        Readings(sensors["temperature_fan exhaust_fan"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 38d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.TargetTemperature, 45d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.FanSpeed, 40d, PrinterSensorUnit.Percent),
            (PrinterSensorQuantity.FanRpm, 1200d, PrinterSensorUnit.Rpm));
        Readings(sensors["fan"]).Should().Equal((PrinterSensorQuantity.FanSpeed, 50d, PrinterSensorUnit.Percent));
        Readings(sensors["heater_fan hotend_fan"]).Should().Equal(
            (PrinterSensorQuantity.FanSpeed, 100d, PrinterSensorUnit.Percent),
            (PrinterSensorQuantity.FanRpm, 6000d, PrinterSensorUnit.Rpm));
        Readings(sensors["controller_fan board"]).Should().Equal((PrinterSensorQuantity.FanSpeed, 30d, PrinterSensorUnit.Percent));
    }

    [Fact]
    public void Snapshot_EnvironmentalAndFilamentSensors_MapEveryReportedQuantity()
    {
        Dictionary<string, PrinterSensorDto> sensors = ById(DiscoverAndApply());

        Readings(sensors["temperature_sensor chamber"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 34.5, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Humidity, 40.5, PrinterSensorUnit.Percent),
            (PrinterSensorQuantity.Pressure, 1012.3, PrinterSensorUnit.HectoPascal),
            (PrinterSensorQuantity.Gas, 52000d, PrinterSensorUnit.Ohm));
        Readings(sensors["temperature_sensor mcu_temp"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 45d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.MeasuredMinTemperature, 30d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.MeasuredMaxTemperature, 50d, PrinterSensorUnit.Celsius));
        Readings(sensors["sht3x enclosure"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 30d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Humidity, 55d, PrinterSensorUnit.Percent));
        Readings(sensors["filament_switch_sensor runout"]).Should().Equal(
            (PrinterSensorQuantity.FilamentDetected, true, PrinterSensorUnit.None),
            (PrinterSensorQuantity.Enabled, false, PrinterSensorUnit.None));
        Readings(sensors["filament_motion_sensor encoder"]).Should().Equal(
            (PrinterSensorQuantity.FilamentDetected, false, PrinterSensorUnit.None),
            (PrinterSensorQuantity.Enabled, true, PrinterSensorUnit.None));
    }

    [Fact]
    public void Snapshot_WritableDescriptors_OnlyForSupportedControlsWithConfiguredBounds()
    {
        Dictionary<string, PrinterSensorDto> sensors = ById(DiscoverAndApply());

        sensors["fan"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.FanSpeed, 0, 100, 1, PrinterSensorUnit.Percent));
        sensors["fan_generic aux"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.FanSpeed, 0, 100, 1, PrinterSensorUnit.Percent));
        sensors["temperature_fan exhaust_fan"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 0, 70, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        sensors["heater_generic chamber_heater"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 0, 80, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        foreach (string id in new[] { "filament_switch_sensor runout", "filament_motion_sensor encoder" })
        {
            sensors[id].Writable.Should().Equal(
                new PrinterSensorWritableDto(PrinterSensorQuantity.Enabled, 0, 1, 1, PrinterSensorUnit.None));
        }

        sensors.Where(pair => pair.Key is not ("fan" or "fan_generic aux" or "temperature_fan exhaust_fan" or "heater_generic chamber_heater" or "filament_switch_sensor runout" or "filament_motion_sensor encoder"))
            .Should().OnlyContain(pair => pair.Value.Writable.Count == 0);
    }

    [Fact]
    public void Snapshot_HeaterGenericWithoutConfiguredBounds_IsReadOnly()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply(FullStatus.Replace("\"min_temp\": -10, ", string.Empty, StringComparison.Ordinal));

        ById(tracker)["heater_generic chamber_heater"].Writable.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_RecordedVendorKlipperPayload_MapsReadingsAndRestrictsTargets()
    {
        // Redacted objects/query + configfile.settings recorded from real Klipper hardware.
        const string status = """
            {
              "configfile": {"settings": {
                "temperature_sensor gd32": {"sensor_type":"temperature_mcu","min_temp":-273.15,"max_temp":99999999.9},
                "heater_generic chamber": {"min_temp":-100,"max_temp":70,"target_min_temp":10,"target_max_temp":65},
                "heater_generic heater_box1": {"min_temp":-100,"max_temp":100,"target_min_temp":0,"target_max_temp":90}
              }},
              "temperature_sensor GD32": {"temperature":30.72},
              "temperature_sensor heater_temp_a_box1": {"temperature":25.5},
              "aht20_f heater_box1": {"temperature":24.26,"humidity":34},
              "heater_generic chamber": {"temperature":25.62,"target":0,"power":0},
              "heater_generic heater_box1": {"temperature":24.26,"target":0,"power":0},
              "chamber_fan chamber_fan": {"speed":0,"rpm":null},
              "box_heater_fan heater_fan_a_box1": {"speed":0,"rpm":null},
              "heater_fan hotend_fan": {"speed":0,"rpm":null},
              "fan_generic chamber_circulation_fan": {"speed":0,"rpm":null},
              "filament_switch_sensor fila": {"filament_detected":true,"enabled":false}
            }
            """;
        using JsonDocument document = JsonDocument.Parse(status);
        var tracker = new MoonrakerSensorTracker();
        tracker.Discover(document.RootElement.EnumerateObject().Select(property => property.Name));
        tracker.Apply(document.RootElement, T0).Should().BeTrue();
        Dictionary<string, PrinterSensorDto> sensors = ById(tracker);

        sensors.Should().HaveCount(10);
        sensors["temperature_sensor GD32"].Kind.Should().Be(PrinterSensorKind.Mcu);
        Readings(sensors["temperature_sensor heater_temp_a_box1"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 25.5, PrinterSensorUnit.Celsius));
        Readings(sensors["aht20_f heater_box1"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 24.26, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Humidity, 34d, PrinterSensorUnit.Percent));
        sensors["heater_generic chamber"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 10, 65, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        sensors["heater_generic heater_box1"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 0, 90, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        foreach (string id in new[] { "chamber_fan chamber_fan", "box_heater_fan heater_fan_a_box1", "heater_fan hotend_fan" })
        {
            sensors[id].Kind.Should().Be(PrinterSensorKind.Fan);
            sensors[id].Writable.Should().BeEmpty();
            Readings(sensors[id]).Should().Equal((PrinterSensorQuantity.FanSpeed, 0d, PrinterSensorUnit.Percent));
            MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.FanSpeed, 20,
                tracker.DiscoveredObjects!, MoonrakerSensorMapper.ReadSettings(document.RootElement.GetProperty("configfile").GetProperty("settings")))
                .Should().BeNull();
        }

        sensors["chamber_fan chamber_fan"].FanRole.Should().Be(PrinterFanRole.Chamber);
        Readings(sensors["filament_switch_sensor fila"]).Should().Equal(
            (PrinterSensorQuantity.FilamentDetected, true, PrinterSensorUnit.None),
            (PrinterSensorQuantity.Enabled, false, PrinterSensorUnit.None));
        Readings(sensors["fan_generic chamber_circulation_fan"]).Should().Equal(
            (PrinterSensorQuantity.FanSpeed, 0d, PrinterSensorUnit.Percent));
        sensors["fan_generic chamber_circulation_fan"].Writable.Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.FanSpeed, 0, 100, 1, PrinterSensorUnit.Percent));
    }

    [Theory]
    [InlineData("""{"min_temp":-100,"max_temp":70,"target_min_temp":10,"target_max_temp":65}""", 10, 65)]
    [InlineData("""{"min_temp":5,"max_temp":70,"target_min_temp":0,"target_max_temp":100}""", 5, 70)]
    [InlineData("""{"min_temp":-100,"max_temp":70}""", 0, 70)]
    public void GetWritable_TargetBounds_IntersectSensorLimitsAndRejectOutsideRange(string section, double min, double max)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"heater_generic chamber":{{section}}}""");
        Dictionary<string, MoonrakerSectionSettings> settings = MoonrakerSensorMapper.ReadSettings(document.RootElement);
        IReadOnlySet<string> objects = new HashSet<string>(["heater_generic chamber"]);

        MoonrakerSensorMapper.GetWritable("heater_generic chamber", settings).Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, min, max, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        MoonrakerSensorMapper.TryBuildCommand("heater_generic chamber", PrinterSensorQuantity.TargetTemperature, min - 1, objects, settings)
            .Should().BeNull();
        MoonrakerSensorMapper.TryBuildCommand("heater_generic chamber", PrinterSensorQuantity.TargetTemperature, max + 1, objects, settings)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("""{"min_temp":0,"max_temp":70,"target_min_temp":"bad"}""")]
    [InlineData("""{"min_temp":0,"max_temp":70,"target_max_temp":null}""")]
    [InlineData("""{"min_temp":0,"max_temp":70,"target_min_temp":80}""")]
    [InlineData("""{"target_min_temp":0,"target_max_temp":70}""")]
    public void GetWritable_InvalidTargetBounds_FailsClosed(string section)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"heater_generic chamber":{{section}}}""");

        MoonrakerSensorMapper.GetWritable("heater_generic chamber", MoonrakerSensorMapper.ReadSettings(document.RootElement))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("extruder", 250)]
    [InlineData("extruder1", 260)]
    [InlineData("extruder2", 270)]
    [InlineData("extruder3", 280)]
    [InlineData("heater_bed", 110)]
    public void TryBuildCommand_ConfiguredHeaters_AddressesExactObjectAndEnforcesItsLimits(string id, double max)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, object> { [id] = new { min_temp = 10, max_temp = max } }));
        Dictionary<string, MoonrakerSectionSettings> settings = MoonrakerSensorMapper.ReadSettings(document.RootElement);
        IReadOnlySet<string> objects = new HashSet<string>(["extruder", "extruder1", "extruder2", "extruder3", "heater_bed"]);

        MoonrakerSensorMapper.GetWritable(id, settings).Should().Equal(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 10, max, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.TargetTemperature, 200 > max ? 100 : 200, objects, settings)
            .Should().Be($"SET_HEATER_TEMPERATURE HEATER={id} TARGET={(200 > max ? 100 : 200)}");
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.TargetTemperature, max + 1, objects, settings).Should().BeNull();
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.TargetTemperature, 5, objects, settings).Should().BeNull();
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.TargetTemperature, 0, objects, settings)
            .Should().Be($"SET_HEATER_TEMPERATURE HEATER={id} TARGET=0");
    }

    [Theory]
    [InlineData("filament_switch_sensor runout")]
    [InlineData("filament_motion_sensor runout")]
    public void TryBuildCommand_FilamentEnablement_UsesExactDiscoveredSafeName(string id)
    {
        IReadOnlySet<string> objects = new HashSet<string>([id]);
        Dictionary<string, MoonrakerSectionSettings> settings = new();

        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.Enabled, 1, objects, settings)
            .Should().Be("SET_FILAMENT_SENSOR SENSOR=runout ENABLE=1");
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.Enabled, 0, objects, settings)
            .Should().Be("SET_FILAMENT_SENSOR SENSOR=runout ENABLE=0");
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.Enabled, 0.5, objects, settings).Should().BeNull();
        MoonrakerSensorMapper.TryBuildCommand(id, PrinterSensorQuantity.Enabled, 2, objects, settings).Should().BeNull();
        MoonrakerSensorMapper.TryBuildCommand("filament_switch_sensor missing", PrinterSensorQuantity.Enabled, 1, objects, settings).Should().BeNull();
    }

    [Fact]
    public void Snapshot_RawBme680Driver_PreservesGasAndUnits()
    {
        var tracker = new MoonrakerSensorTracker();
        tracker.Discover(["bme280 ambient"]);
        Apply(tracker, """{"bme280 ambient":{"temperature":24.5,"pressure":1012,"humidity":45,"gas":123456}}""", T0);

        Readings(Assert.Single(tracker.Snapshot())).Should().Equal(
            (PrinterSensorQuantity.Temperature, 24.5, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Humidity, 45d, PrinterSensorUnit.Percent),
            (PrinterSensorQuantity.Pressure, 1012d, PrinterSensorUnit.HectoPascal),
            (PrinterSensorQuantity.Gas, 123456d, PrinterSensorUnit.Ohm));
    }

    [Fact]
    public void Apply_PartialUpdate_MergesFieldsAndKeepsOtherSensors()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();
        DateTime t1 = T0.AddSeconds(5);

        Apply(tracker, """{ "temperature_sensor chamber": { "humidity": 42 }, "fan": { "speed": 0 } }""", t1);

        Dictionary<string, PrinterSensorDto> sensors = ById(tracker);
        Readings(sensors["temperature_sensor chamber"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 34.5, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Humidity, 42d, PrinterSensorUnit.Percent),
            (PrinterSensorQuantity.Pressure, 1012.3, PrinterSensorUnit.HectoPascal),
            (PrinterSensorQuantity.Gas, 52000d, PrinterSensorUnit.Ohm));
        sensors["temperature_sensor chamber"].LastUpdated.Should().Be(t1);
        Readings(sensors["fan"]).Should().Equal((PrinterSensorQuantity.FanSpeed, 0d, PrinterSensorUnit.Percent));
        sensors["extruder"].LastUpdated.Should().Be(T0);
        sensors.Should().HaveCount(16);
    }

    [Fact]
    public void Apply_TargetOnlyUpdate_ChangesOnlyTarget()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        Apply(tracker, """{ "heater_generic chamber_heater": { "target": 0 } }""", T0.AddSeconds(1));

        Readings(ById(tracker)["heater_generic chamber_heater"]).Should().Equal(
            (PrinterSensorQuantity.Temperature, 35d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.TargetTemperature, 0d, PrinterSensorUnit.Celsius),
            (PrinterSensorQuantity.Power, 100d, PrinterSensorUnit.Percent));
    }

    [Fact]
    public void Apply_NullField_RemovesThatReading()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        Apply(tracker, """{ "heater_fan hotend_fan": { "rpm": null } }""", T0.AddSeconds(1));

        Readings(ById(tracker)["heater_fan hotend_fan"]).Should().Equal((PrinterSensorQuantity.FanSpeed, 100d, PrinterSensorUnit.Percent));
    }

    [Fact]
    public void Apply_ObjectNotInDiscoveredSet_IsIgnored()
    {
        var tracker = new MoonrakerSensorTracker();
        tracker.Discover(["extruder"]);

        Apply(tracker, """{ "extruder": { "temperature": 20 }, "fan_generic rogue": { "speed": 1 } }""", T0);

        tracker.Snapshot().Select(s => s.Id).Should().Equal("extruder");
    }

    [Theory]
    [InlineData("""{ "system_stats": { "temperature": 50 } }""")]
    [InlineData("""{ "gcode_macro PARK": { "speed": 1 } }""")]
    [InlineData("""{ "mystery thing": { "temperature": "hot" } }""")]
    [InlineData("""{ "extruder": "not-an-object" }""")]
    [InlineData("""[]""")]
    public void Apply_NonSensorOrMalformedPayload_ProducesNoSensors(string status)
    {
        var tracker = new MoonrakerSensorTracker();
        tracker.Discover(["system_stats", "gcode_macro PARK", "mystery thing", "extruder"]);

        Apply(tracker, status, T0);

        tracker.HasSensors.Should().BeFalse();
    }

    [Fact]
    public void Discover_ObjectRemovedOnRestart_StaysUnavailableWithLastValues()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        tracker.Discover(AllObjects.Where(name => name != "fan_generic aux"));
        Apply(tracker, FullStatus, T0.AddSeconds(10));

        Dictionary<string, PrinterSensorDto> sensors = ById(tracker);
        sensors["fan_generic aux"].Available.Should().BeFalse();
        sensors["fan_generic aux"].LastUpdated.Should().Be(T0);
        Readings(sensors["fan_generic aux"]).Should().Equal((PrinterSensorQuantity.FanSpeed, 75d, PrinterSensorUnit.Percent));
        sensors.Where(pair => pair.Key != "fan_generic aux").Should().OnlyContain(pair => pair.Value.Available);
        tracker.DiscoveredObjects.Should().NotContain("fan_generic aux");
    }

    [Fact]
    public void Discover_ObjectAddedOnRestart_AppearsAfterSubscriptionSnapshot()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        tracker.Discover([.. AllObjects, "temperature_sensor new_probe"]);
        tracker.Snapshot().Should().OnlyContain(s => !s.Available);
        Apply(tracker, FullStatus.Replace("\"sht3x enclosure\"", "\"temperature_sensor new_probe\": { \"temperature\": 27 }, \"sht3x enclosure\"", StringComparison.Ordinal), T0.AddSeconds(10));

        PrinterSensorDto added = ById(tracker)["temperature_sensor new_probe"];
        added.Available.Should().BeTrue();
        Readings(added).Should().Equal((PrinterSensorQuantity.Temperature, 27d, PrinterSensorUnit.Celsius));
    }

    [Fact]
    public void MarkAllUnavailable_Disconnect_KeepsValuesAndReportsChangeOnce()
    {
        MoonrakerSensorTracker tracker = DiscoverAndApply();

        tracker.MarkAllUnavailable().Should().BeTrue();
        tracker.MarkAllUnavailable().Should().BeFalse();

        tracker.Snapshot().Should().HaveCount(16).And.OnlyContain(s => !s.Available && s.Readings.Count > 0);
    }

    [Theory]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 0, "M106 S0")]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 50, "M106 S128")]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 100, "M106 S255")]
    [InlineData("fan_generic aux", PrinterSensorQuantity.FanSpeed, 33, "SET_FAN_SPEED FAN=aux SPEED=0.33")]
    [InlineData("fan_generic aux", PrinterSensorQuantity.FanSpeed, 100, "SET_FAN_SPEED FAN=aux SPEED=1")]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 45.5, "SET_TEMPERATURE_FAN_TARGET temperature_fan=exhaust_fan target=45.5")]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 70, "SET_TEMPERATURE_FAN_TARGET temperature_fan=exhaust_fan target=70")]
    [InlineData("heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, 60, "SET_HEATER_TEMPERATURE HEATER=chamber_heater TARGET=60")]
    [InlineData("heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, 0, "SET_HEATER_TEMPERATURE HEATER=chamber_heater TARGET=0")]
    public void TryBuildCommand_SupportedWrite_ProducesExactKlipperCommand(
        string id, PrinterSensorQuantity quantity, double value, string expected)
    {
        string? command = MoonrakerSensorMapper.TryBuildCommand(id, quantity, value, Discovered, Settings);

        command.Should().Be(expected);
    }

    [Theory]
    [InlineData("fan_generic missing", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("fan_generic a=b", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("fan_generic aux\nM112", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("fan_generic aux extra", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("fan_generic", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("heater_fan hotend_fan", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("controller_fan board", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("extruder", PrinterSensorQuantity.TargetTemperature, 200)]
    [InlineData("heater_bed", PrinterSensorQuantity.TargetTemperature, 60)]
    [InlineData("temperature_sensor chamber", PrinterSensorQuantity.TargetTemperature, 30)]
    [InlineData("filament_switch_sensor runout", PrinterSensorQuantity.Other, 1)]
    [InlineData("fan", PrinterSensorQuantity.TargetTemperature, 50)]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorQuantity.FanSpeed, 50)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 101)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, -1)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 50.5)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, double.NaN)]
    [InlineData("fan_generic aux", PrinterSensorQuantity.FanSpeed, double.PositiveInfinity)]
    [InlineData("temperature_fan exhaust_fan", PrinterSensorQuantity.TargetTemperature, 70.1)]
    [InlineData("heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, -11)]
    [InlineData("heater_generic chamber_heater", PrinterSensorQuantity.TargetTemperature, -1)]
    [InlineData("heater_generic unbounded", PrinterSensorQuantity.TargetTemperature, 40)]
    public void TryBuildCommand_UnsupportedUnknownOrOutOfRange_ReturnsNull(string id, PrinterSensorQuantity quantity, double value)
    {
        MoonrakerSensorMapper.TryBuildCommand(id, quantity, value, Discovered, Settings).Should().BeNull();
    }

    [Theory]
    [InlineData("fan", true)]
    [InlineData("fan_generic aux-2", true)]
    [InlineData("temperature_sensor Chamber_1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("fan_generic aux;M112", false)]
    [InlineData("fan_generic  aux", false)]
    [InlineData("fan_generic aux\r", false)]
    [InlineData("fan_generic ä", false)]
    public void IsSafeObjectName_Input_AllowsOnlyAsciiTokens(string? name, bool expected)
    {
        MoonrakerSensorMapper.IsSafeObjectName(name).Should().Be(expected);
    }

    [Fact]
    public void IsSafeObjectName_OverLengthLimit_IsRejected()
    {
        MoonrakerSensorMapper.IsSafeObjectName("fan_generic " + new string('a', 60)).Should().BeFalse();
    }

    private static readonly IReadOnlySet<string> Discovered = new HashSet<string>(
        [.. AllObjects, "fan_generic a=b", "fan_generic aux\nM112", "fan_generic aux extra", "fan_generic", "heater_generic unbounded"],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, MoonrakerSectionSettings> Settings =
        new Dictionary<string, MoonrakerSectionSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["temperature_fan exhaust_fan"] = new(0, 70, null),
            ["heater_generic chamber_heater"] = new(-10, 80, null),
            ["heater_generic unbounded"] = new(null, 80, null),
        };
}
