using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="VerifiedReleaseDiscoveryOptionsValidator"/> (issue #2757 item
/// 1): startup-time bounds enforcement for the discovery pipeline's operational options.
/// </summary>
public class VerifiedReleaseDiscoveryOptionsValidatorTests
{
    private static readonly VerifiedReleaseDiscoveryOptionsValidator Validator = new();

    [Fact]
    public void Validate_Defaults_Succeeds()
    {
        var options = new VerifiedReleaseDiscoveryOptions();

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(299)]
    [InlineData(86401)]
    public void Validate_IntervalSecondsOutOfBounds_Fails(int intervalSeconds)
    {
        var options = new VerifiedReleaseDiscoveryOptions { IntervalSeconds = intervalSeconds };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("IntervalSeconds");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(121)]
    public void Validate_HttpTimeoutSecondsOutOfBounds_Fails(int httpTimeoutSeconds)
    {
        var options = new VerifiedReleaseDiscoveryOptions { HttpTimeoutSeconds = httpTimeoutSeconds };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("HttpTimeoutSeconds");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public void Validate_CosignTimeoutSecondsOutOfBounds_Fails(int cosignTimeoutSeconds)
    {
        var options = new VerifiedReleaseDiscoveryOptions { CosignTimeoutSeconds = cosignTimeoutSeconds };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CosignTimeoutSeconds");
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(65537)]
    public void Validate_CosignMaxDiagnosticsBytesOutOfBounds_Fails(int maxDiagnosticsBytes)
    {
        var options = new VerifiedReleaseDiscoveryOptions { CosignMaxDiagnosticsBytes = maxDiagnosticsBytes };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CosignMaxDiagnosticsBytes");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validate_EmptyCosignExecutablePath_Fails(string? cosignExecutablePath)
    {
        var options = new VerifiedReleaseDiscoveryOptions { CosignExecutablePath = cosignExecutablePath! };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CosignExecutablePath");
    }

    [Fact]
    public void ToCosignVerifierOptions_MapsFieldsToCosignVerifierOptions()
    {
        var options = new VerifiedReleaseDiscoveryOptions
        {
            CosignExecutablePath = "/usr/local/bin/cosign",
            CosignTimeoutSeconds = 45,
            CosignMaxDiagnosticsBytes = 4096,
        };

        CosignVerifierOptions mapped = options.ToCosignVerifierOptions();

        mapped.ExecutablePath.Should().Be("/usr/local/bin/cosign");
        mapped.Timeout.Should().Be(TimeSpan.FromSeconds(45));
        mapped.MaxDiagnostics.Should().Be(4096);
    }
}
