using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Bounded, startup-validated operational configuration for the signed release discovery
/// pipeline (issue #2757): the GitHub Releases HTTP client, the Cosign signature verifier
/// process, and <see cref="VerifiedReleaseDiscoveryMonitorService"/>'s polling interval.
/// <para>
/// Deliberately configuration-bound only (like <c>CatalogUpdateSettings</c>'s
/// <c>IOptionsMonitor</c> wiring) rather than a persisted <see cref="Farm.Settings.IAppSetting"/>
/// — these are operator/deployment knobs, not a user-editable settings-store surface. The one
/// user-editable knob for this feature (which release channel to discover) is the separate,
/// genuinely persisted <see cref="Farm.Infrastructure.Settings.UpdateChannelSettings"/>.
/// </para>
/// </summary>
public sealed class VerifiedReleaseDiscoveryOptions
{
    public const string SectionName = "HostUpdates:VerifiedReleaseDiscovery";

    /// <summary>Default Cosign binary resolution — resolved via PATH like other bundled
    /// CLI dependencies documented in <c>docs/SLICER_WORKER_CI_SECURITY.md</c>. Deployments
    /// that vendor a specific Cosign binary should set this to that absolute path.</summary>
    public const string DefaultCosignExecutablePath = "cosign";

    /// <summary>Whether the discovery monitor runs at all. Defaults to enabled; deployments
    /// that never want outbound GitHub/Cosign calls can disable it entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between discovery polls. Bounded to the same 5-minute .. 24-hour
    /// range as <c>CatalogUpdateSettings.IntervalSeconds</c>.</summary>
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>Timeout, in seconds, for the GitHub Releases <see cref="HttpClient"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>Path (or bare command, resolved via PATH) to the Cosign executable actually
    /// invoked by <see cref="ProcessCosignVerifier"/>. Never a user-controlled value — this is
    /// deployment configuration only.</summary>
    public string CosignExecutablePath { get; set; } = DefaultCosignExecutablePath;

    /// <summary>Timeout, in seconds, for each Cosign verification subprocess invocation.</summary>
    public int CosignTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum bytes of combined stdout/stderr captured from a Cosign invocation for
    /// diagnostics, mirroring <see cref="CosignVerifierOptions.MaxDiagnostics"/>'s own default.</summary>
    public int CosignMaxDiagnosticsBytes { get; set; } = 8192;

    /// <summary>Maps to the record type <see cref="ProcessCosignVerifier"/> actually consumes.</summary>
    public CosignVerifierOptions ToCosignVerifierOptions() =>
        new(CosignExecutablePath, TimeSpan.FromSeconds(CosignTimeoutSeconds), CosignMaxDiagnosticsBytes);
}

/// <summary>
/// Startup-time validator for <see cref="VerifiedReleaseDiscoveryOptions"/>. Registered via
/// <c>AddOptions&lt;VerifiedReleaseDiscoveryOptions&gt;()...ValidateOnStart()</c> so a
/// misconfigured interval/timeout/executable path fails fast at process start rather than
/// surfacing as a silently-never-succeeding background service later.
/// </summary>
public sealed class VerifiedReleaseDiscoveryOptionsValidator : IValidateOptions<VerifiedReleaseDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, VerifiedReleaseDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.IntervalSeconds is < 300 or > 86400)
        {
            failures.Add("HostUpdates:VerifiedReleaseDiscovery:IntervalSeconds must be between 300 and 86400.");
        }

        if (options.HttpTimeoutSeconds is < 5 or > 120)
        {
            failures.Add("HostUpdates:VerifiedReleaseDiscovery:HttpTimeoutSeconds must be between 5 and 120.");
        }

        if (options.CosignTimeoutSeconds is < 5 or > 300)
        {
            failures.Add("HostUpdates:VerifiedReleaseDiscovery:CosignTimeoutSeconds must be between 5 and 300.");
        }

        if (options.CosignMaxDiagnosticsBytes is < 1024 or > 65536)
        {
            failures.Add("HostUpdates:VerifiedReleaseDiscovery:CosignMaxDiagnosticsBytes must be between 1024 and 65536.");
        }

        if (string.IsNullOrWhiteSpace(options.CosignExecutablePath))
        {
            failures.Add("HostUpdates:VerifiedReleaseDiscovery:CosignExecutablePath is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
