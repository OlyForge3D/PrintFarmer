using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Serializes target-image migrations under the executor's existing ownership lock. Every target
/// is queried before any mutation so a target-image probe failure cannot leave a partial update.
/// </summary>
public sealed class HostUpdateMigrationCoordinator(IReadOnlyList<IHostUpdateMigrationTarget> targets)
    : IHostUpdateMigrationCoordinator, IHostUpdateMigrationReconciler
{
    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (IHostUpdateMigrationTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Every probe must succeed before any target-image validation/migration can mutate state.
            _ = await target.HasPendingMigrationsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        foreach (IHostUpdateMigrationTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await target.MigrateAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsReconciledAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (IHostUpdateMigrationTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await target.HasPendingMigrationsAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            catch (HostUpdateTargetImageMigrationException)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Runs the migration step of the host update executor.</summary>
public interface IHostUpdateMigrationCoordinator
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Proves whether a prior migration side effect already reached the requested target state.</summary>
public interface IHostUpdateMigrationReconciler
{
    Task<bool> IsReconciledAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts a live context to target-image migration execution. The live context is used only for
/// provider/connection inspection; migration code always runs in the signed target image.
/// </summary>
public sealed class TargetImageMigrationTarget<TContext>(
    string contextName,
    Func<TContext> resolveContext,
    HostUpdateTargetImageMigrationRunner runner) : IHostUpdateMigrationTarget
    where TContext : Microsoft.EntityFrameworkCore.DbContext
{
    public string ContextName { get; } = contextName;

    public Task<string> GetProviderNameAsync(CancellationToken cancellationToken)
    {
        TContext context = resolveContext();
        return Task.FromResult(context.Database.ProviderName ?? string.Empty);
    }

    public Task<string> GetConnectionStringFingerprintAsync(CancellationToken cancellationToken)
    {
        TContext context = resolveContext();
        string? connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            return Task.FromResult(string.Empty);
        }

        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString))).ToLowerInvariant();
        return Task.FromResult(fingerprint);
    }

    public Task<bool> HasPendingMigrationsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
        runner.HasPendingMigrationsAsync(request, ContextName, GetProviderNameAsync, cancellationToken);

    public Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
        runner.MigrateAsync(request, ContextName, GetProviderNameAsync, cancellationToken);

    public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken) =>
        throw new HostUpdateTargetImageMigrationException($"target_image_migration_request_required:{ContextName}");

    public Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken) =>
        throw new HostUpdateTargetImageMigrationException($"target_image_migration_request_required:{ContextName}");
}
