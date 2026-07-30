using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Default <see cref="IOperatorFeatureGate"/> implementation.
///
/// <para>
/// Reads the persisted <see cref="OperatorFeatureSettings"/> JSON directly from
/// <see cref="IAppSettingsRepository"/> under key <c>OperatorFeatures</c> and applies the
/// explicit-<c>false</c> environment hard-disable override on top. This intentionally does
/// NOT go through <see cref="ISettingsService"/> so that
/// <c>OperatorFeatures:&lt;flagName&gt;</c> configuration values are never bound as the base
/// value — the wider <c>SettingsService</c> falls back to
/// <c>config.GetSection("OperatorFeatures").Get(type)</c> when the row is missing, which
/// would silently force-enable a flag from an env var like
/// <c>OperatorFeatures__nativePushEnabled=true</c>. Only explicit-<c>false</c> may take
/// effect (via <see cref="IsEnvironmentHardDisable"/>); every other configuration form
/// (absent, non-boolean, <c>true</c>) falls through to the DB/default base.
/// </para>
///
/// <para>
/// Construction is deliberately DB-independent — the repository ctor holds only a scoped
/// <c>AppDbContext</c> reference and does no I/O — so <c>GET /api/system/capabilities</c>
/// never fails at DI activation. Any repository failure during a read (DB down, provider
/// misconfigured, malformed row) is logged and swallowed with a fall-back to
/// <c>new OperatorFeatureSettings()</c> defaults, ensuring the capability endpoint keeps
/// returning the documented defaults even when persisted settings are unavailable.
/// </para>
///
/// <para>
/// Writes still flow through <see cref="ISettingsService"/> from the Unified Settings admin
/// page, which persists the same JSON under the same key; this gate observes those changes
/// on the next request without needing to invalidate a cache.
/// </para>
/// </summary>
public sealed class OperatorFeatureGate : IOperatorFeatureGate
{
    private readonly IAppSettingsRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatorFeatureGate> _logger;

    private sealed record FeatureDescriptor(
        OperatorFeature Feature,
        string FlagName,
        Func<OperatorFeatureSettings, bool> Read,
        bool Default);

    private static readonly IReadOnlyList<FeatureDescriptor> Descriptors =
    [
        new(OperatorFeature.Attention, "attentionEnabled", s => s.AttentionEnabled, true),
        new(OperatorFeature.NativePush, "nativePushEnabled", s => s.NativePushEnabled, false),
        new(OperatorFeature.FilamentCoverage, "filamentCoverageEnabled", s => s.FilamentCoverageEnabled, true),
        new(OperatorFeature.GuidedSwap, "guidedSwapEnabled", s => s.GuidedSwapEnabled, true),
        new(OperatorFeature.MultiSlotFallback, "multiSlotFallbackEnabled", s => s.MultiSlotFallbackEnabled, true),
        new(OperatorFeature.ShiftPlan, "shiftPlanEnabled", s => s.ShiftPlanEnabled, true),
        new(OperatorFeature.PrintedPartsInventory, "printedPartsInventoryEnabled", s => s.PrintedPartsInventoryEnabled, false),
        new(OperatorFeature.OfflineWriteReplay, "offlineWriteReplayEnabled", s => s.OfflineWriteReplayEnabled, true),
    ];

    private static readonly IReadOnlyList<(OperatorFeature Feature, string FlagName)> FeatureNames =
        Descriptors.Select(d => (d.Feature, d.FlagName)).ToArray();

    public OperatorFeatureGate(
        IAppSettingsRepository repository,
        IConfiguration configuration,
        ILogger<OperatorFeatureGate> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures => FeatureNames;

    public string GetFlagName(OperatorFeature feature) => FindDescriptor(feature).FlagName;

    public bool IsEnabled(OperatorFeature feature)
    {
        FeatureDescriptor descriptor = FindDescriptor(feature);
        return Resolve(descriptor, LoadSettings());
    }

    public async Task<bool> IsEnabledAsync(OperatorFeature feature, CancellationToken cancellationToken = default)
    {
        // General fallback path (controllers, filters, hosted services, capability
        // gating). Async and cancellation-aware so callers never block a thread-pool
        // worker on the repository read. Failure semantics MIRROR the synchronous
        // <see cref="IsEnabled(OperatorFeature)"/>: a repository/DB failure logs and
        // degrades to the documented configured/default result rather than propagating
        // (which would turn a transient outage into an HTTP 500 for every migrated
        // caller). Caller-requested cancellation is control flow and is rethrown, never
        // swallowed into a fallback answer. Security/correctness boundaries that must
        // fail closed use <see cref="IsEnabledStrictAsync"/> instead.
        FeatureDescriptor descriptor = FindDescriptor(feature);
        try
        {
            OperatorFeatureSettings settings = await LoadSettingsStrictAsync(cancellationToken).ConfigureAwait(false);
            return Resolve(descriptor, settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do not swallow caller-requested cancellation: propagate so shutdown/abort
            // is observed as control flow rather than a spurious feature answer.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Scoped strictly to persisted-settings acquisition so a DB outage, provider
            // startup race, or missing table degrades to defaults instead of a 500. The
            // environment hard-disable override is still applied by Resolve below.
            _logger.LogWarning(
                ex,
                "Unable to read persisted OperatorFeatureSettings for {FlagName}; falling back to defaults",
                descriptor.FlagName);
            return Resolve(descriptor, new OperatorFeatureSettings());
        }
    }

    public async Task<bool> IsEnabledStrictAsync(OperatorFeature feature, CancellationToken cancellationToken = default)
    {
        // Strict, fail-closed companion used only by security/correctness boundaries —
        // currently the native-push transport reservation/authorization path. Never
        // blocks a thread on the async repository read, and DELIBERATELY does NOT
        // swallow repository/DB exceptions: dispatchers depend on the caller-scoped
        // catch site to log fail-closed (with delivery/lifecycle context) and roll back
        // reservations in one place, so an outage can never silently authorize a send.
        // A malformed persisted JSON row still degrades to defaults (mirrors the general
        // and sync paths): that is a data-shape failure the gate can safely absorb
        // without hiding an infrastructure outage. Cancellation propagates unchanged.
        FeatureDescriptor descriptor = FindDescriptor(feature);
        OperatorFeatureSettings settings = await LoadSettingsStrictAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(descriptor, settings);
    }

    public bool IsHardDisabledByEnvironment(OperatorFeature feature)
        => IsEnvironmentHardDisable(FindDescriptor(feature).FlagName);

    public OperatorFeatureFlagsDto GetEffectiveFlags()
    {
        OperatorFeatureSettings s = LoadSettings();
        return new OperatorFeatureFlagsDto
        {
            AttentionEnabled = Resolve(Descriptors[0], s),
            NativePushEnabled = Resolve(Descriptors[1], s),
            FilamentCoverageEnabled = Resolve(Descriptors[2], s),
            GuidedSwapEnabled = Resolve(Descriptors[3], s),
            MultiSlotFallbackEnabled = Resolve(Descriptors[4], s),
            ShiftPlanEnabled = Resolve(Descriptors[5], s),
            PrintedPartsInventoryEnabled = Resolve(Descriptors[6], s),
            OfflineWriteReplayEnabled = Resolve(Descriptors[7], s),
        };
    }

    private OperatorFeatureSettings LoadSettings()
    {
        // Read the persisted JSON row through the repository's no-tracking path. This must
        // query persisted state on every call: long-running fan-outs re-use a scoped gate, and
        // a tracked AppSettings entity would hide a kill-switch update committed by another
        // context. We block on the async repository because the gate surface is sync
        // (controllers, worker gate checks) and the wider settings
        // pipeline uses the same sync-over-async pattern (see SettingsService.Save<T>). No
        // SynchronizationContext exists in ASP.NET Core, so there is no deadlock risk.
        //
        // Every failure mode — DB down, provider misconfigured, malformed JSON — falls back
        // to class defaults. Configuration-section binding is intentionally NOT consulted
        // here so that `OperatorFeatures__<flag>=true` cannot force-enable a feature.
        //
        // Callers that hold in-memory locks (native-push dispatcher/lifecycle/transport)
        // must use <see cref="LoadSettingsStrictAsync"/> via
        // <see cref="IsEnabledStrictAsync"/> to avoid pinning a thread-pool worker on the
        // underlying EF round-trip; see the documented contract on
        // <see cref="IOperatorFeatureGate.IsEnabledStrictAsync"/>.
        try
        {
#pragma warning disable VSTHRD002 // Synchronously waiting on tasks — required to keep the gate sync surface
            AppSettingsEntity? row = _repository
                .GetReadOnlyAsync(OperatorFeatureSettings.SectionName)
                .GetAwaiter()
                .GetResult();
#pragma warning restore VSTHRD002

            return ParseSettingsRow(row);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OperatorFeatureSettings row is malformed JSON; falling back to defaults");
            return new OperatorFeatureSettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Broad catch is scoped strictly to persisted-settings acquisition so a DB outage,
            // provider startup race, or missing table does not turn every request into a 500.
            // Feature endpoints that perform normal writes must NOT copy this pattern — the
            // gate is the single degradation point.
            _logger.LogWarning(ex, "Unable to read persisted OperatorFeatureSettings; falling back to defaults");
            return new OperatorFeatureSettings();
        }
    }

    private async Task<OperatorFeatureSettings> LoadSettingsStrictAsync(CancellationToken cancellationToken)
    {
        // Shared strict loader for both async paths. Used directly by
        // <see cref="IsEnabledStrictAsync"/> (fail-closed) and wrapped by the
        // fallback try/catch in <see cref="IsEnabledAsync"/>. The native-push
        // dispatcher reads the gate at its authorization linearization point (see docs
        // on <see cref="IOperatorFeatureGate.IsEnabledStrictAsync"/>), outside every
        // dispatcher/lifecycle/item/transport lock. Cancellation propagates via
        // <paramref name="cancellationToken"/> so a shutdown/caller cancel is observed
        // promptly and the caller can roll back reservations without waiting on the DB.
        //
        // Repository/DB exceptions PROPAGATE (unlike <see cref="LoadSettings"/>) so the
        // strict caller can log fail-closed with delivery/lifecycle context and roll
        // back its reservations at the same site; the general fallback caller converts
        // them to defaults. A malformed persisted JSON row still degrades to class
        // defaults: that is a data-shape issue and matches the capability endpoint's
        // contract.
        AppSettingsEntity? row;
        try
        {
            row = await _repository
                .GetReadOnlyAsync(OperatorFeatureSettings.SectionName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OperatorFeatureSettings row is malformed JSON; falling back to defaults");
            return new OperatorFeatureSettings();
        }

        try
        {
            return ParseSettingsRow(row);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OperatorFeatureSettings row is malformed JSON; falling back to defaults");
            return new OperatorFeatureSettings();
        }
    }

    private static OperatorFeatureSettings ParseSettingsRow(AppSettingsEntity? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.SettingsJson))
        {
            return new OperatorFeatureSettings();
        }

        OperatorFeatureSettings? parsed = JsonSerializer.Deserialize<OperatorFeatureSettings>(row.SettingsJson);
        return parsed ?? new OperatorFeatureSettings();
    }

    private bool Resolve(FeatureDescriptor descriptor, OperatorFeatureSettings settings)
    {
        if (IsEnvironmentHardDisable(descriptor.FlagName))
        {
            return false;
        }

        return descriptor.Read(settings);
    }

    private bool IsEnvironmentHardDisable(string flagName)
    {
        // Environment/config values named OperatorFeatures__<flagName>=false hard-disable the
        // feature regardless of the database value. Any other value (missing, "true", junk)
        // falls through to the database value — explicit-false is the only override.
        string? raw = _configuration[$"{OperatorFeatureSettings.SectionName}:{flagName}"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out bool parsed) && !parsed;
    }

    private static FeatureDescriptor FindDescriptor(OperatorFeature feature)
    {
        foreach (FeatureDescriptor d in Descriptors)
        {
            if (d.Feature == feature)
            {
                return d;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown operator feature.");
    }
}
