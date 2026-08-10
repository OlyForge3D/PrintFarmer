using System;
using System.IO;
using System.Security.Cryptography;
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
        // Generate a real P-256 ECDSA key so the crypto probe (Hicks #6/#7) accepts it.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = pem,
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
        string missing = Path.Join(Path.GetTempPath(), "farm-tests-" + Guid.NewGuid().ToString("N") + ".p8");

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
        // Use a real P-256 PEM so the crypto probe doesn't also complain
        // and hide the TeamId-specific assertion.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = string.Empty,
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = pem,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("TeamId");
    }

    /// <summary>
    /// Hicks #7 precedence: a valid inline PEM MUST win outright, and the
    /// (invalid / non-existent) P8KeyPath MUST NOT block startup. This
    /// mirrors <c>DirectApnsNativePushSender.EnsureSigningKey</c> which
    /// prefers inline when both slots are populated.
    /// </summary>
    [Fact]
    public void Validate_DirectMode_ValidInlinePem_WithGarbagePath_InlineWins()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();

        // Guaranteed non-existent path — proves the file is not consulted
        // when inline PEM is present.
        string garbagePath = Path.Join(
            Path.GetTempPath(),
            "farm-tests-nonexistent-" + Guid.NewGuid().ToString("N") + ".p8");

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = pem,
                P8KeyPath = garbagePath,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Succeeded.Should().BeTrue(
            "when a valid inline PEM is present the file path MUST NOT be consulted (Hicks #7 precedence)");
    }

    /// <summary>
    /// Hicks #7: an inline PEM that is not a valid ECDSA private key MUST
    /// fail with a sanitized diagnostic — no key contents, no OpenSSL error
    /// text, only the config path (<c>NativePush:Apns:P8KeyPem</c>).
    /// </summary>
    [Fact]
    public void Validate_DirectMode_InvalidInlinePem_FailsWithSanitizedDiagnostics()
    {
        // A well-formed PEM envelope wrapping random base64 — parses as a
        // PEM but not as an ECDSA key.
        string invalidPem = "-----BEGIN PRIVATE KEY-----\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n-----END PRIVATE KEY-----";

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = invalidPem,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("P8KeyPem");
        joined.Should().Contain("not a valid");
        // No PEM bytes / no OpenSSL text leak.
        joined.Should().NotContain("AAAAAAAA", "sanitized diagnostics MUST NOT echo raw key bytes");
        joined.Should().NotContain("-----BEGIN", "sanitized diagnostics MUST NOT echo the PEM envelope");
    }

    /// <summary>
    /// Hicks #7: no inline + a file whose contents cannot parse as an ECDSA
    /// key MUST fail with a sanitized diagnostic; the offending path is not
    /// echoed and the file contents are not surfaced.
    /// </summary>
    [Fact]
    public void Validate_DirectMode_NoInlineWithInvalidFileContents_FailsWithSanitizedDiagnostics()
    {
        // Write junk to a real, readable temp file — File.Open + ReadAllText
        // succeed, but ImportFromPem fails.
        string tempPath = Path.Join(
            Path.GetTempPath(),
            "farm-tests-invalid-pem-" + Guid.NewGuid().ToString("N") + ".p8");
        File.WriteAllText(tempPath, "not a pem file");
        try
        {
            var settings = new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                Apns = new NativePushApnsSettings
                {
                    TeamId = "TEAM123",
                    KeyId = "KEY456",
                    BundleId = "com.example.app",
                    P8KeyPath = tempPath,
                    P8KeyPem = null,
                },
            };
            NativePushSettingsValidator validator = BuildValidator();

            ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

            result.Failed.Should().BeTrue();
            string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
            joined.Should().Contain("P8KeyPath", "the config source MUST be named");
            joined.Should().Contain("not a valid");
            joined.Should().NotContain(tempPath, "sanitized diagnostics MUST NOT echo the raw file path");
            joined.Should().NotContain("not a pem file", "sanitized diagnostics MUST NOT echo file contents");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Hicks #7: a valid ECDSA key on a curve OTHER than P-256 MUST fail —
    /// APNs ES256 requires nistP256. The diagnostic names the curve
    /// requirement without echoing key material.
    /// </summary>
    [Fact]
    public void Validate_DirectMode_NonP256Curve_Fails()
    {
        // P-384 is a real ECDSA curve but wrong for APNs ES256.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string pem = key.ExportPkcs8PrivateKeyPem();

        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = pem,
            },
        };
        NativePushSettingsValidator validator = BuildValidator();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("P-256", "the diagnostic MUST name the required curve");
        joined.Should().NotContain(pem, "sanitized diagnostics MUST NOT echo the raw PEM");
    }

    [Fact]
    public void Validate_DirectMode_InlinePublicOnlyP256_FailsAsPrivateKeyWithoutLeakingPem()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = key.ExportSubjectPublicKeyInfoPem();
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123",
                KeyId = "KEY456",
                BundleId = "com.example.app",
                P8KeyPem = publicPem,
            },
        };

        ValidateOptionsResult result = BuildValidator().Validate(Options.DefaultName, settings);

        result.Failed.Should().BeTrue();
        string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
        joined.Should().Contain("P8KeyPem");
        joined.Should().Contain("private key");
        joined.Should().NotContain(publicPem);
        joined.Should().NotContain("BEGIN PUBLIC KEY");
    }

    [Fact]
    public void Validate_DirectMode_FilePublicOnlyP256_FailsAsPrivateKeyWithoutLeakingSource()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = key.ExportSubjectPublicKeyInfoPem();
        string path = Path.Join(Path.GetTempPath(), $"farm-tests-public-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, publicPem);
        try
        {
            var settings = new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                Apns = new NativePushApnsSettings
                {
                    TeamId = "TEAM123",
                    KeyId = "KEY456",
                    BundleId = "com.example.app",
                    P8KeyPath = path,
                },
            };

            ValidateOptionsResult result = BuildValidator().Validate(Options.DefaultName, settings);

            result.Failed.Should().BeTrue();
            string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
            joined.Should().Contain("P8KeyPath");
            joined.Should().Contain("private key");
            joined.Should().NotContain(path);
            joined.Should().NotContain(publicPem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_DirectMode_FilePrivateP256_Succeeds()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = Path.Join(Path.GetTempPath(), $"farm-tests-private-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        try
        {
            var settings = new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                Apns = new NativePushApnsSettings
                {
                    TeamId = "TEAM123",
                    KeyId = "KEY456",
                    BundleId = "com.example.app",
                    P8KeyPath = path,
                },
            };

            BuildValidator().Validate(Options.DefaultName, settings).Succeeded.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_DirectMode_PublicInlineWithValidPrivateFile_FailsSelectedInlineSource()
    {
        using ECDsa publicKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = publicKey.ExportSubjectPublicKeyInfoPem();
        string path = Path.Join(Path.GetTempPath(), $"farm-tests-fallback-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, privateKey.ExportPkcs8PrivateKeyPem());
        try
        {
            var settings = new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                Apns = new NativePushApnsSettings
                {
                    TeamId = "TEAM123",
                    KeyId = "KEY456",
                    BundleId = "com.example.app",
                    P8KeyPem = publicPem,
                    P8KeyPath = path,
                },
            };

            ValidateOptionsResult result = BuildValidator().Validate(Options.DefaultName, settings);

            result.Failed.Should().BeTrue();
            string joined = string.Join(" | ", result.Failures ?? Array.Empty<string>());
            joined.Should().Contain("P8KeyPem");
            joined.Should().NotContain("P8KeyPath");
            joined.Should().NotContain(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Hicks #7: no inline and no path is the strictest failure — both slots
    /// missing means Direct mode cannot function.
    /// </summary>
    [Fact]
    public void Validate_DirectMode_NoInlineNoPath_Fails()
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
        joined.Should().Contain("P8KeyPath");
    }

    private static NativePushSettingsValidator BuildValidator()
        => new(NullLogger<NativePushSettingsValidator>.Instance);
}
