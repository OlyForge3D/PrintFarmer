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

        IHostUpdateExecutionLease? lease = HostUpdateCli.TryAcquireLock(provider);
        if (lease is null)
        {
            return await FailAsync(output, args, HostUpdateCliExitCodes.LockHeld, "host_update_lock_held").ConfigureAwait(false);
        }

        using (lease)
        {
            HostUpdateReplayDecision replay;
            try
            {
                HostStateOptions? hostState = configuration.GetSection(HostStateOptions.SectionName).Get<HostStateOptions>();
                if (hostState is null || !hostState.Enabled || string.IsNullOrWhiteSpace(hostState.RootPath))
                {
                    return await FailAsync(output, args, HostUpdateCliExitCodes.ConfigurationUnproven, "host_state_not_enabled").ConfigureAwait(false);
                }

                HostStatePath paths = new(Options.Create(hostState));
                using var anchor = new FileHostUpdateReplayAnchor(paths);
                using var store = new FileHostUpdateReplayStore(paths.Root, anchor);
                replay = await store.DecideAsync(candidate, HostUpdateReplayIntent.Activate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (HostUpdateCli.IsStateFailure(exception))
            {
                return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable,
                    new HostUpdateCli.CliFailure(HostUpdateCli.StateFailureCode(exception), [])).ConfigureAwait(false);
            }

            bool imported = replay.Disposition == HostUpdateReplayDisposition.Imported ||
                (replay.Disposition == HostUpdateReplayDisposition.Accepted && replay.Reused);
            if (!imported)
            {
                return await FailAsync(output, args, HostUpdateCliExitCodes.Refused,
                    "replay_" + replay.Disposition.ToString().ToLowerInvariant()).ConfigureAwait(false);
            }

            try
            {
                await provider.GetRequiredService<IHostUpdateLocalImageVerifier>()
                    .VerifyTargetsAsync(request, cancellationToken).ConfigureAwait(false);
                string? safetyError = await provider.GetRequiredService<IHostUpdateOfflineActivationSafetyProbe>()
                    .ValidateSafeToExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                if (safetyError is not null)
                {
                    return await FailAsync(output, args, HostUpdateCliExitCodes.Refused, safetyError).ConfigureAwait(false);
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
        }

        HostUpdateExecutionResult result;
        try
        {
            result = await provider.GetRequiredService<IHostUpdateExecutor>().ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
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

internal sealed class DockerComposeApiAbsenceProbe(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    HostUpdateExecutionOptions options) : IHostUpdateOfflineActivationSafetyProbe
{
    public async Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.ActiveServiceIds.Contains("api", StringComparer.Ordinal))
        {
            return null;
        }

        HostUpdateServiceMappingOptions? apiMapping = options.ServiceMappings
            .FirstOrDefault(mapping => string.Equals(mapping.ServiceId, "api", StringComparison.Ordinal));
        if (apiMapping is null)
        {
            return "api_absence_unproven:service_mapping_missing";
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
        arguments.Add("--services");
        arguments.Add("--filter");
        arguments.Add("status=running");
        arguments.Add(apiMapping.ComposeServiceName);

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
            return "api_absence_unproven:compose_ps_failed";
        }

        string[] runningServices = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return runningServices.Any(service => string.Equals(service, apiMapping.ComposeServiceName, StringComparison.Ordinal))
            ? "api_service_running"
            : null;
    }
}
