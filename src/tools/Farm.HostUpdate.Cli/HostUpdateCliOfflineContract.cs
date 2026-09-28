using System.ComponentModel;
using System.Globalization;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Logging;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Offline fence proof for the host-local CLI (issue #3127). The shared fence registration proves
/// background writers quiesced through in-memory activity flags that only the writer's own host
/// process can acknowledge; the CLI is a separate process, so those flags can never be acknowledged
/// and the fence would always fail. Offline activation instead proves each background writer is
/// fenced by a stronger, externally observable fact: the durable admission gate is closed <em>and</em>
/// every compose writer host (<c>api</c>, <c>slicer-host</c>, <c>monolith</c>) is observed stopped on
/// each poll. A stopped host cannot run any of its background writers, and a host started later
/// (for example by apply) observes the closed durable gate and stays paused until release.
/// </summary>
/// <remarks>
/// No writer is dropped: every registered writer name, and every configured
/// <see cref="HostUpdateExecutionOptions.RequiredFencedWriterNames"/> entry, must be proven by the
/// same bounded <see cref="HostUpdateFenceCoordinator"/>. A required name this contract does not know
/// how to prove is reported unfenced, never assumed quiesced.
/// </remarks>
internal sealed class HostUpdateCliWriterStoppedFenceCoordinator(
    IHostUpdateAdmissionGate admissionGate,
    IHostUpdateOfflineActivationSafetyProbe writerHostProbe,
    IReadOnlyList<IFenceableWriter> registeredWriters,
    HostUpdateExecutionOptions options,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null) : IHostUpdateFenceCoordinator
{
    private readonly ILogger _writerLogger = loggerFactory.CreateLogger<WriterHostStoppedFenceableWriter>();
    private readonly ILogger<HostUpdateFenceCoordinator> _coordinatorLogger = loggerFactory.CreateLogger<HostUpdateFenceCoordinator>();

    /// <summary>Compose writer hosts that run the fenced background writers.</summary>
    internal static readonly IReadOnlySet<string> BackgroundWriterHostServiceIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "api",
        "monolith",
    };

    private const string AdmissionWriterName = "api-admission";

    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateCoordinator(request).RunAsync(request, cancellationToken);
    }

    public Task ReleaseAsync(CancellationToken cancellationToken) => admissionGate.OpenAsync(cancellationToken);

    private HostUpdateFenceCoordinator CreateCoordinator(HostUpdateExecutionRequest request)
    {
        bool writerHostInTopology = options.ActiveServiceIds.Any(BackgroundWriterHostServiceIds.Contains);

        // Every background writer is proven by the same host observation, so one poll probes once (#3133).
        var writerHostsStopped = new PerPollWriterHostProbe(cancellationToken => writerHostProbe is DockerComposeApiAbsenceProbe composeProbe
            ? composeProbe.ValidateWriterHostsStoppedAsync(cancellationToken)
            : writerHostProbe.ValidateSafeToExecuteAsync(request, cancellationToken));
        var writers = new List<IFenceableWriter>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in registeredWriters.Select(writer => writer.Name).Concat(options.RequiredFencedWriterNames))
        {
            if (!names.Add(name))
            {
                continue;
            }

            if (string.Equals(name, AdmissionWriterName, StringComparison.Ordinal))
            {
                writers.Add(new AdmissionFenceableWriter(admissionGate));
            }
            else if (registeredWriters.FirstOrDefault(writer => string.Equals(writer.Name, name, StringComparison.Ordinal))
                is HostUpdatePriorReleaseWriterFence priorReleaseWriter)
            {
                // Proves itself from outside the process and stops the tolerated N-1 writer (#3126).
                writers.Add(priorReleaseWriter);
            }
            else if (registeredWriters.Any(writer => string.Equals(writer.Name, name, StringComparison.Ordinal)))
            {
                writers.Add(new WriterHostStoppedFenceableWriter(
                    name,
                    admissionGate,
                    writerHostInTopology,
                    writerHostsStopped.ProbeAsync,
                    _writerLogger));
            }
            else
            {
                writers.Add(new UnprovableFenceableWriter(name));
            }
        }

        return new HostUpdateFenceCoordinator(
            writers,
            TimeSpan.FromSeconds(options.FenceProofTimeoutSeconds),
            TimeSpan.FromSeconds(options.FencePollIntervalSeconds),
            timeProvider,
            _coordinatorLogger,
            writerHostsStopped.BeginPoll);
    }
}

/// <summary>
/// Shares one writer-host observation across every writer evaluated in the same fence poll. The
/// first writer to ask in a poll runs the probe; the rest of that poll reuse its result, and
/// <see cref="BeginPoll"/> discards it so every poll observes the hosts afresh.
/// </summary>
internal sealed class PerPollWriterHostProbe(Func<CancellationToken, Task<string?>> probe)
{
    private readonly Lock _sync = new();
    private int _poll;
    private bool _observed;
    private string? _failure;

    public void BeginPoll()
    {
        lock (_sync)
        {
            _poll++;
            _observed = false;
            _failure = null;
        }
    }

    public async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        int poll;
        lock (_sync)
        {
            if (_observed)
            {
                return _failure;
            }

            poll = _poll;
        }

        string? failure = await probe(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            // A result observed for an earlier poll is never published into a later one.
            if (poll == _poll)
            {
                _observed = true;
                _failure = failure;
            }
        }

        return failure;
    }
}

/// <summary>
/// Proves one background writer fenced when the durable admission gate is closed and its writer
/// host is observed stopped. The probe runs fresh on every poll; a failed or unparseable probe, or a
/// topology with no host that runs background writers, is unproven (fail closed).
/// </summary>
internal sealed class WriterHostStoppedFenceableWriter(
    string name,
    IHostUpdateAdmissionGate admissionGate,
    bool writerHostInTopology,
    Func<CancellationToken, Task<string?>> probeWriterHostsStopped,
    ILogger logger) : IFenceableWriter
{
    public string Name { get; } = name;

    public Task QuiesceAsync(CancellationToken cancellationToken) => admissionGate.CloseAsync(cancellationToken);

    public async Task<bool> IsQuiescedAsync(CancellationToken cancellationToken)
    {
        if (!writerHostInTopology || !await admissionGate.IsClosedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string? failure = await probeWriterHostsStopped(cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            return true;
        }

        // The probe returns a fixed fault code, never process output or host paths.
        logger.LogInformation("host_update_writer_host_not_proven_stopped writer={WriterName} code={Code}", Name, failure);
        return false;
    }

    public Task ResumeAsync(CancellationToken cancellationToken) => admissionGate.OpenAsync(cancellationToken);
}

/// <summary>A required writer the offline contract has no way to prove; it always blocks the fence.</summary>
internal sealed class UnprovableFenceableWriter(string name) : IFenceableWriter
{
    public string Name { get; } = name;

    public Task QuiesceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> IsQuiescedAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// In-network transport for the aggregate <c>/health</c> readiness check (issue #3127). The API port
/// is published only on the internal compose network, so the host running the CLI cannot reach
/// <see cref="HostUpdateExecutionOptions.HealthCheckBaseUrl"/>. This runs <c>curl</c> inside the
/// target API container through <c>docker compose exec</c> against its own loopback listener and
/// applies the unchanged <see cref="HostUpdateAggregateHealthReport"/> rule (top-level and every
/// required result exactly <c>Healthy</c>). Any process failure, timeout or malformed body is
/// unhealthy.
/// </summary>
internal sealed class ComposeExecAggregateHealthCheck(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    HostUpdateExecutionOptions options) : IHostUpdateAggregateHealthCheck
{
    /// <summary>
    /// Container-internal listener of each host that serves the aggregate <c>/health</c> endpoint. Must match
    /// the <c>ASPNETCORE_URLS</c> each compose template sets; a test pins them together (#3133).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> HealthPortsByServiceId = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["monolith"] = 5000,
        ["api"] = 5245,
    };

    private const int ProcessTimeoutGraceSeconds = 5;

    public string Name => HostUpdateRecoveryEngineRegistration.AggregateHealthCheckName;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildArguments(options, out IReadOnlyList<string>? arguments))
        {
            return false;
        }

        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                executableResolver.Resolve("docker"),
                arguments!,
                TimeSpan.FromSeconds(options.ProcessDefaultTimeoutSeconds + ProcessTimeoutGraceSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException or Win32Exception)
        {
            return false;
        }

        return result.Succeeded &&
            HostUpdateAggregateHealthReport.IsHealthy(
                result.StandardOutput,
                options.RequiredAggregateHealthResultNames.ToHashSet(StringComparer.Ordinal));
    }

    internal static bool TryBuildArguments(HostUpdateExecutionOptions options, out IReadOnlyList<string>? arguments)
    {
        arguments = null;
        string? serviceId = options.ActiveServiceIds.Contains("monolith", StringComparer.Ordinal) ? "monolith"
            : options.ActiveServiceIds.Contains("api", StringComparer.Ordinal) ? "api"
            : null;
        if (serviceId is null)
        {
            return false;
        }

        HostUpdateServiceMappingOptions? mapping = options.ServiceMappings
            .FirstOrDefault(candidate => string.Equals(candidate.ServiceId, serviceId, StringComparison.Ordinal));
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.ComposeServiceName))
        {
            return false;
        }

        List<string> list = ["compose"];
        foreach (string composeFile in options.ComposeFiles)
        {
            list.Add("-f");
            list.Add(composeFile);
        }

        list.AddRange(
        [
            "-p",
            options.ComposeProjectName,
            "exec",
            "-T",
            mapping.ComposeServiceName,
            "curl",
            "--silent",
            "--show-error",
            "--noproxy",
            "*",
            "--max-time",
            options.ProcessDefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            $"http://127.0.0.1:{HealthPortsByServiceId[serviceId].ToString(CultureInfo.InvariantCulture)}/health",
        ]);
        arguments = list;
        return true;
    }
}
