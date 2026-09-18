namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Composes the concrete preflight/drain/fence/backup/migrate/apply/verify adapters into the
/// <see cref="IHostUpdateExecutionSteps"/> contract consumed by <see cref="HostUpdateExecutor"/>.
/// This is the production adapter layer described in <c>docs/HOST_UPDATE_EXECUTOR.md</c> as
/// still required by the safe-executor foundation.
/// </summary>
public sealed class HostUpdateExecutionStepsAdapter(
    IHostUpdatePreflightCheck preflight,
    IHostUpdateDrainCoordinator drain,
    IHostUpdateFenceCoordinator fence,
    IHostUpdateBackupCoordinator backup,
    IHostUpdateMigrationCoordinator migration,
    IHostUpdateApplyCoordinator apply,
    IHostUpdateHealthVerifier verify,
    IInstalledHostStateStore installedStateStore) : IHostUpdateExecutionSteps
{
    public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        preflight.RunAsync(request, ct);

    public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        drain.RunAsync(request, ct);

    public Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        fence.RunAsync(request, ct);

    public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        backup.RunAsync(request, ct);

    public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        migration.RunAsync(request, ct);

    public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) =>
        apply.RunAsync(request, ct);

    public async Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct)
    {
        await verify.RunAsync(request, ct).ConfigureAwait(false);

        // Only after every readiness signal (including exact running digests) has verified
        // healthy do we persist this as the new installed state and reopen writers.
        var serviceDigests = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
        var servicePlatforms = request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal);
        await installedStateStore.WriteAsync(
            new InstalledHostState(request.ReleaseId, request.ManifestDigest, serviceDigests, Topology(request), DateTimeOffset.UtcNow, servicePlatforms),
            ct).ConfigureAwait(false);
    }

    public Task ReleaseFenceAsync(CancellationToken cancellationToken) => fence.ReleaseAsync(cancellationToken);

    private static string Topology(HostUpdateExecutionRequest request) =>
        string.Join('+', request.Targets.Select(t => t.ServiceId).OrderBy(id => id, StringComparer.Ordinal));
}
