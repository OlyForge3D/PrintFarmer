using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Host-local, API-independent status and recovery entry point (issue #2980, first slice).
/// Reuses the shared engine registration, the journal-binding resolver, the execution lock and
/// the recovery coordinator; it adds no second executor and accepts no replacement release
/// material, arbitrary paths, shell text, or credentials on the command line.
/// </summary>
public static partial class HostUpdateCli
{
    public const string Usage = """
        Usage:
          printfarmer-host-update status [--release <releaseId>] [--json]
          printfarmer-host-update recover --release <releaseId> [--request-id <requestId>] --preview [--json]
          printfarmer-host-update recover --release <releaseId> [--request-id <requestId>] --confirm <releaseId> [--json]

        Configuration comes from --config <absolute-json-path> and environment variables
        (HostUpdateExecution__*, DB_PROVIDER, ConnectionStrings__Default). Credentials are never
        accepted as arguments. This tool is not rollout authorization.

        Exit codes: 0 ok, 2 usage, 3 configuration/namespace unproven, 4 state unreadable,
        5 no history, 6 refused, 7 lock held, 10 needs operator, 11 fence release pending.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!HostUpdateCliArguments.TryParse(args, out HostUpdateCliArguments? parsed, out string? usageError))
        {
            await error.WriteLineAsync(usageError).ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return HostUpdateCliExitCodes.Usage;
        }

        if (parsed!.Command == HostUpdateCliCommand.Help)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return HostUpdateCliExitCodes.Success;
        }

        ServiceProvider provider;
        HostUpdateExecutionOptions options;
        try
        {
            provider = BuildServices(configuration, error);
            options = provider.GetRequiredService<HostUpdateExecutionOptions>();
        }
        catch (OptionsValidationException exception)
        {
            return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("configuration_invalid", exception.Failures.ToArray())).ConfigureAwait(false);
        }

        await using (provider.ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Directory.Exists(options.RootDirectory) || !Directory.Exists(options.StateDirectory))
            {
                return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("root_directory_not_visible", [])).ConfigureAwait(false);
            }

            return parsed.Command == HostUpdateCliCommand.Status
                ? await StatusAsync(provider, parsed, output, cancellationToken).ConfigureAwait(false)
                : await RecoverAsync(provider, configuration, options, parsed, output, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static ServiceProvider BuildServices(IConfiguration configuration, TextWriter error)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddProvider(new HostUpdateCliLoggerProvider(error)));
        services.AddHostUpdateRecoveryEngine(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<int> StatusAsync(IServiceProvider provider, HostUpdateCliArguments args, TextWriter output, CancellationToken cancellationToken)
    {
        IHostUpdateAdmissionGate gate = provider.GetRequiredService<IHostUpdateAdmissionGate>();
        bool admissionClosed = await gate.IsClosedAsync(cancellationToken).ConfigureAwait(false);

        // Journal reads run only while holding the execution lock so a status probe can never
        // race a live executor's staged rewrite. A held lock is reported, never waited on.
        IHostUpdateExecutionLease? lease = TryAcquireLock(provider);
        if (lease is null)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new StatusReport(true, admissionClosed, null, null)).ConfigureAwait(false);
        }

        using (lease)
        {
            IHostUpdateExecutionJournal journal = provider.GetRequiredService<IHostUpdateExecutionJournal>();
            try
            {
                if (args.ReleaseId is null)
                {
                    ReleaseSummary[] releases = [.. journal.ListReleaseIds().Select(id =>
                    {
                        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(id);
                        return new ReleaseSummary(id, activities[^1].State, activities[^1].Phase, activities[^1].RecordedAt);
                    })];
                    return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.Success, new StatusReport(false, admissionClosed, releases, null)).ConfigureAwait(false);
                }

                IReadOnlyList<HostUpdateExecutionActivity> history = journal.Read(args.ReleaseId);
                if (history.Count == 0)
                {
                    return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.NoHistory, new CliFailure(HostUpdateRecoveryRequestResolver.NoHistory, [])).ConfigureAwait(false);
                }

                HostUpdateRecoveryOutcomeRecord? outcome = await provider.GetRequiredService<IHostUpdateRecoveryOutcomeStore>()
                    .ReadAsync(args.ReleaseId, cancellationToken).ConfigureAwait(false);
                HostUpdateExecutionActivity last = history[^1];
                var detail = new ReleaseDetail(
                    args.ReleaseId,
                    last.State,
                    last.Phase,
                    last.RecordedAt,
                    last.RequestBinding?.RequestId,
                    FindUncertainPhases(history),
                    outcome is null ? null : new OutcomeSummary(outcome.Outcome, outcome.Detail, outcome.RecordedAt),
                    [.. history.Select(a => new ActivitySummary(a.State, a.Phase, a.RecordedAt))]);
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.Success, new StatusReport(false, admissionClosed, null, detail)).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStateFailure(exception))
            {
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> RecoverAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateExecutionOptions options,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> proofFailures = HostUpdateNamespaceProof.Check(
            options,
            DatabaseProviderConfiguration.FromConfiguration(configuration),
            provider.GetRequiredService<IHostUpdateExecutableResolver>());
        if (args.Confirm && proofFailures.Count > 0)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("namespace_unproven", [.. proofFailures])).ConfigureAwait(false);
        }

        IReadOnlyList<HostUpdateExecutionActivity> activities;
        IHostUpdateExecutionLease? lease = TryAcquireLock(provider);
        if (lease is null)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new CliFailure("host_update_lock_held", [])).ConfigureAwait(false);
        }

        using (lease)
        {
            try
            {
                activities = provider.GetRequiredService<IHostUpdateExecutionJournal>().Read(args.ReleaseId!);
            }
            catch (Exception exception) when (IsStateFailure(exception))
            {
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
            }
        }

        HostUpdateRecoveryRequestResolution resolution = HostUpdateRecoveryRequestResolver.Resolve(activities, args.RequestId);
        if (!resolution.Succeeded)
        {
            int code = resolution.ErrorCode == HostUpdateRecoveryRequestResolver.NoHistory ? HostUpdateCliExitCodes.NoHistory : HostUpdateCliExitCodes.Refused;
            return await EmitAsync(output, args.Json, code, new CliFailure(resolution.ErrorCode!, [])).ConfigureAwait(false);
        }

        using IServiceScope scope = provider.CreateScope();
        try
        {
            if (!args.Confirm)
            {
                HostUpdateRecoveryPlan plan = await scope.ServiceProvider.GetRequiredService<IHostUpdateRecoveryPlanner>()
                    .PlanAsync(resolution.Request!, activities, cancellationToken).ConfigureAwait(false);
                int planCode = plan.Kind == HostUpdateRecoveryPlanKind.NeedsOperator ? HostUpdateCliExitCodes.NeedsOperator : HostUpdateCliExitCodes.Success;
                return await EmitAsync(output, args.Json, planCode, new PreviewReport(args.ReleaseId!, resolution.Request!.RequestId, plan, proofFailures)).ConfigureAwait(false);
            }

            HostUpdateRecoveryResult result = await scope.ServiceProvider.GetRequiredService<IHostUpdateRecoveryCoordinator>()
                .RecoverAsync(resolution.Request!, activities, cancellationToken).ConfigureAwait(false);
            int resultCode = HostUpdateAvailabilityCodes.IsDurableUnavailable(result.Detail)
                ? HostUpdateCliExitCodes.StateUnreadable
                : result.Outcome switch
                {
                    HostUpdateRecoveryOutcome.RolledBack => HostUpdateCliExitCodes.Success,
                    HostUpdateRecoveryOutcome.FenceReleasePending => HostUpdateCliExitCodes.FenceReleasePending,
                    _ => HostUpdateCliExitCodes.NeedsOperator,
                };
            return await EmitAsync(output, args.Json, resultCode, new RecoveryReport(args.ReleaseId!, resolution.Request!.RequestId, result.Outcome, result.Detail)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new CliFailure("host_update_lock_held", [])).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The coordinator has already durably recorded NeedsOperator recovery_canceled.
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.NeedsOperator, new CliFailure("recovery_canceled", [])).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStateFailure(exception))
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
        }
    }

    private static IHostUpdateExecutionLease? TryAcquireLock(IServiceProvider provider)
    {
        try
        {
            return provider.GetRequiredService<IHostUpdateExecutionLock>().Acquire(TimeSpan.Zero, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static bool IsStateFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException
            or HostUpdateSubsystemUnavailableException or NotSupportedException or System.Security.SecurityException;

    private static string StateFailureCode(Exception exception) => exception switch
    {
        InvalidDataException data when !string.IsNullOrWhiteSpace(data.Message) && JournalCode().IsMatch(data.Message) => data.Message,
        HostUpdateSubsystemUnavailableException => "state_unavailable",
        UnauthorizedAccessException => "state_access_denied",
        _ => "state_unreadable:" + exception.GetType().Name,
    };

    /// <summary>Phases whose <c>:before</c> marker has no matching <c>:after</c>: side effects that may or may not have happened.</summary>
    internal static string[] FindUncertainPhases(IReadOnlyList<HostUpdateExecutionActivity> history)
    {
        var completed = history.Select(a => a.Phase)
            .Where(p => p.EndsWith(":after", StringComparison.Ordinal))
            .Select(p => p[..^":after".Length])
            .ToHashSet(StringComparer.Ordinal);
        return [.. history.Select(a => a.Phase)
            .Where(p => p.EndsWith(":before", StringComparison.Ordinal))
            .Select(p => p[..^":before".Length])
            .Where(p => !completed.Contains(p))
            .Distinct(StringComparer.Ordinal)];
    }

    private static async Task<int> EmitAsync(TextWriter output, bool json, int exitCode, object report)
    {
        if (json)
        {
            var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exitCode"] = exitCode,
                ["result"] = report,
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
            return exitCode;
        }

        await output.WriteLineAsync($"exitCode: {exitCode}").ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
        await WriteTextAsync(output, document.RootElement, string.Empty).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task WriteTextAsync(TextWriter output, JsonElement element, string prefix)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string key = prefix + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    await WriteTextAsync(output, property.Value, key + ".").ConfigureAwait(false);
                    break;
                case JsonValueKind.Array:
                    int index = 0;
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            await WriteTextAsync(output, item, $"{key}[{index}].").ConfigureAwait(false);
                        }
                        else
                        {
                            await output.WriteLineAsync($"{key}[{index}]: {item}").ConfigureAwait(false);
                        }

                        index++;
                    }

                    if (index == 0)
                    {
                        await output.WriteLineAsync($"{key}: (none)").ConfigureAwait(false);
                    }

                    break;
                default:
                    await output.WriteLineAsync($"{key}: {property.Value}").ConfigureAwait(false);
                    break;
            }
        }
    }

    [GeneratedRegex("^journal_[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex JournalCode();

    private sealed record CliFailure(string Code, string[] Details);

    private sealed record ReleaseSummary(string ReleaseId, HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt);

    private sealed record ActivitySummary(HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt);

    private sealed record OutcomeSummary(HostUpdateRecoveryOutcome Outcome, string Detail, DateTimeOffset RecordedAt);

    private sealed record ReleaseDetail(
        string ReleaseId,
        HostUpdateExecutionState State,
        string Phase,
        DateTimeOffset RecordedAt,
        string? RequestId,
        string[] UncertainPhases,
        OutcomeSummary? RecoveryOutcome,
        ActivitySummary[] Activities);

    private sealed record StatusReport(bool LockHeld, bool AdmissionClosed, ReleaseSummary[]? Releases, ReleaseDetail? Release);

    private sealed record PreviewReport(string ReleaseId, string RequestId, HostUpdateRecoveryPlan Plan, IReadOnlyList<string> NamespaceProofFailures);

    private sealed record RecoveryReport(string ReleaseId, string RequestId, HostUpdateRecoveryOutcome Outcome, string Detail);
}
