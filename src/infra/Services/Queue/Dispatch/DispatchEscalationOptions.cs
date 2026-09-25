using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Escalation levels for an unresolved indeterminate pre-start claim (issue #2859).
/// Escalation is notification-only: no level ever releases the claim.
/// </summary>
public enum DispatchEscalationLevel
{
    /// <summary>No indeterminate claim is held.</summary>
    None = 0,

    /// <summary>Immediate warning shown as soon as the claim becomes indeterminate.</summary>
    Warning = 1,

    /// <summary>Operational escalation notification (default 15 minutes).</summary>
    Operational = 2,

    /// <summary>Overdue critical alert (default 1 hour).</summary>
    Critical = 3,

    /// <summary>Hard maximum-age boundary (default 24 hours). The claim is still retained.</summary>
    HardLimit = 4,
}

/// <summary>
/// Versioned, validated time-to-live escalation policy for indeterminate claims. Thresholds are
/// measured from <c>QueueDispatchAttempt.ClaimedAtUtc</c>, must be positive and strictly
/// increasing, and are keyed by <see cref="PolicyRevision"/> for durable deduplication.
/// </summary>
public sealed class DispatchEscalationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Queue:DispatchEscalation";

    /// <summary>Version of this threshold policy. Bump whenever thresholds change.</summary>
    public int PolicyRevision { get; set; } = 1;

    /// <summary>Claim age at which the operational escalation is raised.</summary>
    public TimeSpan OperationalAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Claim age at which the critical overdue alert is raised.</summary>
    public TimeSpan CriticalAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Claim age at which the hard maximum-age boundary is raised.</summary>
    public TimeSpan HardLimitAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the escalation worker scans unresolved claims.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Returns the highest escalation level due for a claim of <paramref name="claimAge"/>.</summary>
    public DispatchEscalationLevel Resolve(TimeSpan claimAge)
    {
        if (claimAge >= HardLimitAfter)
        {
            return DispatchEscalationLevel.HardLimit;
        }

        if (claimAge >= CriticalAfter)
        {
            return DispatchEscalationLevel.Critical;
        }

        return claimAge >= OperationalAfter
            ? DispatchEscalationLevel.Operational
            : DispatchEscalationLevel.Warning;
    }

    /// <summary>Returns every level due for a claim of <paramref name="claimAge"/>, in order.</summary>
    public IEnumerable<DispatchEscalationLevel> DueLevels(TimeSpan claimAge)
    {
        DispatchEscalationLevel highest = Resolve(claimAge);
        for (DispatchEscalationLevel level = DispatchEscalationLevel.Warning; level <= highest; level++)
        {
            yield return level;
        }
    }
}

/// <summary>Validates <see cref="DispatchEscalationOptions"/> at startup.</summary>
public sealed class DispatchEscalationOptionsValidator : IValidateOptions<DispatchEscalationOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DispatchEscalationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        if (options.PolicyRevision < 1)
        {
            failures.Add("PolicyRevision must be at least 1.");
        }

        if (options.OperationalAfter <= TimeSpan.Zero)
        {
            failures.Add("OperationalAfter must be positive.");
        }

        if (options.CriticalAfter <= options.OperationalAfter)
        {
            failures.Add("CriticalAfter must be greater than OperationalAfter.");
        }

        if (options.HardLimitAfter <= options.CriticalAfter)
        {
            failures.Add("HardLimitAfter must be greater than CriticalAfter.");
        }

        if (options.ScanInterval <= TimeSpan.Zero)
        {
            failures.Add("ScanInterval must be positive.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
