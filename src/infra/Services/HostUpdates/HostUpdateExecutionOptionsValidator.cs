using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Fails process start fast (via <c>ValidateOnStart()</c>) rather than letting the host-update
/// executor silently resolve its durable root under a temp directory, the process's current
/// directory, or a relative path that later moves when the working directory changes. This is
/// the single choke point that guarantees the executor never writes journal/lock/backup state
/// somewhere an OS/container temp-cleanup or an application-DB restore could destroy it.
/// </summary>
public sealed class HostUpdateExecutionOptionsValidator : IValidateOptions<HostUpdateExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, HostUpdateExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            failures.Add("HostUpdateExecution:RootDirectory is required (no default is provided).");
        }
        else
        {
            string root = options.RootDirectory;
            if (!Path.IsPathRooted(root))
            {
                failures.Add("HostUpdateExecution:RootDirectory must be an absolute path.");
            }
            else
            {
                string fullRoot = Path.GetFullPath(root);
                string tempRoot = Path.GetFullPath(Path.GetTempPath());
                string currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());

                if (IsWithin(fullRoot, tempRoot))
                {
                    failures.Add("HostUpdateExecution:RootDirectory must not be under the OS temp directory.");
                }

                if (string.Equals(fullRoot, currentDirectory, StringComparison.OrdinalIgnoreCase)
                    || IsWithin(fullRoot, currentDirectory))
                {
                    failures.Add("HostUpdateExecution:RootDirectory must not be the process's current/working directory or a subdirectory of it.");
                }
            }
        }

        if (options.SupportedProviderNames is null || options.SupportedProviderNames.Length == 0)
        {
            failures.Add("HostUpdateExecution:SupportedProviderNames must list at least one allowed EF Core provider.");
        }

        if (options.ComposeFiles is null || options.ComposeFiles.Length == 0)
        {
            failures.Add("HostUpdateExecution:ComposeFiles must list at least one compose file.");
        }

        if (options.ServiceMappings is null || options.ServiceMappings.Length == 0)
        {
            failures.Add("HostUpdateExecution:ServiceMappings must map at least one service.");
        }
        else
        {
            foreach (HostUpdateServiceMappingOptions mapping in options.ServiceMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ServiceId)
                    || string.IsNullOrWhiteSpace(mapping.ComposeServiceName)
                    || string.IsNullOrWhiteSpace(mapping.ImageEnvironmentVariable)
                    || string.IsNullOrWhiteSpace(mapping.ImageRepository))
                {
                    failures.Add($"HostUpdateExecution:ServiceMappings entry for '{mapping.ServiceId}' is missing a required field.");
                }
            }
        }

        if (options.MinimumFreeBytes <= 0)
        {
            failures.Add("HostUpdateExecution:MinimumFreeBytes must be positive.");
        }

        CheckPositive(options.DrainTimeoutSeconds, "DrainTimeoutSeconds", failures);
        CheckPositive(options.DrainPollIntervalSeconds, "DrainPollIntervalSeconds", failures);
        CheckPositive(options.FenceProofTimeoutSeconds, "FenceProofTimeoutSeconds", failures);
        CheckPositive(options.FencePollIntervalSeconds, "FencePollIntervalSeconds", failures);
        CheckPositive(options.BackupTimeoutSeconds, "BackupTimeoutSeconds", failures);
        CheckPositive(options.VerifyTimeoutSeconds, "VerifyTimeoutSeconds", failures);
        CheckPositive(options.VerifyPollIntervalSeconds, "VerifyPollIntervalSeconds", failures);
        CheckPositive(options.ApplyTimeoutSeconds, "ApplyTimeoutSeconds", failures);
        CheckPositive(options.ProcessDefaultTimeoutSeconds, "ProcessDefaultTimeoutSeconds", failures);

        if (string.IsNullOrWhiteSpace(options.HealthCheckBaseUrl) || !Uri.TryCreate(options.HealthCheckBaseUrl, UriKind.Absolute, out _))
        {
            failures.Add("HostUpdateExecution:HealthCheckBaseUrl must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ComposeProjectName))
        {
            failures.Add("HostUpdateExecution:ComposeProjectName is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckPositive(int value, string fieldName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"HostUpdateExecution:{fieldName} must be positive.");
        }
    }

    private static bool IsWithin(string candidate, string ancestor)
    {
        string normalizedAncestor = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase);
    }
}
