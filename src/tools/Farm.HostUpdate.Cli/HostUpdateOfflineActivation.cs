using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli;

/// <summary>Activates a previously imported offline bundle without registry fallback or local builds.</summary>
internal static class HostUpdateOfflineActivation
{
    private static readonly string[] ServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    internal static Func<string> HostPlatformFactory { get; set; } = HostUpdateHostPlatform.Current;

    public static async Task<int> RunAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        HostUpdatePolicyObservation policy = HostUpdateRecoveryDrift.ReadPolicy(configuration);
        if (!policy.Available)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "policy_unavailable:" + policy.Error).ConfigureAwait(false);
        }

        if (!string.Equals(policy.Channel, args.Channel, StringComparison.Ordinal))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, "channel_mismatch_policy").ConfigureAwait(false);
        }

        if (!HostUpdateOfflineAdmission.TryReadStaged(args.Staging!, args.Channel!, out HostUpdateOfflineAdmission.StagedRelease? staged, out string? stagingError))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, stagingError!).ConfigureAwait(false);
        }

        string? signatureError = await HostUpdateOfflineAdmission.VerifySignatureAsync(args, staged!, cancellationToken).ConfigureAwait(false);
        if (signatureError is not null)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, signatureError).ConfigureAwait(false);
        }

        HostUpdateExecutionRequest request;
        VerifiedHostUpdateCandidate candidate;
        try
        {
            string hostPlatform = HostPlatformFactory();
            HostUpdatePlatformDigests digests = PlatformDigestsFor(staged!.Manifest, hostPlatform);
            candidate = staged.Candidate with { PlatformDigests = digests, HostPlatform = hostPlatform };
            request = new HostUpdateExecutionRequest(
                candidate.ReleaseId,
                candidate.Sequence,
                candidate.ManifestDigest,
                candidate.SourceCommit,
                candidate.Channel == "stable" ? HostUpdateExecutionChannel.Stable : HostUpdateExecutionChannel.Insider,
                CreateTargets(digests, hostPlatform))
            {
                RequestId = "offline-activate:" + candidate.Identity[7..39],
                TrustRoot = candidate.TrustRoot,
                PolicyRevision = policy.Revision,
                PolicyFingerprint = policy.Fingerprint,
                HostPlatform = hostPlatform,
                AuthorizationKind = HostUpdateAuthorizationKind.StandingPolicy,
                ImageSourceMode = HostUpdateImageSourceMode.PreloadedLocal,
            };
        }
        catch (InvalidOperationException exception)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, exception.Message).ConfigureAwait(false);
        }

        if (!request.IsValid(out string requestError))
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, requestError).ConfigureAwait(false);
        }

        HostUpdateReplayDecision replay;
        try
        {
            replay = await ReadReplayDecisionAsync(configuration, candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HostUpdateCli.IsStateFailure(exception))
        {
            return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable,
                new HostUpdateCli.CliFailure(HostUpdateCli.StateFailureCode(exception), [])).ConfigureAwait(false);
        }

        bool skipReadinessChecks = false;
        if (replay.Disposition != HostUpdateReplayDisposition.Imported)
        {
            if (replay.Disposition == HostUpdateReplayDisposition.Accepted)
            {
                using IServiceScope recoveryScope = provider.CreateScope();
                if (await HostUpdateOfflineActivationStartGuard.TryFinalizeAcceptedActivationAsync(
                        recoveryScope.ServiceProvider.GetRequiredService<IHostUpdateExecutionJournal>(),
                        recoveryScope.ServiceProvider.GetRequiredService<IInstalledHostStateStore>(),
                        request,
                        appendCompletion: false,
                        cancellationToken).ConfigureAwait(false))
                {
                    skipReadinessChecks = true;
                }
                else
                {
                    return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, "replay_accepted").ConfigureAwait(false);
                }
            }
            else
            {
                return await FailAsync(output, args, HostUpdateCliExitCodes.Refused,
                    "replay_" + replay.Disposition.ToString().ToLowerInvariant()).ConfigureAwait(false);
            }
        }

        using (IHostUpdateExecutionLease? readinessLease = HostUpdateCli.TryAcquireLock(provider))
        {
            if (readinessLease is null)
            {
                return await FailAsync(output, args, HostUpdateCliExitCodes.LockHeld, "host_update_lock_held").ConfigureAwait(false);
            }
        }

        try
        {
            if (!skipReadinessChecks)
            {
                using IServiceScope preflightScope = provider.CreateScope();
                IServiceProvider scoped = preflightScope.ServiceProvider;
                await scoped.GetRequiredService<IHostUpdateLocalImageVerifier>()
                    .VerifyTargetsAsync(request, cancellationToken).ConfigureAwait(false);
                string? safetyError = await scoped.GetRequiredService<IHostUpdateOfflineActivationSafetyProbe>()
                    .ValidateSafeToExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                if (safetyError is not null)
                {
                    return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, safetyError).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is HostUpdatePreloadedImageVerificationException or HostUpdateApplyUnsupportedServiceException)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, exception.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, "activation_prerequisite_unproven:" + exception.GetType().Name).ConfigureAwait(false);
        }

        HostUpdateExecutionResult result;
        try
        {
            using IServiceScope executionScope = provider.CreateScope();
            result = await executionScope.ServiceProvider.GetRequiredService<IHostUpdateExecutor>().ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            IInstalledHostStateStore installedStore = executionScope.ServiceProvider.GetRequiredService<IInstalledHostStateStore>();
            InstalledHostState? installed = await installedStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            bool installedMatches = InstalledStateMatches(request, installed);
            bool bindingProvenCompleted = HostUpdateOfflineActivationStartGuard.CompletedJournalMatchesRequest(
                executionScope.ServiceProvider.GetRequiredService<IHostUpdateExecutionJournal>().Read(request.ReleaseId),
                request);
            if (result.State == HostUpdateExecutionState.Completed)
            {
                if (!installedMatches)
                {
                    result = result with { FailureCode = "activation_not_applied" };
                }
            }
            else if (installedMatches && bindingProvenCompleted)
            {
                result = result with { State = HostUpdateExecutionState.Completed };
            }
        }
        catch (TimeoutException)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.LockHeld, "host_update_lock_held").ConfigureAwait(false);
        }
        catch (Exception exception) when (HostUpdateCli.IsStateFailure(exception))
        {
            return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable,
                new HostUpdateCli.CliFailure(HostUpdateCli.StateFailureCode(exception), [])).ConfigureAwait(false);
        }

        int exitCode = result.State == HostUpdateExecutionState.Completed
            ? HostUpdateCliExitCodes.Success
            : HostUpdateCliExitCodes.Refused;
        return await HostUpdateCli.EmitAsync(output, args.Json, exitCode,
            new OfflineActivationReport(
                result.State == HostUpdateExecutionState.Completed ? "activated" : "refused",
                result.FailureCode,
                request.ReleaseId,
                request.ManifestDigest,
                request.RequestId,
                result.State)).ConfigureAwait(false);
    }

    private static HostUpdatePlatformDigests PlatformDigestsFor(SignedUpdateManifest manifest, string hostPlatform)
    {
        if (!manifest.Platforms.Contains(hostPlatform, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("platform_not_in_manifest");
        }

        string Digest(string serviceId)
        {
            string key = HostUpdateOfflineAdmission.PlatformKey(serviceId, hostPlatform);
            if (!manifest.PlatformDigests.TryGetValue(key, out string? digest) || !IsCanonicalDigest(digest))
            {
                throw new InvalidOperationException("image_set_incomplete:" + serviceId);
            }

            return digest;
        }

        var digests = new HostUpdatePlatformDigests(
            Digest(ServiceIds[0]),
            Digest(ServiceIds[1]),
            Digest(ServiceIds[2]),
            Digest(ServiceIds[3]),
            Digest(ServiceIds[4]),
            Digest(ServiceIds[5]));
        if (!digests.IsComplete)
        {
            throw new InvalidOperationException("image_set_mixed_or_incomplete");
        }

        return digests;
    }

    private static List<HostUpdateExecutionTarget> CreateTargets(HostUpdatePlatformDigests digests, string hostPlatform) =>
    [
        new(ServiceIds[0], hostPlatform, digests.Api),
        new(ServiceIds[1], hostPlatform, digests.Frontend),
        new(ServiceIds[2], hostPlatform, digests.SlicerHost),
        new(ServiceIds[3], hostPlatform, digests.PrinterDiscovery),
        new(ServiceIds[4], hostPlatform, digests.OrcaslicerWorker),
        new(ServiceIds[5], hostPlatform, digests.Monolith),
    ];

    private static Task<int> FailAsync(TextWriter output, HostUpdateCliArguments args, int exitCode, string code) =>
        HostUpdateCli.EmitAsync(output, args.Json, exitCode, new HostUpdateCli.CliFailure(code, []));

    internal static async Task<HostUpdateReplayDecision> ReadReplayDecisionAsync(
        IConfiguration configuration,
        VerifiedHostUpdateCandidate candidate,
        CancellationToken cancellationToken)
    {
        using FileHostUpdateReplayStore store = ReplayStore(configuration, out FileHostUpdateReplayAnchor anchor);
        using (anchor)
        {
            return await store.ReadIdentityAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<HostUpdateReplayDecision> MarkActivatedAsync(
        IConfiguration configuration,
        VerifiedHostUpdateCandidate candidate,
        CancellationToken cancellationToken)
    {
        using FileHostUpdateReplayStore store = ReplayStore(configuration, out FileHostUpdateReplayAnchor anchor);
        using (anchor)
        {
            return await store.MarkActivatedAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static VerifiedHostUpdateCandidate CandidateFromRequest(HostUpdateExecutionRequest request)
    {
        Dictionary<string, string> digests = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
        string Digest(string serviceId) => digests.TryGetValue(serviceId, out string? digest) ? digest : string.Empty;
        return new VerifiedHostUpdateCandidate(
            request.ReleaseId,
            request.SourceCommit,
            request.AuthenticatedSequence,
            request.ManifestDigest,
            request.Channel == HostUpdateExecutionChannel.Stable ? "stable" : "insider",
            CryptographicallyVerified: true,
            CompatibilityReady: true,
            InstallationAvailable: true,
            SafetyPassed: true,
            MaintenanceWindowOpen: true,
            IsNewer: true,
            new HostUpdatePlatformDigests(
                Digest(ServiceIds[0]),
                Digest(ServiceIds[1]),
                Digest(ServiceIds[2]),
                Digest(ServiceIds[3]),
                Digest(ServiceIds[4]),
                Digest(ServiceIds[5])),
            TrustRoot: request.TrustRoot)
        {
            HostPlatform = request.HostPlatform,
        };
    }

    private static FileHostUpdateReplayStore ReplayStore(IConfiguration configuration, out FileHostUpdateReplayAnchor anchor)
    {
        HostStateOptions? hostState = configuration.GetSection(HostStateOptions.SectionName).Get<HostStateOptions>();
        if (hostState is null || !hostState.Enabled || string.IsNullOrWhiteSpace(hostState.RootPath))
        {
            throw new InvalidDataException("host_state_not_enabled");
        }

        HostStatePath paths = new(Options.Create(hostState));
        anchor = new FileHostUpdateReplayAnchor(paths);
        return new FileHostUpdateReplayStore(paths.Root, anchor);
    }

    internal static bool InstalledStateMatches(HostUpdateExecutionRequest request, InstalledHostState? installed)
    {
        if (installed is null ||
            !string.Equals(installed.ReleaseId, request.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals(installed.ManifestDigest, request.ManifestDigest, StringComparison.Ordinal) ||
            installed.ServicePlatforms is null)
        {
            return false;
        }

        Dictionary<string, string> expectedDigests = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
        Dictionary<string, string> expectedPlatforms = request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal);
        return expectedDigests.Count == installed.ServiceDigests.Count &&
            expectedDigests.All(pair => installed.ServiceDigests.TryGetValue(pair.Key, out string? digest) && string.Equals(digest, pair.Value, StringComparison.Ordinal)) &&
            expectedPlatforms.All(pair => installed.ServicePlatforms.TryGetValue(pair.Key, out string? platform) && string.Equals(platform, pair.Value, StringComparison.Ordinal));
    }

    private static bool IsCanonicalDigest(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.Length == 71 &&
        value[7..].All(Uri.IsHexDigit);

    private sealed record OfflineActivationReport(
        string Decision,
        string? Reason,
        string ReleaseId,
        string ManifestDigest,
        string RequestId,
        HostUpdateExecutionState State);
}

internal interface IHostUpdateOfflineActivationSafetyProbe
{
    Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

internal sealed class HostUpdateOfflineActivationStartGuard(
    IConfiguration configuration,
    IHostUpdateExecutionJournal journal,
    IHostUpdateOfflineActivationSafetyProbe safetyProbe,
    IInstalledHostStateStore installedStateStore) : IHostUpdateExecutionStartGuard
{
    public async Task<string?> ValidateAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(request.ReleaseId);
        HostUpdateExecutionState? current = activities.Count == 0 ? null : activities[^1].State;
        if (current == HostUpdateExecutionState.Completed)
        {
            if (!CompletedJournalMatchesRequest(activities, request))
            {
                return "activation_already_completed";
            }

            HostUpdateReplayDecision completedReplay = await HostUpdateOfflineActivation.ReadReplayDecisionAsync(
                configuration,
                HostUpdateOfflineActivation.CandidateFromRequest(request),
                cancellationToken).ConfigureAwait(false);
            if (completedReplay.Disposition == HostUpdateReplayDisposition.Imported)
            {
                InstalledHostState? installed = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (HostUpdateOfflineActivation.InstalledStateMatches(request, installed))
                {
                    HostUpdateReplayDecision recorded = await HostUpdateOfflineActivation.MarkActivatedAsync(
                        configuration,
                        HostUpdateOfflineActivation.CandidateFromRequest(request),
                        cancellationToken).ConfigureAwait(false);
                    return recorded.Disposition == HostUpdateReplayDisposition.Accepted
                        ? null
                        : "activation_replay_record_failed";
                }
            }
            else if (completedReplay.Disposition == HostUpdateReplayDisposition.Accepted &&
                HostUpdateOfflineActivation.InstalledStateMatches(request, await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false)))
            {
                return null;
            }

            return "activation_already_completed";
        }

        if (current == HostUpdateExecutionState.RecoveryRequired)
        {
            return "activation_recovery_required";
        }

        HostUpdateReplayDecision replay = await HostUpdateOfflineActivation.ReadReplayDecisionAsync(
            configuration,
            HostUpdateOfflineActivation.CandidateFromRequest(request),
            cancellationToken).ConfigureAwait(false);
        if (replay.Disposition == HostUpdateReplayDisposition.Accepted &&
            await TryFinalizeAcceptedActivationAsync(journal, installedStateStore, request, appendCompletion: true, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (replay.Disposition != HostUpdateReplayDisposition.Imported)
        {
            return "replay_" + replay.Disposition.ToString().ToLowerInvariant();
        }

        return await safetyProbe.ValidateSafeToExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> TryFinalizeAcceptedActivationAsync(
        IHostUpdateExecutionJournal journal,
        IInstalledHostStateStore installedStateStore,
        HostUpdateExecutionRequest request,
        bool appendCompletion,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(request.ReleaseId);
        HostUpdateExecutionState? current = activities.Count == 0 ? null : activities[^1].State;
        if (!HostUpdateOfflineActivation.InstalledStateMatches(request, await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }

        if (current == HostUpdateExecutionState.Completed)
        {
            return CompletedJournalMatchesRequest(activities, request);
        }

        if (current == HostUpdateExecutionState.RecoveryRequired || !VerifiedJournalMatchesRequest(activities, request))
        {
            return false;
        }

        if (appendCompletion)
        {
            AppendCompletedJournal(journal, request);
        }

        return true;
    }

    internal static bool CompletedJournalMatchesRequest(IReadOnlyList<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request) =>
        VerifiedJournalMatchesRequest(activities, request) &&
        ExactBoundActivityExists(activities, request, HostUpdateExecutionState.Completed, "completed");

    private static bool VerifiedJournalMatchesRequest(IReadOnlyList<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request)
    {
        return activities.Count > 0 &&
            activities.All(activity => activity.RequestBindingHash is null || string.Equals(activity.RequestBindingHash, HostUpdateRequestBinding.Compute(request), StringComparison.Ordinal)) &&
            ExactBoundActivityExists(activities, request, HostUpdateExecutionState.Verifying, "verify:after");
    }

    private static bool ExactBoundActivityExists(
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        HostUpdateExecutionRequest request,
        HostUpdateExecutionState state,
        string phase)
    {
        string bindingHash = HostUpdateRequestBinding.Compute(request);
        return activities.Any(activity =>
            activity.State == state &&
            string.Equals(activity.Phase, phase, StringComparison.Ordinal) &&
            string.Equals(activity.RequestBindingHash, bindingHash, StringComparison.Ordinal));
    }

    private static void AppendCompletedJournal(IHostUpdateExecutionJournal journal, HostUpdateExecutionRequest request)
    {
        string bindingHash = HostUpdateRequestBinding.Compute(request);
        journal.Append(new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), request.ReleaseId, HostUpdateExecutionState.Completed, "fence-release:after", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = bindingHash,
            RequestBinding = request,
        });
        journal.Append(new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), request.ReleaseId, HostUpdateExecutionState.Completed, "completed", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = bindingHash,
            RequestBinding = request,
        });
    }
}

internal sealed class HostUpdateOfflineActivationCompletionHook(
    IConfiguration configuration,
    IInstalledHostStateStore installedStateStore) : IHostUpdateExecutionCompletionHook
{
    public async Task<string?> CompleteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        InstalledHostState? installed = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!HostUpdateOfflineActivation.InstalledStateMatches(request, installed))
        {
            return "activation_not_applied";
        }

        HostUpdateReplayDecision recorded = await HostUpdateOfflineActivation.MarkActivatedAsync(
            configuration,
            HostUpdateOfflineActivation.CandidateFromRequest(request),
            cancellationToken).ConfigureAwait(false);
        return recorded.Disposition == HostUpdateReplayDisposition.Accepted
            ? null
            : "activation_replay_record_failed";
    }
}

internal sealed class DockerComposeApiAbsenceProbe(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    HostUpdateExecutionOptions options) : IHostUpdateOfflineActivationSafetyProbe
{
    private static readonly IReadOnlySet<string> WriterServiceIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "api",
        "slicer-host",
        "monolith",
    };

    private static readonly HashSet<string> AllowedInactiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "exited",
        "dead",
        "removed",
        "not created",
    };

    public async Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string[] activeWriterIds = [.. options.ActiveServiceIds.Where(WriterServiceIds.Contains).Order(StringComparer.Ordinal)];
        if (activeWriterIds.Length == 0)
        {
            return null;
        }

        Dictionary<string, string> composeServicesByServiceId = [];
        foreach (string serviceId in activeWriterIds)
        {
            HostUpdateServiceMappingOptions? mapping = options.ServiceMappings
                .FirstOrDefault(candidate => string.Equals(candidate.ServiceId, serviceId, StringComparison.Ordinal));
            if (mapping is null)
            {
                return "writer_absence_unproven:service_mapping_missing:" + serviceId;
            }

            composeServicesByServiceId[serviceId] = mapping.ComposeServiceName;
        }

        List<string> arguments = ["compose"];
        foreach (string composeFile in options.ComposeFiles)
        {
            arguments.Add("-f");
            arguments.Add(composeFile);
        }

        arguments.Add("-p");
        arguments.Add(options.ComposeProjectName);
        arguments.Add("ps");
        arguments.Add("-a");
        arguments.Add("--format");
        arguments.Add("json");
        arguments.AddRange(composeServicesByServiceId.Values);

        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                executableResolver.Resolve("docker"),
                arguments,
                TimeSpan.FromSeconds(options.ProcessDefaultTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "api_absence_unproven:" + exception.GetType().Name;
        }

        if (!result.Succeeded)
        {
            return "writer_absence_unproven:compose_ps_failed";
        }

        return ValidateComposeState(result.StandardOutput, composeServicesByServiceId);
    }

    internal static string? ValidateComposeState(string standardOutput, IReadOnlyDictionary<string, string> composeServicesByServiceId)
    {
        Dictionary<string, string> serviceIdsByComposeName = composeServicesByServiceId.ToDictionary(
            pair => pair.Value,
            pair => pair.Key,
            StringComparer.Ordinal);
        try
        {
            string trimmed = standardOutput.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (trimmed.StartsWith('['))
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return "writer_absence_unproven:compose_ps_unparseable";
                }

                foreach (JsonElement entry in document.RootElement.EnumerateArray())
                {
                    string? error = ValidateEntry(entry, serviceIdsByComposeName);
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }
            else
            {
                foreach (string line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    string? error = ValidateEntry(document.RootElement, serviceIdsByComposeName);
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return "writer_absence_unproven:compose_ps_unparseable";
        }
    }

    private static string? ValidateEntry(JsonElement entry, Dictionary<string, string> serviceIdsByComposeName)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !TryGetString(entry, "Service", out string? composeService) ||
            !TryGetString(entry, "State", out string? state))
        {
            return "writer_absence_unproven:compose_ps_unparseable";
        }

        if (!serviceIdsByComposeName.TryGetValue(composeService!, out string? serviceId))
        {
            return null;
        }

        return AllowedInactiveStates.Contains(state!)
            ? null
            : "writer_service_active:" + serviceId + ":" + state;
    }

    private static bool TryGetString(JsonElement entry, string propertyName, out string? value)
    {
        if (entry.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}
