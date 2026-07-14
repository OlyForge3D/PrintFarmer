using System;
using System.IO;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

/// <summary>
/// Hicks #6: startup validation for native-push credentials MUST reject
/// obvious deployment errors up-front, with sanitized diagnostics that never
/// leak the raw ApiKey, .p8 contents, absolute key path, or full URI. These
/// tests pin both the accept paths (Disabled requires nothing; correctly
/// configured Relay/Direct pass) and the reject paths (missing / non-HTTPS
/// endpoint, missing key material, unreadable .p8 file).
/// </summary>
public sealed class NativePushSettingsValidatorTests
{
    /// <summary>
    /// Disabled mode is the shipped default: an out-of-the-box deployment
    /// with an empty NativePush config section MUST start cleanly. We prove
    /// that an entirely default-constructed settings object passes.
    /// </summary>
    [Fact]
    public void Validate_DisabledMode_Succeeds()
    {
        var settings = new NativePushSettings { Mode = NativePushMode.Disabled };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Succeeded.Should().BeTrue("Disabled mode requires no credentials by contract");
    }

    /// <summary>
    /// A well-formed Relay config with https URI + api key passes and does
    /// NOT include any raw secret material in the reported diagnostics
    /// (there are no diagnostics on success, but sanitized-diagnostics
    /// coverage is a hard requirement — see Fail tests below).
    /// </summary>
    [Fact]
    public void Validate_RelayMode_WithHttpsAndApiKey_Succeeds()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            Relay = new NativePushRelaySettings
            {
                Endpoint = "https://relay.example.com/push",
                ApiKey = "SECRET-BEARER-VALUE",
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Relay mode with an <c>http://</c> endpoint MUST fail — the bearer
    /// api key can never ride plaintext to the relay. Diagnostics must NOT
    /// echo the endpoint (its userinfo / query can carry secrets).
    /// </summary>
    [Fact]
    public void Validate_RelayMode_WithHttpEndpoint_FailsWithSanitizedDiagnostics()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            Relay = new NativePushRelaySettings
            {
                Endpoint = "http://relay.example.com/path?token=SECRET",
                ApiKey = "BEARER",
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("HTTPS", "diagnostics must name the shape violation");
        joined.Should().NotContain("SECRET", "sanitized diagnostics MUST NOT echo the raw URI query");
        joined.Should().NotContain("relay.example.com", "sanitized diagnostics MUST NOT echo the host either");
    }

    /// <summary>Relay mode with a missing endpoint fails and diagnostics stay sanitized.</summary>
    [Fact]
    public void Validate_RelayMode_MissingEndpoint_Fails()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            Relay = new NativePushRelaySettings
            {
                Endpoint = string.Empty,
                ApiKey = "BEARER",
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("Endpoint");
    }

    /// <summary>Relay mode without api key fails without echoing the (missing) key.</summary>
    [Fact]
    public void Validate_RelayMode_MissingApiKey_Fails()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            Relay = new NativePushRelaySettings
            {
                Endpoint = "https://relay.example.com/push",
                ApiKey = string.Empty,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("ApiKey");
    }

    /// <summary>Direct mode with inline PEM (no path) passes.</summary>
    [Fact]
    public void Validate_DirectMode_WithInlinePem_Succeeds()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Succeeded.Should().BeTrue();
    }

    /// <summary>Direct mode without any key material fails.</summary>
    [Fact]
    public void Validate_DirectMode_NoKeyMaterial_Fails()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = null,
                P8KeyPath = null,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("P8KeyPem");
    }

    /// <summary>
    /// Direct mode with a path that does NOT exist fails and never echoes
    /// the offending path back — the path itself is sensitive on shared
    /// deployment hosts (predictable /etc/... layouts leak host topology).
    /// </summary>
    [Fact]
    public void Validate_DirectMode_UnreadableP8Path_FailsWithSanitizedDiagnostics()
    {
        // Use a random /tmp-ish path that certainly does not exist.
        string missing = Path.Combine(Path.GetTempPath(), "farm-tests-" + Guid.NewGuid().ToString("N") + ".p8");

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPath = missing,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("P8KeyPath");
        joined.Should().NotContain(missing, "sanitized diagnostics MUST NOT echo the raw path");
    }

    /// <summary>Direct mode missing TeamId fails.</summary>
    [Fact]
    public void Validate_DirectMode_MissingTeamId_Fails()
    {
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = string.Empty,
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("TeamId");
    }

    private static NativePushSettingsValidator BuildValidator()
        => new(NullLogger<NativePushSettingsValidator>.Instance);
}
