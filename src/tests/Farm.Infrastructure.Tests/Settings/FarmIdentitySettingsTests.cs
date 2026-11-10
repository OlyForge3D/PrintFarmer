using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Settings;

public class FarmIdentitySettingsTests
{
    [Fact]
    public void Defaults_ExistingInstallation_PreservesBrandAndUtc()
    {
        var settings = JsonSerializer.Deserialize<FarmIdentitySettings>("{}")!;
        settings.Validate();
        Assert.Equal("PrintFarmer", settings.FarmName);
        Assert.Equal("UTC", settings.TimeZone);
        Assert.Equal("UTC", new JobSchedule().TimeZone);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Pacific/Chatham")]
    public void Validate_IanaTimeZone_Accepts(string timeZone)
    {
        new FarmIdentitySettings { TimeZone = timeZone }.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    [InlineData("Pacific Standard Time")]
    [InlineData(" America/Los_Angeles")]
    public void Validate_InvalidTimeZone_ThrowsFieldError(string? timeZone)
    {
        var settings = new FarmIdentitySettings { TimeZone = timeZone! };
        var error = Assert.Throws<ValidationException>(settings.Validate);
        Assert.Contains(nameof(FarmIdentitySettings.TimeZone), error.ValidationResult!.MemberNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Farm\nName")]
    public void Validate_InvalidName_ThrowsFieldError(string? name)
    {
        var settings = new FarmIdentitySettings { FarmName = name! };
        var error = Assert.Throws<ValidationException>(settings.Validate);
        Assert.Contains(nameof(FarmIdentitySettings.FarmName), error.ValidationResult!.MemberNames);
    }

    [Fact]
    public void Validate_NameLength_EnforcesBoundary()
    {
        new FarmIdentitySettings { FarmName = new string('a', 80) }.Validate();
        Assert.Throws<ValidationException>(() =>
            new FarmIdentitySettings { FarmName = new string('a', 81) }.Validate());
    }

    [Fact]
    public void Serialize_CustomIdentity_UsesCamelCase()
    {
        string json = JsonSerializer.Serialize(new FarmIdentitySettings { FarmName = "Workshop", TimeZone = "Europe/London" });
        Assert.Contains("\"farmName\":\"Workshop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"Europe/London\"", json, StringComparison.Ordinal);
    }
}
