using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Settings;

public class ObicoSettingsTests
{
    [Fact]
    public void Validate_DisabledWithoutUrl_AllowsEmptyUrl()
    {
        var settings = new ObicoSettings();

        settings.Validate();
    }

    [Fact]
    public void Validate_DisabledWithInvalidConfidenceThreshold_Throws()
    {
        var settings = new ObicoSettings { ConfidenceThreshold = 1.1m };

        ValidationException exception = Assert.Throws<ValidationException>(settings.Validate);

        Assert.Contains("Confidence threshold", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EnabledWithoutUrl_AllowsPoolOnlyConfiguration()
    {
        var settings = new ObicoSettings { Enabled = true };

        settings.Validate();
    }
}
