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
    IHostUpdateProcessRunner processRunner) : IHostUpdateExecutionAvailabilityProvider
{
    private const string ProbeReleaseId = "__availability_probe__";

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

        if (migrationTargets.Count == 0)
        {
            reasons.Add("no_migration_targets_configured");
        }

        if (backupTargets.Count == 0)
        {
            reasons.Add("no_backup_targets_configured");
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

        try
        {
            HostUpdateProcessResult result = await processRunner.RunAsync(
                "docker",
                ["version", "--format", "{{.Server.Version}}"],
                TimeSpan.FromSeconds(options.ProcessDefaultTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                reasons.Add("docker_runtime_unavailable");
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
