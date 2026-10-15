using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.ReleaseUpdates;

/// <summary>
/// Operator configuration for the farm-admin application release update alert (issue #3281).
/// Configuration-bound only (section <see cref="SectionName"/>); no persisted settings or
/// schema change. Disable it for air-gapped deployments that must not contact GitHub.
/// </summary>
public sealed class ApplicationReleaseUpdateOptions
{
    public const string SectionName = "ApplicationReleaseUpdates";

    /// <summary>Whether the periodic GitHub release check runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between GitHub release checks (5 minutes .. 24 hours). The default of
    /// six hours keeps the unauthenticated GitHub API usage negligible.</summary>
    public int IntervalSeconds { get; set; } = 21600;

    /// <summary>Timeout, in seconds, for each GitHub Releases request.</summary>
    public int HttpTimeoutSeconds { get; set; } = 15;
}

/// <summary>Startup validator for <see cref="ApplicationReleaseUpdateOptions"/>.</summary>
public sealed class ApplicationReleaseUpdateOptionsValidator : IValidateOptions<ApplicationReleaseUpdateOptions>
{
    public ValidateOptionsResult Validate(string? name, ApplicationReleaseUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.IntervalSeconds is < 300 or > 86400)
        {
            failures.Add($"{ApplicationReleaseUpdateOptions.SectionName}:IntervalSeconds must be between 300 and 86400.");
        }

        if (options.HttpTimeoutSeconds is < 5 or > 120)
        {
            failures.Add($"{ApplicationReleaseUpdateOptions.SectionName}:HttpTimeoutSeconds must be between 5 and 120.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
