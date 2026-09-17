using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Serializes provider migrations for every registered context under the same execution
/// ownership (the caller''s process lock), in manifest order, coordinating with the existing
/// <c>ProviderAwareMigrationRunner</c> startup behavior. Never runs contexts concurrently, so
/// there is never a mixed old/new writer window across contexts.
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
            _ = await target.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsReconciledAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (IHostUpdateMigrationTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await target.HasPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
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
/// Adapts a live <see cref="Microsoft.EntityFrameworkCore.DbContext"/> to <see cref="IHostUpdateMigrationTarget"/>,
/// delegating to the existing <c>ProviderAwareMigrationRunner</c> rather than duplicating its logic.
/// </summary>
public sealed class DbContextMigrationTarget<TContext>(
    string contextName,
    Farm.Infrastructure.Data.Migrations.DatabaseMigrationTarget migrationTarget,
    Func<TContext> resolveContext,
    Microsoft.Extensions.Logging.ILogger logger) : IHostUpdateMigrationTarget
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

    public async Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        TContext context = resolveContext();
        return (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any();
    }

    public async Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        TContext context = resolveContext();
        return await Farm.Infrastructure.Data.Migrations.ProviderAwareMigrationRunner.MigrateAsync(
            context,
            migrationTarget,
            logger,
            cancellationToken).ConfigureAwait(false);
    }
}
