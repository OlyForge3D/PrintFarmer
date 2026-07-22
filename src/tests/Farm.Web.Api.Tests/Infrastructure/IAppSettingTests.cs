using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Settings.Tests;

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
}
