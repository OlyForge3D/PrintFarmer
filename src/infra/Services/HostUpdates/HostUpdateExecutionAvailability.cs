using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Whether the concrete host-update executor is actually usable right now.</summary>
public enum HostUpdateExecutionAvailabilityState
{
    Available,
    Unavailable,
}

/// <summary>
/// The executor's current operational readiness. Never a guess: <see cref="Available"/> means
/// every required adapter, its durable root, and its runtime dependency have been positively
/// probed; <see cref="Unavailable"/> always carries the exact <see cref="Reasons"/> so an
/// operator (or the #2666 scheduler polling this status) knows precisely what is missing,
/// rather than receiving a bare failure with no diagnosis.
/// </summary>
public sealed record HostUpdateExecutionAvailability(
    HostUpdateExecutionAvailabilityState State,
    IReadOnlyList<string> Reasons,
    DateTimeOffset CheckedAt)
{
    public static HostUpdateExecutionAvailability Available(DateTimeOffset checkedAt) =>
        new(HostUpdateExecutionAvailabilityState.Available, [], checkedAt);

    public static HostUpdateExecutionAvailability Unavailable(DateTimeOffset checkedAt, IReadOnlyList<string> reasons) =>
        new(HostUpdateExecutionAvailabilityState.Unavailable, reasons, checkedAt);
}

/// <summary>Computes the current <see cref="HostUpdateExecutionAvailability"/> by positively probing every dependency.</summary>
public interface IHostUpdateExecutionAvailabilityProvider
{
    Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Probes the durable root's writability, the journal's integrity, that at least one migration
/// and one backup target are actually configured, that every configured compose file exists on
/// disk, and that the container runtime is reachable. This is deliberately a real filesystem/
/// process probe, not a static configuration-shape check (that is
/// <see cref="HostUpdateExecutionOptionsValidator"/>'s job at startup).
/// </summary>
public sealed class HostUpdateExecutionAvailabilityProvider(
    HostUpdateExecutionOptions options,
    IHostUpdateExecutionJournal journal,
    IReadOnlyList<IHostUpdateMigrationTarget> migrationTargets,
    IReadOnlyList<IHostUpdateBackupTarget> backupTargets,
    IReadOnlyList<IFenceableWriter> fenceableWriters,
    IHostUpdateProcessRunner processRunner,
    IHostUpdateRecoveryOutcomeStore recoveryOutcomeStore,
    IHostUpdateExecutableResolver executableResolver) : IHostUpdateExecutionAvailabilityProvider
{
    private const string ProbeReleaseId = "__availability_probe__";

    private static readonly string[] CodeOwnedUnavailableFacilities =
    [
        "target_image_migration_runner_unavailable",
        "queue_reconciliation_writer_fence_unavailable",
        "sql_server_visible_backup_path_mapping_unverified",
    ];

    public async Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            reasons.Add("root_directory_not_configured");
        }
        else if (!TryEnsureWritable(options.RootDirectory, out string? writeError))
        {
            reasons.Add($"root_directory_unwritable:{writeError}");
        }

        if (journal is IHostUpdateAvailability { IsAvailable: false } journalAvailability)
        {
            reasons.Add(journalAvailability.UnavailableReason);
        }
        else
        {
            try
            {
                // Harmless probe read: no release will ever legitimately use this id. A successful
                // (possibly empty) read proves the journal file, if any, is not corrupt.
                _ = journal.Read(ProbeReleaseId);
            }
            catch (InvalidDataException exception)
            {
                reasons.Add($"journal_corrupt:{exception.Message}");
            }
            catch (IOException exception)
            {
                reasons.Add($"journal_unreadable:{exception.Message}");
            }

            await ReconcileNonterminalReleasesAsync(reasons, cancellationToken).ConfigureAwait(false);
        }

        if (migrationTargets.Count == 0)
        {
            reasons.Add("no_migration_targets_configured");
        }

        if (backupTargets.Count == 0)
        {
            reasons.Add("no_backup_targets_configured");
        }

        foreach (string unavailableFacility in CodeOwnedUnavailableFacilities.Concat(options.RequiredUnavailableFacilities).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            reasons.Add($"facility_unavailable:{unavailableFacility}");
        }

        var fencedNames = new HashSet<string>(fenceableWriters.Select(w => w.Name), StringComparer.Ordinal);
        string[] missingWriters = options.RequiredFencedWriterNames
            .Where(name => !fencedNames.Contains(name))
            .ToArray();
        if (missingWriters.Length > 0)
        {
            reasons.Add($"insufficient_fenced_writers:{string.Join(',', missingWriters)}");
        }

        foreach (string composeFile in options.ComposeFiles)
        {
            if (!File.Exists(composeFile))
            {
                reasons.Add($"compose_file_missing:{composeFile}");
            }
        }

        var requiredTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "docker",
        };
        foreach (IHostUpdateMigrationTarget target in migrationTargets)
        {
            string providerName = await target.GetProviderNameAsync(cancellationToken).ConfigureAwait(false);
            switch (providerName)
            {
                case "Microsoft.EntityFrameworkCore.Sqlite":
                    requiredTools.Add("sqlite3");
                    break;
                case "Npgsql.EntityFrameworkCore.PostgreSQL":
                    requiredTools.Add("pg_dump");
                    requiredTools.Add("pg_restore");
                    break;
                case "Microsoft.EntityFrameworkCore.SqlServer":
                    requiredTools.Add("sqlcmd");
                    break;
                case "":
                    reasons.Add($"database_provider_not_configured:{target.ContextName}");
                    break;
                default:
                    reasons.Add($"database_provider_tooling_unsupported:{target.ContextName}:{providerName}");
                    break;
            }
        }

        foreach (string toolName in requiredTools)
        {
            if (!options.HostExecutablePaths.TryGetValue(toolName, out string? configuredPath) ||
                string.IsNullOrWhiteSpace(configuredPath) ||
                !Path.IsPathRooted(configuredPath))
            {
                reasons.Add($"host_executable_not_configured:{toolName}");
            }
        }

        try
        {
            HostUpdateProcessResult result = await processRunner.RunAsync(
                executableResolver.Resolve("docker"),
                ["version", "--format", "{{.Server.Version}}"],
                TimeSpan.FromSeconds(options.ProcessDefaultTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                reasons.Add("docker_runtime_unavailable");
            }
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("host_update_executable_", StringComparison.Ordinal))
        {
            const string dockerNotConfiguredReason = "host_executable_not_configured:docker";
            if (!reasons.Contains(dockerNotConfiguredReason, StringComparer.Ordinal))
            {
                reasons.Add(dockerNotConfiguredReason);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reasons.Add($"docker_runtime_unreachable:{exception.GetType().Name}");
        }

        return reasons.Count == 0
            ? HostUpdateExecutionAvailability.Available(now)
            : HostUpdateExecutionAvailability.Unavailable(now, reasons);
    }

    /// <summary>
    /// Restart reconciliation (Bishop/Hicks #2663 finding): <see cref="InMemoryHostUpdateAdmissionGate"/>
    /// and every in-process <see cref="IFenceableWriter"/> default back open on every process
    /// start, regardless of what the durable journal says. Without this, a process that crashed
    /// mid-update (or that crashed after reaching <see cref="HostUpdateExecutionState.RecoveryRequired"/>
    /// and before an operator resolved it) would silently admit new writes the moment the host
    /// restarted. This scans every release the journal has ever seen and, for any release whose
    /// last recorded state is neither <see cref="HostUpdateExecutionState.Completed"/> nor a
    /// durably confirmed <see cref="HostUpdateRecoveryOutcome.RolledBack"/> resolution, re-closes
    /// every registered writer immediately -- before this check ever reports Available -- and
    /// records why. It never resumes or retries the update itself; only an explicit operator
    /// call to the admin API's execute/recover endpoints can move a release forward again.
    /// </summary>
    private async Task ReconcileNonterminalReleasesAsync(List<string> reasons, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> releaseIds;
        try
        {
            releaseIds = journal.ListReleaseIds();
        }
        catch (InvalidDataException exception)
        {
            reasons.Add($"journal_corrupt:{exception.Message}");
            return;
        }

        foreach (string releaseId in releaseIds)
        {
            IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(releaseId);
            HostUpdateExecutionState? last = activities.Count == 0 ? null : activities[^1].State;
            if (last is null)
            {
                continue;
            }

            if (last == HostUpdateExecutionState.Completed &&
                activities.Any(a => a.State == HostUpdateExecutionState.Completed && string.Equals(a.Phase, "fence-release:after", StringComparison.Ordinal)))
            {
                continue;
            }

            if (last == HostUpdateExecutionState.RecoveryRequired)
            {
                HostUpdateRecoveryOutcomeRecord? outcome = await recoveryOutcomeStore.ReadAsync(releaseId, cancellationToken).ConfigureAwait(false);
                if (outcome is { Outcome: HostUpdateRecoveryOutcome.RolledBack })
                {
                    // Already durably resolved by a prior, confirmed recovery attempt -- a plain
                    // RolledBack record is only ever written after the admission fence was actually
                    // released, so there is nothing to re-fence for this specific release. A
                    // FenceReleasePending record deliberately does not match: that rollback's
                    // release never completed, so writers must stay fenced until it does.
                    continue;
                }
            }

            foreach (IFenceableWriter writer in fenceableWriters)
            {
                try
                {
                    await writer.QuiesceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    reasons.Add($"restart_reconciliation_fence_failed:{writer.Name}:{exception.GetType().Name}");
                }
            }

            reasons.Add($"restart_reconciliation_pending:{releaseId}:{last}");
        }
    }

    private static bool TryEnsureWritable(string rootDirectory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(rootDirectory);
            string probePath = Path.Combine(rootDirectory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = exception.GetType().Name;
            return false;
        }
    }
}

/// <summary>
/// Thread-safe holder for the most recently computed <see cref="HostUpdateExecutionAvailability"/>,
/// shared by the background recheck loop, the admin status endpoint, and the #2666 scheduler's
/// availability polling -- all without re-running the (filesystem/process) probe on every read.
/// </summary>
public sealed class HostUpdateExecutionAvailabilityHolder
{
    private readonly object _lock = new();
    private HostUpdateExecutionAvailability _current =
        HostUpdateExecutionAvailability.Unavailable(DateTimeOffset.UtcNow, ["not_yet_checked"]);

    public HostUpdateExecutionAvailability Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Update(HostUpdateExecutionAvailability availability)
    {
        lock (_lock)
        {
            _current = availability;
        }
    }
}

/// <summary>
/// Computes availability once immediately at startup (restart reconciliation: a fresh process
/// re-proves its own readiness rather than assuming a previous run's state still holds) and
/// then periodically thereafter, publishing every result into <see cref="HostUpdateExecutionAvailabilityHolder"/>.
/// Never executes or resumes an update itself -- this service only maintains status.
/// </summary>
public sealed class HostUpdateExecutionAvailabilityHostedService(
    IHostUpdateExecutionAvailabilityProvider provider,
    HostUpdateExecutionAvailabilityHolder holder,
    ILogger<HostUpdateExecutionAvailabilityHostedService> logger,
    TimeSpan recheckInterval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HostUpdateExecutionAvailability availability = await provider.CheckAsync(stoppingToken).ConfigureAwait(false);
            holder.Update(availability);
            if (availability.State == HostUpdateExecutionAvailabilityState.Unavailable)
            {
                logger.LogWarning(
                    "Host update executor is unavailable: {Reasons}",
                    string.Join(", ", availability.Reasons));
            }
            else
            {
                logger.LogInformation("Host update executor is available for manual operator use.");
            }

            try
            {
                await Task.Delay(recheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
