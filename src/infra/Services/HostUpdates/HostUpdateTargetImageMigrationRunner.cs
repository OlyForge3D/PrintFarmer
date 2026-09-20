using Farm.Infrastructure.Data.Migrations;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Fails closed when a target image cannot prove or apply its EF migrations.</summary>
#pragma warning disable CA1032 // This exception is constructed only with machine-readable failure codes.
public sealed class HostUpdateTargetImageMigrationException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
#pragma warning restore CA1032

/// <summary>
/// Runs the target image's migration command through the constrained Docker process boundary.
/// The target image is selected only from the signed request's platform digest; no local
/// assembly or mutable image tag participates in forward migration.
/// </summary>
public sealed class HostUpdateTargetImageMigrationRunner(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    IReadOnlyDictionary<string, HostUpdateApplyServiceMapping> serviceMappings,
    TimeSpan timeout)
{
    private static readonly Dictionary<string, string> ContextServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppDbContext"] = "api",
            ["SlicerDbContext"] = "slicer-host",
        };

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.Ordinal)
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.SqlServer",
    };

    public async Task<bool> HasPendingMigrationsAsync(
        HostUpdateExecutionRequest request,
        string contextName,
        Func<CancellationToken, Task<string>> getProviderName,
        CancellationToken cancellationToken)
    {
        HostUpdateProcessResult result = await RunAsync(request, contextName, "probe", getProviderName, cancellationToken).ConfigureAwait(false);
        string expected = $"HOST_UPDATE_MIGRATION_PENDING:{contextName}:";
        string[] lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? marker = lines.SingleOrDefault(line => line.StartsWith(expected, StringComparison.Ordinal));
        if (string.Equals(marker, expected + "0", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(marker, expected + "1", StringComparison.Ordinal))
        {
            return true;
        }

        throw new HostUpdateTargetImageMigrationException($"target_image_migration_probe_invalid:{contextName}");
    }

    public async Task<DatabaseMigrationResult> MigrateAsync(
        HostUpdateExecutionRequest request,
        string contextName,
        Func<CancellationToken, Task<string>> getProviderName,
        CancellationToken cancellationToken)
    {
        HostUpdateProcessResult result = await RunAsync(request, contextName, "apply", getProviderName, cancellationToken).ConfigureAwait(false);
        string marker = $"HOST_UPDATE_MIGRATION_APPLIED:{contextName}";
        if (!result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(marker, StringComparer.Ordinal))
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_apply_invalid:{contextName}");
        }

        return new DatabaseMigrationResult(false, []);
    }

    private async Task<HostUpdateProcessResult> RunAsync(
        HostUpdateExecutionRequest request,
        string contextName,
        string operation,
        Func<CancellationToken, Task<string>> getProviderName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ContextServices.TryGetValue(contextName, out string? serviceId) ||
            !serviceMappings.TryGetValue(serviceId, out HostUpdateApplyServiceMapping? mapping))
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_context_unsupported:{contextName}");
        }

        string providerName;
        try
        {
            providerName = await getProviderName(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_provider_inspection_failed:{contextName}");
        }

        if (!SupportedProviders.Contains(providerName))
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_provider_unsupported:{contextName}:{providerName}");
        }

        HostUpdateExecutionTarget? target = request.Targets.SingleOrDefault(candidate => string.Equals(candidate.ServiceId, serviceId, StringComparison.Ordinal));
        if (target is null)
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_target_missing:{contextName}");
        }

        string image = $"{mapping.ImageRepository}@{target.ChildDigest}";
        IReadOnlyList<string> arguments =
        [
            "run", "--rm", "--network", "host",
            "--entrypoint", "dotnet",
            image,
            serviceId == "api" ? "Farm.Web.Api.dll" : "Farm.Slicer.Host.dll",
            "--host-update-migration", contextName, operation,
        ];
        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                executableResolver.Resolve("docker"),
                arguments,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HostUpdateTargetImageMigrationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_execution_failed:{contextName}");
        }

        if (!result.Succeeded)
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_execution_failed:{contextName}:exit={result.ExitCode}");
        }

        return result;
    }
}
