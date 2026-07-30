using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Startup-time validator for <see cref="NativePushSettings"/>.
/// Hicks #6/#7: production must fail-fast on missing / malformed native-push
/// credentials rather than lazily degrading to "notConfigured" on the first
/// dispatch — a misconfigured relay endpoint or an invalid .p8 file is a
/// deployment defect, not a per-envelope skip. The APNs key material is
/// cryptographically parsed with <c>ECDsa.ImportFromPem</c>. A disposable
/// sign/verify probe proves that the selected P-256 PEM contains private key
/// material; public-only PEMs are rejected before startup completes. The
/// temporary key is disposed before returning, and every failure is sanitized.
///
/// Registered via <c>AddOptions&lt;NativePushSettings&gt;()...ValidateOnStart()</c>
/// so the process fails to start with a redacted, mode-specific error if any
/// required piece is missing or malformed. When <see cref="NativePushMode.Disabled"/>
/// is set (the shipping default) no credentials are validated and the sender is
/// wired to a no-op — this is intentional: an out-of-the-box deployment must
/// still start with an empty <c>NativePush</c> section.
/// Because these settings are bound and validated at startup, changing any
/// <c>NativePush</c> value requires a process restart.
///
/// Runtime source precedence for the APNs key mirrors
/// <see cref="DirectApnsNativePushSender.EnsureSigningKey"/> — any nonblank
/// inline PEM is the selected source and the file path is ignored. An invalid
/// inline key fails validation rather than silently falling back to a different
/// credential. The file is loaded only when the inline slot is empty.
///
/// Diagnostics are deliberately sanitized. We NEVER emit:
/// * the raw bearer relay ApiKey,
/// * the raw APNs P8 key contents,
/// * the on-disk path of the P8 key file,
/// * the full relay endpoint (query / userinfo can leak),
/// * device tokens (they aren't options anyway).
/// Only high-level shape errors surface: "relay endpoint missing", "relay
/// endpoint must be absolute https URI", "APNs team id missing", "APNs key
/// file unreadable", "APNs key material is not a valid P-256 ECDSA private
/// key", etc.
/// </summary>
public sealed class NativePushSettingsValidator : IValidateOptions<NativePushSettings>
{
    private readonly ILogger<NativePushSettingsValidator> _logger;

    /// <summary>Constructs the validator.</summary>
    public NativePushSettingsValidator(ILogger<NativePushSettingsValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, NativePushSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        switch (options.Mode)
        {
            case NativePushMode.Disabled:
                // No credential requirements. A disabled deployment must still
                // start cleanly with an empty NativePush section.
                break;

            case NativePushMode.Relay:
                ValidateRelay(options.Relay, failures);
                break;

            case NativePushMode.Direct:
                ValidateDirect(options.Apns, failures);
                break;

            default:
                failures.Add($"NativePush:Mode has unknown value '{options.Mode}'.");
                break;
        }

        if (failures.Count == 0)
        {
            // Sanitized "ready" log: emit ONLY the mode name so operators can
            // see the effective config from the boot log without leaking any
            // secrets or absolute file paths.
            _logger.LogInformation(
                "[NativePush] Startup validation ok (mode={Mode}).",
                options.Mode);
            return ValidateOptionsResult.Success;
        }

        // Sanitized failure log too — same rationale.
        _logger.LogError(
            "[NativePush] Startup validation FAILED (mode={Mode}, failures={FailureCount}).",
            options.Mode,
            failures.Count);
        return ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRelay(NativePushRelaySettings relay, List<string> failures)
    {
        if (relay is null)
        {
            failures.Add("NativePush:Relay section is missing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(relay.Endpoint))
        {
            failures.Add("NativePush:Relay:Endpoint is required when NativePush:Mode=Relay.");
        }
        else if (!Uri.TryCreate(relay.Endpoint, UriKind.Absolute, out Uri? parsed))
        {
            failures.Add("NativePush:Relay:Endpoint must be an absolute URI.");
        }
        else if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            // Hard-fail http:// — relay bearer credential MUST NOT ride
            // plaintext. This matches the docs/OPERATOR_NATIVE_PUSH.md
            // production requirement.
            failures.Add("NativePush:Relay:Endpoint must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(relay.ApiKey))
        {
            // Deliberately do NOT echo any part of the key on failure — this
            // log line ends up in ops sinks.
            failures.Add("NativePush:Relay:ApiKey is required when NativePush:Mode=Relay.");
        }
    }

    private static void ValidateDirect(NativePushApnsSettings apns, List<string> failures)
    {
        if (apns is null)
        {
            failures.Add("NativePush:Apns section is missing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(apns.TeamId))
        {
            failures.Add("NativePush:Apns:TeamId is required when NativePush:Mode=Direct.");
        }

        if (string.IsNullOrWhiteSpace(apns.KeyId))
        {
            failures.Add("NativePush:Apns:KeyId is required when NativePush:Mode=Direct.");
        }

        if (string.IsNullOrWhiteSpace(apns.BundleId))
        {
            failures.Add("NativePush:Apns:BundleId is required when NativePush:Mode=Direct.");
        }

        // Hicks #7 source precedence: mirror DirectApnsNativePushSender.EnsureSigningKey.
        // A valid inline P8KeyPem wins outright — the file is ignored so a
        // stale / unreadable path never blocks a working inline deployment.
        // Only when the inline slot is missing do we fall back to the file
        // path. Both slots empty is an outright failure.
        bool hasInline = !string.IsNullOrWhiteSpace(apns.P8KeyPem);
        bool hasPath = !string.IsNullOrWhiteSpace(apns.P8KeyPath);
        if (!hasInline && !hasPath)
        {
            failures.Add("NativePush:Apns requires either P8KeyPem or P8KeyPath when NativePush:Mode=Direct.");
            return;
        }

        string? pem = null;
        string source;
        if (hasInline)
        {
            pem = apns.P8KeyPem;
            source = "P8KeyPem";
        }
        else
        {
            source = "P8KeyPath";
            try
            {
                using FileStream probe = File.Open(apns.P8KeyPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!probe.CanRead)
                {
                    failures.Add("NativePush:Apns:P8KeyPath cannot be read.");
                    return;
                }
            }
            catch (Exception)
            {
                // File.Exists tolerates permission errors by returning false; we
                // rely on a real Open() to prove readability. Any failure here
                // is reduced to a sanitized shape error — the raw path never
                // enters the diagnostic (secrets logging rule).
                failures.Add("NativePush:Apns:P8KeyPath cannot be read.");
                return;
            }

            try
            {
                pem = File.ReadAllText(apns.P8KeyPath!);
            }
            catch (Exception)
            {
                failures.Add("NativePush:Apns:P8KeyPath cannot be read.");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(pem))
        {
            failures.Add($"NativePush:Apns:{source} is empty.");
            return;
        }

        // Cryptographically parse the selected PEM. Any parse or curve error
        // fails validation with a sanitized diagnostic. The ECDsa instance is
        // disposed immediately via `using` — this is a startup-only probe.
        using ECDsa probeKey = ECDsa.Create();
        try
        {
            probeKey.ImportFromPem(pem);
        }
        catch (Exception)
        {
            // No key contents / OpenSSL error text is leaked.
            failures.Add($"NativePush:Apns:{source} is not a valid PEM-encoded ECDSA private key.");
            return;
        }

        ECParameters parameters;
        try
        {
            parameters = probeKey.ExportParameters(false);
        }
        catch (Exception)
        {
            failures.Add($"NativePush:Apns:{source} could not be inspected as an ECDSA key.");
            return;
        }

        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
            && !string.Equals(parameters.Curve.Oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"NativePush:Apns:{source} must be a P-256 (nistP256) ECDSA key as required by APNs ES256.");
            return;
        }

        // ImportFromPem also accepts PUBLIC KEY PEM blocks. Prove possession of
        // private material without exporting it: a public-only key cannot sign,
        // while a valid private P-256 key must round-trip this disposable probe.
        try
        {
            ReadOnlySpan<byte> challenge = "PrintFarmer APNs credential validation"u8;
            byte[] signature = probeKey.SignData(challenge, HashAlgorithmName.SHA256);
            if (!probeKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256))
            {
                failures.Add($"NativePush:Apns:{source} is not a valid PEM-encoded ECDSA private key.");
            }
        }
        catch (Exception)
        {
            failures.Add($"NativePush:Apns:{source} is not a valid PEM-encoded ECDSA private key.");
        }
    }
}
