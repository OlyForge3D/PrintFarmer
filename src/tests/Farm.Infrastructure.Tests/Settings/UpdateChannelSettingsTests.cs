using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Settings;

/// <summary>
/// Focused coverage for <see cref="UpdateChannelSettings"/> (issue #2757 item 2): strict
/// stable/insider value enforcement and the explicit insider-acknowledgement requirement.
/// </summary>
public class UpdateChannelSettingsTests
{
    [Fact]
    public void SectionName_IsUpdateChannel()
    {
        UpdateChannelSettings.SectionName.Should().Be("UpdateChannel");
        UpdateChannelSettings.SectionKey.Should().Be("UpdateChannel");
    }

    [Fact]
    public void Defaults_AreStableAndUnacknowledged()
    {
        var settings = new UpdateChannelSettings();

        settings.Channel.Should().Be("stable");
        settings.InsiderAcknowledged.Should().BeFalse();
    }

    [Fact]
    public void Validate_DefaultStable_DoesNotThrow()
    {
        var settings = new UpdateChannelSettings();

        Action act = settings.Validate;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Stable")]
    [InlineData("Insider")]
    [InlineData("beta")]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_NonLiteralOrUnknownChannel_ThrowsValidationException(string channel)
    {
        var settings = new UpdateChannelSettings { Channel = channel };

        Action act = settings.Validate;

        act.Should().Throw<ValidationException>()
            .WithMessage($"*{channel}*");
    }

    [Fact]
    public void Validate_InsiderWithoutAcknowledgement_ThrowsValidationException()
    {
        var settings = new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = false };

        Action act = settings.Validate;

        act.Should().Throw<ValidationException>()
            .WithMessage("*InsiderAcknowledged*");
    }

    [Fact]
    public void Validate_InsiderWithAcknowledgement_DoesNotThrow()
    {
        var settings = new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true };

        Action act = settings.Validate;

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_StableWithoutAcknowledgement_DoesNotThrow()
    {
        // Acknowledgement is only required when selecting insider -- stable never needs it.
        var settings = new UpdateChannelSettings { Channel = "stable", InsiderAcknowledged = false };

        Action act = settings.Validate;

        act.Should().NotThrow();
    }
}
