using System.Collections.Frozen;
using System.Text.Json;
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
    Func<IReadOnlyDictionary<string, string>> migrationEnvironmentFactory,
    string composeNetwork,
    TimeSpan timeout)
{
    private static readonly Dictionary<string, string> ContextServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppDbContext"] = "api",
            ["SlicerDbContext"] = "slicer-host",
        };

    public static IReadOnlySet<string> SupportedProviders { get; } =
        FrozenSet.ToFrozenSet(
            ["Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.EntityFrameworkCore.SqlServer"],
            StringComparer.Ordinal);

    public async Task<bool> HasPendingMigrationsAsync(
        HostUpdateExecutionRequest request,
        string contextName,
        Func<CancellationToken, Task<string>> getProviderName,
        CancellationToken cancellationToken)
    {
        HostUpdateProcessResult result = await RunAsync(request, contextName, "probe", getProviderName, cancellationToken).ConfigureAwait(false);
        string expected = $"HOST_UPDATE_MIGRATION_PENDING:{contextName}:";
        string[] lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] markers = lines.Where(line => line.StartsWith(expected, StringComparison.Ordinal)).ToArray();
        string? marker = markers.Length == 1 ? markers[0] : null;
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
        string markerPrefix = $"HOST_UPDATE_MIGRATION_APPLIED:{contextName}:";
        string[] markers = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(markerPrefix, StringComparison.Ordinal))
            .ToArray();
        if (markers.Length != 1)
        {
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_apply_invalid:{contextName}");
        }

        string[] migrations = markers[0][markerPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new DatabaseMigrationResult(false, migrations);
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

        string dockerPlatform = target.Platform switch
        {
            "linux-amd64" => "linux/amd64",
            "linux-arm64" => "linux/arm64",
            _ => throw new HostUpdateTargetImageMigrationException($"target_image_migration_platform_unsupported:{contextName}:{target.Platform}"),
        };
        string image = $"{mapping.ImageRepository}@{target.ChildDigest}";
        IReadOnlyDictionary<string, string> migrationEnvironment;
        try
        {
            migrationEnvironment = migrationEnvironmentFactory();
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith("target_image_migration_configuration_missing:", StringComparison.Ordinal))
        {
            throw new HostUpdateTargetImageMigrationException(exception.Message);
        }

        string dockerPath;
        try
        {
            dockerPath = executableResolver.Resolve("docker");
            if (request.ImageSourceMode == HostUpdateImageSourceMode.PreloadedLocal)
            {
                HostUpdateProcessResult inspectResult = await processRunner.RunAsync(
                    dockerPath,
                    ["image", "inspect", image, "--format", "{{json .}}"],
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                if (!inspectResult.Succeeded || !IsExpectedLocalImage(inspectResult.StandardOutput, image, target.Platform))
                {
                    throw new HostUpdateTargetImageMigrationException($"target_image_migration_stage_failed:{contextName}:preloaded_unverified");
                }
            }
            else
            {
                var pullArguments = new List<string> { "image", "pull", "--platform", dockerPlatform, image };
                HostUpdateProcessResult pullResult = await processRunner.RunAsync(
                    dockerPath,
                    pullArguments,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                if (!pullResult.Succeeded)
                {
                    throw new HostUpdateTargetImageMigrationException($"target_image_migration_stage_failed:{contextName}:exit={pullResult.ExitCode}");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is HostUpdateTargetImageMigrationException)
            {
                throw;
            }

            throw new HostUpdateTargetImageMigrationException($"target_image_migration_stage_failed:{contextName}:{exception.GetType().Name}");
        }

        var arguments = new List<string>
        {
            "run", "--rm", "--pull", "never", "--platform", dockerPlatform,
            "--network", composeNetwork,
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--user", "appuser", "--read-only",
#pragma warning disable S5443 // This target container mounts only an ephemeral, non-executable tmpfs.
            "--tmpfs", "/tmp:rw,noexec,nosuid",
#pragma warning restore S5443
            "--entrypoint", "dotnet",
        };
        foreach (string environmentName in migrationEnvironment.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            arguments.Add("--env");
            arguments.Add(environmentName);
        }

        arguments.AddRange(
        [
            image,
            serviceId == "api" ? "Farm.Web.Api.dll" : "Farm.Slicer.Host.dll",
            "--host-update-migration", contextName, operation, providerName,
        ]);
        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                dockerPath,
                arguments,
                timeout,
                cancellationToken,
                migrationEnvironment).ConfigureAwait(false);
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
            string diagnostic = result.StandardError
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith($"HOST_UPDATE_MIGRATION_ERROR:{contextName}:", StringComparison.Ordinal))
                ?? "no_diagnostic";
            throw new HostUpdateTargetImageMigrationException($"target_image_migration_execution_failed:{contextName}:exit={result.ExitCode}:{diagnostic}");
        }

        return result;
    }

    private static bool IsExpectedLocalImage(string standardOutput, string imageReference, string platform)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                root = root.GetArrayLength() == 1 ? root[0] : default;
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("RepoDigests", out JsonElement repoDigests) ||
                repoDigests.ValueKind != JsonValueKind.Array ||
                !repoDigests.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), imageReference, StringComparison.Ordinal)))
            {
                return false;
            }

            string[] platformParts = platform.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string expectedVariant = platformParts.Length == 3 ? platformParts[2] : string.Empty;
            string actualVariant = root.TryGetProperty("Variant", out JsonElement variant) ? variant.GetString() ?? string.Empty : string.Empty;
            return platformParts.Length is 2 or 3 &&
                root.TryGetProperty("Os", out JsonElement os) &&
                root.TryGetProperty("Architecture", out JsonElement architecture) &&
                string.Equals(os.GetString(), platformParts[0], StringComparison.Ordinal) &&
                string.Equals(architecture.GetString(), platformParts[1], StringComparison.Ordinal) &&
                (string.Equals(actualVariant, expectedVariant, StringComparison.Ordinal) ||
                 (string.Equals(platformParts[1], "arm64", StringComparison.Ordinal) &&
                  string.IsNullOrEmpty(expectedVariant) &&
                  string.Equals(actualVariant, "v8", StringComparison.Ordinal)));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
