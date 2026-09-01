using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Infrastructure;

public class IAppSettingTests
{
    [Theory]
    [InlineData(typeof(NetworkDiscoverySettings))]
    [InlineData(typeof(GcodeUploadSettings))]
    [InlineData(typeof(SystemLogSettings))]
    [InlineData(typeof(SignalRSettings))]
    [InlineData(typeof(Farm.Slicer.Module.Settings.SlicerSettings))]
    [InlineData(typeof(DatabaseSettings))]
    [InlineData(typeof(Farm.Infrastructure.Settings.OctoPrintSettings))]
    [InlineData(typeof(ShiftPlanSettings))]
    public void CanSerializeAndDeserializeSettings(Type settingsType)
    {
        object? instance = Activator.CreateInstance(settingsType);
        string json = JsonSerializer.Serialize(instance, settingsType);
        object? deserialized = JsonSerializer.Deserialize(json, settingsType);
        Assert.NotNull(deserialized);
        Assert.IsType(settingsType, deserialized);
    }

    [Fact]
    public void NetworkDiscoverySettings_DeserializationAndValidation_ValidPayload()
    {
        string json = "{" +
                "\"enableDiscovery\": true," +
                "\"requestDelayMs\": 60," +
                "\"clientTimeoutMs\": 5," +
                "\"ports\": [80, 8080]," +
                "\"maxConcurrentRequests\": 10," +
                "\"maxRetries\": 0," +
                "\"discoverySubnets\": [\"192.168.1.0/24\", \"10.0.0.0/24\"]" +
                "}";

        NetworkDiscoverySettings? settings = JsonSerializer.Deserialize<NetworkDiscoverySettings>(json);
        Assert.NotNull(settings);
        Assert.True(settings.EnableDiscovery);
        Assert.Equal(60, settings.RequestDelayMs);
        Assert.Equal(5, settings.ClientTimeoutMs);
        // NOTE: Ports property removed - each discovery probe handles its own backend-specific ports
        Assert.Equal(new[] { "192.168.1.0/24", "10.0.0.0/24" }, settings.DiscoverySubnets);

        if (settings is IValidatableSetting validatable)
        {
            validatable.Validate();
        }
    }

    [Fact]
    public void NetworkDiscoverySettings_Validation_ThrowsOnMalformedPayload()
    {
        string json = "{" +
                "\"enableDiscovery\": true," +
                "\"requestDelayMs\": -1," +
                "\"clientTimeoutMs\": 0," +
                "\"ports\": []," +
                "\"discoverySubnets\": []" +
                "}";

        NetworkDiscoverySettings? settings = JsonSerializer.Deserialize<NetworkDiscoverySettings>(json);
        Assert.NotNull(settings);
        Assert.True(settings.EnableDiscovery);
        Assert.Equal(-1, settings.RequestDelayMs);
        Assert.Equal(0, settings.ClientTimeoutMs);

        if (settings is IValidatableSetting validatable)
        {
            Exception ex = Record.Exception(() => validatable.Validate());
            Assert.NotNull(ex);
            _ = Assert.IsType<ValidationException>(ex);
        }
    }

    [Theory]
    [InlineData(typeof(NetworkDiscoverySettings))]
    [InlineData(typeof(GcodeUploadSettings))]
    [InlineData(typeof(SystemLogSettings))]
    [InlineData(typeof(DatabaseSettings))]
    [InlineData(typeof(ShiftPlanSettings))]
    public void ValidationDoesNotThrowForDefaults(Type settingsType)
    {
        object? instance = Activator.CreateInstance(settingsType);
        if (instance is IValidatableSetting validatable)
        {
            validatable.Validate();
        }
    }

    [Fact]
    public void ShiftPlanSettings_SpoolRestockLeadMinutes_SerializesDefaultAndBoundaries()
    {
        var defaults = new ShiftPlanSettings();

        using JsonDocument defaultJson = JsonDocument.Parse(JsonSerializer.Serialize(defaults));
        Assert.Equal(0, defaultJson.RootElement.GetProperty("spoolRestockLeadMinutes").GetInt32());

        foreach (int value in new[] { 0, 1440 })
        {
            var settings = new ShiftPlanSettings { SpoolRestockLeadMinutes = value };
            settings.Validate();
            string json = JsonSerializer.Serialize(settings);
            ShiftPlanSettings? roundTrip = JsonSerializer.Deserialize<ShiftPlanSettings>(json);
            Assert.NotNull(roundTrip);
            Assert.Equal(value, roundTrip.SpoolRestockLeadMinutes);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void ShiftPlanSettings_SpoolRestockLeadMinutes_OutOfRangeThrows(int value)
    {
        var settings = new ShiftPlanSettings { SpoolRestockLeadMinutes = value };

        _ = Assert.Throws<ValidationException>(settings.Validate);
    }

    [Fact]
    public void SpoolCoverageSettings_SpoolSourceTimeoutMs_SerializesDefaultAndBoundaries()
    {
        var defaults = new SpoolCoverageSettings();

        using JsonDocument defaultJson = JsonDocument.Parse(JsonSerializer.Serialize(defaults));
        Assert.Equal(5000, defaultJson.RootElement.GetProperty("spoolSourceTimeoutMs").GetInt32());

        foreach (int value in new[] { 250, 30000 })
        {
            // FleetResolveTimeoutMs must stay >= SpoolSourceTimeoutMs (cross-field
            // invariant), so pin it to the max range value for both boundaries.
            var settings = new SpoolCoverageSettings { SpoolSourceTimeoutMs = value, FleetResolveTimeoutMs = 60000 };
            settings.Validate();
            string json = JsonSerializer.Serialize(settings);
            SpoolCoverageSettings? roundTrip = JsonSerializer.Deserialize<SpoolCoverageSettings>(json);
            Assert.NotNull(roundTrip);
            Assert.Equal(value, roundTrip.SpoolSourceTimeoutMs);
        }
    }

    [Theory]
    [InlineData(249)]
    [InlineData(30001)]
    public void SpoolCoverageSettings_SpoolSourceTimeoutMs_OutOfRangeThrows(int value)
    {
        var settings = new SpoolCoverageSettings { SpoolSourceTimeoutMs = value };

        _ = Assert.Throws<ValidationException>(settings.Validate);
    }

    [Fact]
    public void SpoolCoverageSettings_FleetResolveTimeoutMs_SerializesDefaultAndBoundaries()
    {
        var defaults = new SpoolCoverageSettings();

        using JsonDocument defaultJson = JsonDocument.Parse(JsonSerializer.Serialize(defaults));
        Assert.Equal(8000, defaultJson.RootElement.GetProperty("fleetResolveTimeoutMs").GetInt32());

        // The fleet budget must stay under the mobile client's 10s per-probe readiness
        // budget by default, or the app reports the coverage and attention services as
        // unavailable at startup - the exact bug this setting exists to prevent.
        Assert.True(defaults.FleetResolveTimeoutMs < 10000);

        foreach (int value in new[] { 1000, 60000 })
        {
            // SpoolSourceTimeoutMs must stay <= FleetResolveTimeoutMs (cross-field
            // invariant), so pin it to the min range value for both boundaries.
            var settings = new SpoolCoverageSettings { FleetResolveTimeoutMs = value, SpoolSourceTimeoutMs = 250 };
            settings.Validate();
            string json = JsonSerializer.Serialize(settings);
            SpoolCoverageSettings? roundTrip = JsonSerializer.Deserialize<SpoolCoverageSettings>(json);
            Assert.NotNull(roundTrip);
            Assert.Equal(value, roundTrip.FleetResolveTimeoutMs);
        }
    }

    [Theory]
    [InlineData(999)]
    [InlineData(60001)]
    public void SpoolCoverageSettings_FleetResolveTimeoutMs_OutOfRangeThrows(int value)
    {
        var settings = new SpoolCoverageSettings { FleetResolveTimeoutMs = value };

        _ = Assert.Throws<ValidationException>(settings.Validate);
    }

    [Fact]
    public void SpoolCoverageSettings_FleetTimeoutEqualToSourceTimeout_Passes()
    {
        // Boundary case for the cross-field invariant (issue #2317): equal values must
        // be accepted, not just strictly-greater ones. Uses non-default values for both
        // fields so the check can't be satisfied by accident via a hardcoded default.
        var settings = new SpoolCoverageSettings { SpoolSourceTimeoutMs = 1000, FleetResolveTimeoutMs = 1000 };

        settings.Validate();
    }

    [Fact]
    public void SpoolCoverageSettings_FleetTimeoutLessThanSourceTimeout_Throws()
    {
        // Rejecting case for the cross-field invariant (issue #2317): a fleet deadline
        // shorter than a single source's own timeout guarantees a slow-but-healthy
        // source never gets a chance to respond before the fleet gives up, silently
        // degrading it to "unavailable" and suppressing runout warnings. Uses
        // non-default values so the check can't be satisfied by a hardcoded default.
        var settings = new SpoolCoverageSettings { SpoolSourceTimeoutMs = 10000, FleetResolveTimeoutMs = 9000 };

        ValidationException ex = Assert.Throws<ValidationException>(settings.Validate);
        Assert.Contains("Fleet spool resolve timeout", ex.Message, StringComparison.Ordinal);
        Assert.Contains("spool source timeout", ex.Message, StringComparison.Ordinal);
    }
}
