using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Startup-time validator for <see cref="NativePushSettings"/>.
/// Hicks #6: production must fail-fast on missing / malformed native-push
/// credentials rather than lazily degrading to "notConfigured" on the first
/// dispatch — a misconfigured relay endpoint or missing .p8 file is a
/// deployment defect, not a per-envelope skip.
///
/// Registered via <c>AddOptions&lt;NativePushSettings&gt;()...ValidateOnStart()</c>
/// so the process fails to start with a redacted, mode-specific error if any
/// required piece is missing. When <see cref="NativePushMode.Disabled"/> is
/// set (the shipping default) no credentials are validated and the sender is
/// wired to a no-op — this is intentional: an out-of-the-box deployment must
/// still start with an empty <c>NativePush</c> section.
///
/// Diagnostics are deliberately sanitized. We NEVER emit:
/// * the raw bearer relay ApiKey,
/// * the raw APNs P8 key contents,
/// * the on-disk path of the P8 key file,
/// * the full relay endpoint (query / userinfo can leak),
/// * device tokens (they aren't options anyway).
/// Only high-level shape errors surface: "relay endpoint missing", "relay
/// endpoint must be absolute https URI", "APNs team id missing", "APNs key
/// file unreadable", etc.
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

        bool hasInline = !string.IsNullOrWhiteSpace(apns.P8KeyPem);
        bool hasPath = !string.IsNullOrWhiteSpace(apns.P8KeyPath);
        if (!hasInline && !hasPath)
        {
            failures.Add("NativePush:Apns requires either P8KeyPem or P8KeyPath when NativePush:Mode=Direct.");
        }
        else if (hasPath)
        {
            // File.Exists tolerates permission errors by returning false; use
            // an explicit read-check that neither logs the path nor throws so
            // operators see a shape error, not a stack trace over a sensitive
            // path. We MUST NOT include apns.P8KeyPath in the failure message
            // (secrets logging rule).
            try
            {
                using FileStream probe = File.Open(apns.P8KeyPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!probe.CanRead)
                {
                    failures.Add("NativePush:Apns:P8KeyPath cannot be read.");
                }
            }
            catch (Exception)
            {
                failures.Add("NativePush:Apns:P8KeyPath cannot be read.");
            }
        }
    }
}
