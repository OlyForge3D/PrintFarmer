using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates.PullApproval;

/// <summary>Current API-side inputs for a readiness query.</summary>
public sealed record HostUpdatePullReadinessContext
{
    public HostUpdateDaemonKeyRecord? Enrollment { get; init; }

    public string? StrictInstallationId { get; init; }

    public required bool KillSwitchActive { get; init; }

    public required HostUpdateMaintenanceWindowReadinessDto MaintenanceWindow { get; init; }

    public required HostUpdateReadinessConditionDto PrinterActivity { get; init; }

    public required HostUpdateReadinessConditionDto RecoveryEvidence { get; init; }

    /// <summary>False when the automation policy could not be read authoritatively.</summary>
    public required bool PolicyReadable { get; init; }

    public required string Channel { get; init; }

    public required long PolicyRevision { get; init; }

    public required long TrustRevision { get; init; }

    public required bool AutomaticPolicyEnabled { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// Builds the readiness payload. Readiness never grants execution; <c>Unknown</c> conditions count as
/// not satisfied, and an unknown, non-active or mismatched host identity is never eligible.
/// </summary>
public static class HostUpdatePullReadinessEvaluator
{
    public static HostUpdateReadinessDto Evaluate(HostUpdatePullReadinessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var identityReasons = new List<string>();
        HostUpdatePullApprovalEvaluator.AddIdentityReasons(context.Enrollment, context.StrictInstallationId, context.Now, identityReasons);
        var hostEligibility = new HostUpdateReadinessConditionDto
        {
            State = identityReasons.Count == 0 ? HostUpdateReadinessState.Satisfied : HostUpdateReadinessState.NotSatisfied,
            Reasons = identityReasons
        };

        var policyReasons = new List<string>();
        if (!context.PolicyReadable)
        {
            policyReasons.Add("policy_unavailable");
        }

        if (!HostUpdatePullIdentifiers.IsChannel(context.Channel))
        {
            policyReasons.Add(HostUpdatePullReasons.ChannelMismatch);
        }

        var policy = new HostUpdatePolicyReadinessDto
        {
            State = !context.PolicyReadable
                ? HostUpdateReadinessState.Unknown
                : policyReasons.Count == 0 ? HostUpdateReadinessState.Satisfied : HostUpdateReadinessState.NotSatisfied,
            Channel = context.Channel,
            PolicyRevision = context.PolicyRevision,
            TrustRevision = context.TrustRevision,
            AutomaticPolicyEnabled = context.AutomaticPolicyEnabled,
            AutomaticUpdatesRuntimeEnabled = HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled,
            Reasons = policyReasons
        };

        var reasons = new List<string>();
        AddIfNotSatisfied(context.MaintenanceWindow.State, "maintenance_window_not_open", reasons);
        AddIfNotSatisfied(context.PrinterActivity.State, "printer_activity_blocking", reasons);
        AddIfNotSatisfied(policy.State, "policy_not_ready", reasons);
        AddIfNotSatisfied(context.RecoveryEvidence.State, HostUpdatePullReasons.RecoveryPrerequisitesMissing, reasons);
        AddIfNotSatisfied(hostEligibility.State, "host_not_eligible", reasons);
        if (context.KillSwitchActive)
        {
            reasons.Add(HostUpdatePullReasons.KillSwitchActive);
        }

        return new HostUpdateReadinessDto
        {
            Eligible = reasons.Count == 0,
            MaintenanceWindow = context.MaintenanceWindow,
            PrinterActivity = context.PrinterActivity,
            Policy = policy,
            RecoveryEvidence = context.RecoveryEvidence,
            HostEligibility = hostEligibility,
            KillSwitchActive = context.KillSwitchActive,
            Reasons = reasons
        };
    }

    private static void AddIfNotSatisfied(HostUpdateReadinessState state, string reason, List<string> reasons)
    {
        if (state != HostUpdateReadinessState.Satisfied)
        {
            reasons.Add(reason);
        }
    }
}

/// <summary>
/// Validates daemon status reports. Redaction is enforced by shape: every free-form member must be a
/// bounded reason code or canonical identifier, so credentials, enrollment codes, key material,
/// host paths, URLs and raw errors cannot be accepted or stored. Invalid reports are rejected, not
/// sanitized.
/// </summary>
public static class HostUpdateDaemonStatusReportValidator
{
    public static IReadOnlyList<string> Validate(HostUpdateDaemonStatusReportDto? report, DateTimeOffset now)
    {
        if (report is null)
        {
            return ["status_report_missing"];
        }

        var errors = new List<string>();
        if (!Enum.IsDefined(report.DaemonState))
        {
            errors.Add("daemonState");
        }

        if (!Enum.IsDefined(report.ExecutionMode))
        {
            errors.Add("executionMode");
        }

        if (report.HostPolicyRevision < 0)
        {
            errors.Add("hostPolicyRevision");
        }

        if (report.CurrentCheckpoint is not null && !HostUpdatePullIdentifiers.IsReasonCode(report.CurrentCheckpoint))
        {
            errors.Add("currentCheckpoint");
        }

        ValidateCodes(report.DeferReasons, "deferReasons", errors);
        ValidateCodes(report.RecoveryHints, "recoveryHints", errors);

        if ((now - report.ReportedAt).Duration() > HostUpdateDaemonProtocol.ClockSkew)
        {
            errors.Add("reportedAt");
        }

        HostUpdateDaemonLastResultDto? last = report.LastResult;
        if (last is null)
        {
            errors.Add("lastResult");
            return errors;
        }

        if (!Enum.IsDefined(last.Outcome))
        {
            errors.Add("lastResult.outcome");
        }

        if (last.ApprovalId is not null && !HostUpdatePullIdentifiers.IsOpaqueId(last.ApprovalId))
        {
            errors.Add("lastResult.approvalId");
        }

        if (last.ReleaseId is not null && !HostUpdatePullIdentifiers.IsReleaseId(last.ReleaseId))
        {
            errors.Add("lastResult.releaseId");
        }

        if (last.ReasonCode is not null && !HostUpdatePullIdentifiers.IsReasonCode(last.ReasonCode))
        {
            errors.Add("lastResult.reasonCode");
        }

        if (last.CompletedAt is { } completed && completed > now + HostUpdateDaemonProtocol.ClockSkew)
        {
            errors.Add("lastResult.completedAt");
        }

        return errors;
    }

    private static void ValidateCodes(IReadOnlyList<string>? codes, string field, List<string> errors)
    {
        if (codes is null || codes.Count > HostUpdateDaemonProtocol.MaxListItems || !codes.All(HostUpdatePullIdentifiers.IsReasonCode))
        {
            errors.Add(field);
        }
    }
}
