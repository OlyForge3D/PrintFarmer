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
    [InlineData(typeof(SlicerSettings))]
    [InlineData(typeof(DatabaseSettings))]
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
    public void ValidationDoesNotThrowForDefaults(Type settingsType)
    {
        object? instance = Activator.CreateInstance(settingsType);
        if (instance is IValidatableSetting validatable)
        {
            validatable.Validate();
        }
    }
}
