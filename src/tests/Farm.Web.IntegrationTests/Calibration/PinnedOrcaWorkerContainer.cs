using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// Runs the published pinned OrcaSlicer worker image by immutable digest and points it at the API
/// listener the smoke started.
/// </summary>
/// <remarks>
/// <para>
/// The image is never executed by tag. The container digest the worker attests with is injected at
/// runtime through <c>Worker__ContainerDigest</c>, because embedding it during the build would change
/// the very digest it claims.
/// </para>
/// <para>
/// The container joins the runner's network namespace so it can dial the loopback listener the API host
/// bound. No secret, private URL or host path is ever passed on the command line beyond the run-scoped
/// registration key, and container logs are truncated and scrubbed before they reach test output.
/// </para>
/// </remarks>
internal sealed class PinnedOrcaWorkerContainer : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly string _containerName;
    private readonly IReadOnlyCollection<string> _redactedValues;
    private int _disposed;

    private PinnedOrcaWorkerContainer(
        string containerName,
        string imageReference,
        int workerPort,
        IReadOnlyCollection<string> redactedValues)
    {
        _containerName = containerName;
        _redactedValues = redactedValues;
        ImageReference = imageReference;
        WorkerPort = workerPort;
    }

    /// <summary>Gets the immutable <c>repository@sha256:...</c> reference that was executed.</summary>
    public string ImageReference { get; }

    /// <summary>Gets the loopback port the worker's own HTTP surface listens on.</summary>
    public int WorkerPort { get; }

    /// <summary>Gets the worker's loopback base address on the runner.</summary>
    public string BaseAddress => FormattableString.Invariant($"http://127.0.0.1:{WorkerPort}");

    /// <summary>
    /// Determines whether a usable Docker CLI is present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when <c>docker --version</c> succeeds.</returns>
    public static async Task<bool> HasDockerAsync(CancellationToken cancellationToken)
    {
        try
        {
            CommandResult result = await RunAsync(["--version"], cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the local registry digest of a pulled image reference.
    /// </summary>
    /// <param name="imageReference">The immutable image reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The repository digests the local daemon recorded.</returns>
    public static async Task<IReadOnlyList<string>> ReadRepositoryDigestsAsync(
        string imageReference,
        CancellationToken cancellationToken)
    {
        CommandResult inspect = await RunAsync(
            ["image", "inspect", "--format", "{{json .RepoDigests}}", imageReference],
            cancellationToken);
        return inspect.ExitCode != 0
            ? []
            : inspect.StandardOutput
                .Trim()
                .Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.Trim('"'))
                .Where(entry => entry.Length > 0)
                .ToArray();
    }

    /// <summary>
    /// Pulls the published image by digest.
    /// </summary>
    /// <param name="imageReference">The immutable image reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command outcome.</returns>
    public static Task<CommandResult> PullAsync(string imageReference, CancellationToken cancellationToken) =>
        RunAsync(["pull", imageReference], cancellationToken);

    /// <summary>
    /// Starts the published worker against an API listener.
    /// </summary>
    /// <param name="imageReference">The immutable <c>repository@sha256:...</c> reference to run.</param>
    /// <param name="containerDigest">The registry manifest digest injected at runtime.</param>
    /// <param name="apiBaseAddress">Loopback address of the API listener.</param>
    /// <param name="registrationKey">Shared registration key for the production registration route.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started container.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the container could not start.</exception>
    public static async Task<PinnedOrcaWorkerContainer> StartAsync(
        string imageReference,
        string containerDigest,
        string apiBaseAddress,
        string registrationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationKey);

        int workerPort = ReserveLoopbackPort();
        string containerName = $"pfarm-orca-smoke-{Guid.NewGuid():N}"[..40];
        string workerAddress = FormattableString.Invariant($"http://127.0.0.1:{workerPort}");

        List<string> arguments =
        [
            "run",
            "--detach",
            "--name", containerName,

            // Share the runner's network namespace so the worker can dial the loopback API listener.
            "--network", "host",
            "--env", "ASPNETCORE_ENVIRONMENT=Production",
            "--env", FormattableString.Invariant($"ASPNETCORE_URLS={workerAddress}"),
            "--env", $"SlicerApi__BaseUrl={apiBaseAddress}",
            "--env", $"SlicerRegistry__ApiBaseUrl={apiBaseAddress}",
            "--env", $"Worker__ApiBaseUrl={apiBaseAddress}",
            "--env", $"Worker__StorageEndpoint={apiBaseAddress}",
            "--env", "Worker__OrcaSlicerPath=/opt/orcaslicer/bin/orca-slicer",
            "--env", $"SlicerRegistry__Host={workerAddress}",
            "--env", $"SlicerRegistry__ServiceName={containerName}",
            "--env", $"SlicerRegistry__InstanceId={containerName}",
            "--env", $"Worker__InstanceId={containerName}",

            // Runtime-only injection: the digest cannot be embedded while the image is being built.
            "--env", $"Worker__ContainerDigest={containerDigest}",
            "--env", $"WorkerAuth__SharedKey={registrationKey}",
            "--env", $"WorkerAuth__SharedApiKey={registrationKey}",
            "--env", "Worker__PollIntervalSeconds=2",
            "--env", "Worker__MaxConcurrentJobs=1",
            "--env", "Worker__LeaseDurationSeconds=600",
            imageReference,
        ];

        CommandResult started = await RunAsync(arguments, cancellationToken);
        PinnedOrcaWorkerContainer container = new(
            containerName,
            imageReference,
            workerPort,
            [registrationKey]);
        if (started.ExitCode != 0)
        {
            await container.DisposeAsync();
            throw new InvalidOperationException(
                "The published pinned OrcaSlicer worker container could not start: " +
                Scrub(started.Describe(), [registrationKey]));
        }

        return container;
    }

    /// <summary>
    /// Waits until the worker answers a health probe on its own HTTP surface.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the worker is reachable.</returns>
    /// <exception cref="TimeoutException">Thrown when the worker never answered.</exception>
    public async Task WaitUntilReachableAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(10) };
        DateTime deadline = DateTime.UtcNow + timeout;
        string lastFailure = "(never attempted)";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    new Uri("/healthz", UriKind.Relative),
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastFailure = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastFailure = exception.GetType().Name;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"The pinned worker never answered on {BaseAddress} (last result: {lastFailure}). " +
            await ReadScrubbedLogsAsync(CancellationToken.None));
    }

    /// <summary>
    /// Reads a bounded, scrubbed tail of the container log for operator-facing diagnostics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scrubbed log tail.</returns>
    public async Task<string> ReadScrubbedLogsAsync(CancellationToken cancellationToken)
    {
        CommandResult logs = await RunAsync(["logs", "--tail", "80", _containerName], cancellationToken);
        string combined = logs.StandardOutput + Environment.NewLine + logs.StandardError;
        return "Worker log tail: " + Scrub(Truncate(combined, 6000), _redactedValues);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _ = await RunAsync(["rm", "--force", _containerName], CancellationToken.None);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            // The container is already gone or Docker disappeared; nothing durable is leaked either way.
        }
    }

    /// <summary>The outcome of a Docker CLI invocation.</summary>
    /// <param name="ExitCode">Process exit code.</param>
    /// <param name="StandardOutput">Captured standard output.</param>
    /// <param name="StandardError">Captured standard error.</param>
    internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        /// <summary>Renders the outcome for diagnostics.</summary>
        /// <returns>A bounded description.</returns>
        public string Describe() =>
            $"exitCode={ExitCode}, stdout='{Truncate(StandardOutput.Trim(), 1500)}', " +
            $"stderr='{Truncate(StandardError.Trim(), 1500)}'";
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[^maximumLength..];

    private static string Scrub(string value, IReadOnlyCollection<string> secrets)
    {
        StringBuilder builder = new(value);
        foreach (string secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)))
        {
            _ = builder.Replace(secret, "***");
        }

        return builder.ToString();
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    private static async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new IOException("The docker command could not be started.");
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new CommandResult(process.ExitCode, await standardOutput, await standardError);
    }
}
