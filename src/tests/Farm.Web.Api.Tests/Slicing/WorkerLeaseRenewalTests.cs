using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Verifies the worker's job-mutation request contract: every mutating request the job poller owns
/// carries the worker identity plus the exact lease token and fencing counter the job was claimed
/// under, exactly once, regardless of what the shared <see cref="HttpClient"/> carries as defaults.
/// </summary>
/// <remarks>
/// A real smoke run claimed a job, ran the slicer for over 20 minutes, and every renewal request was
/// denied. The poller was mixing two sources of truth: worker identity and the job lease were pushed
/// onto <see cref="HttpClient.DefaultRequestHeaders"/> at claim time while individual requests added
/// their own copies, so the authenticated worker mutation contract was ambiguous. These tests capture
/// the actual outgoing requests and assert a single unambiguous value per required header, and that a
/// denied renewal now stops the job instead of slicing on toward a stale-fence artifact.
/// </remarks>
public sealed class WorkerLeaseRenewalTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(AppContext.BaseDirectory, "worker-renewal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Lease renewal presents worker identity, lease token, fence and body exactly once")]
    public async Task Renewal_SendsExplicitWorkerIdentityAndLeaseHeaders()
    {
        Guid jobId = Guid.NewGuid();
        Guid serviceId = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        const long leaseFence = 7;
        const string apiKey = "test-worker-api-key";
        string jobDirectory = CreateJobOutput(jobId);

        StubHandler handler = new(HttpStatusCode.NoContent);
        RecordingLogger<HttpJobPollerService> logger = new();
        (RenewalRecordingPoller poller, WorkerStateService workerState) = CreatePoller(handler, logger, leaseDurationSeconds: 30);
        workerState.SetRegisteredService(serviceId, apiKey);

        await poller.RunAsync(jobId, jobDirectory, leaseToken, leaseFence);

        CapturedRequest? renewal = handler.Captured
            .FirstOrDefault(request => request.Path.EndsWith("/renew-lease", StringComparison.Ordinal));
        _ = renewal.Should().NotBeNull("the renewal loop must send at least one renew-lease request");
        _ = renewal!.Method.Should().Be(HttpMethod.Post);
        _ = renewal.Path.Should().Be($"/api/slice/{jobId}/renew-lease");
        _ = renewal.ContentType.Should().Be("application/json");
        AssertSingleValuedWorkerHeaders(renewal, apiKey, serviceId, leaseToken, leaseFence);

        RenewLeaseRequest? body = JsonSerializer.Deserialize<RenewLeaseRequest>(
            renewal.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = body.Should().NotBeNull();
        _ = body!.LeaseDurationSeconds.Should().Be(30);

        // The job must still complete normally: the renewal loop is a side channel and does not
        // gate the pipeline's own completion report.
        _ = Directory.Exists(jobDirectory).Should().BeFalse("a terminal completion discards local work");
    }

    [Fact(DisplayName = "Every job mutation the poller owns carries exactly one of each required header")]
    public async Task AllJobMutations_CarryExactlyOneValuePerRequiredHeader()
    {
        Guid jobId = Guid.NewGuid();
        Guid serviceId = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        const long leaseFence = 42;
        const string apiKey = "test-worker-api-key";
        string jobDirectory = CreateJobOutput(jobId);

        StubHandler handler = new(HttpStatusCode.NoContent);
        RecordingLogger<HttpJobPollerService> logger = new();
        (RenewalRecordingPoller poller, WorkerStateService workerState) = CreatePoller(handler, logger, leaseDurationSeconds: 30);
        workerState.SetRegisteredService(serviceId, apiKey);

        // Mirror the live poller: the claim leaves worker identity on the client's default headers.
        // Every mutation must still put exactly one value of each header on the wire.
        await poller.RunAsync(
            jobId,
            jobDirectory,
            leaseToken,
            leaseFence,
            configureDefaults: defaults =>
            {
                defaults.Add(WorkerLeaseHeaders.WorkerKey, apiKey);
                defaults.Add(WorkerLeaseHeaders.WorkerId, serviceId.ToString());
            });

        string[] observedPaths = handler.Captured.Select(request => request.Path).ToArray();
        _ = observedPaths.Should().Contain($"/api/slice/{jobId}/progress");
        _ = observedPaths.Should().Contain($"/api/slice/{jobId}/renew-lease");
        _ = observedPaths.Should().Contain($"/api/slice/{jobId}/artifacts");
        _ = observedPaths.Should().Contain($"/api/slice/{jobId}/complete");

        foreach (CapturedRequest request in handler.Captured)
        {
            AssertSingleValuedWorkerHeaders(request, apiKey, serviceId, leaseToken, leaseFence);
        }

        // The multipart upload is the easiest one to get wrong, so assert its shape explicitly.
        CapturedRequest upload = handler.Captured
            .Single(request => request.Path.EndsWith("/artifacts", StringComparison.Ordinal));
        _ = upload.ContentType.Should().Be("multipart/form-data");
    }

    [Fact(DisplayName = "A stale default header never survives alongside the current worker identity")]
    public async Task JobMutations_OverrideStaleClientDefaults_WithExactlyOneCurrentValue()
    {
        Guid jobId = Guid.NewGuid();
        Guid serviceId = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        const long leaseFence = 11;
        const string apiKey = "current-worker-api-key";
        string jobDirectory = CreateJobOutput(jobId);

        StubHandler handler = new(HttpStatusCode.NoContent);
        RecordingLogger<HttpJobPollerService> logger = new();
        (RenewalRecordingPoller poller, WorkerStateService workerState) = CreatePoller(handler, logger, leaseDurationSeconds: 30);
        workerState.SetRegisteredService(serviceId, apiKey);

        // The client's defaults were set from an earlier registration. Presenting both would make
        // the worker's identity ambiguous, which is exactly what the API rejects.
        await poller.RunAsync(
            jobId,
            jobDirectory,
            leaseToken,
            leaseFence,
            configureDefaults: defaults =>
            {
                defaults.Add(WorkerLeaseHeaders.WorkerKey, "stale-worker-api-key");
                defaults.Add(WorkerLeaseHeaders.WorkerId, Guid.NewGuid().ToString());
                defaults.Add(WorkerLeaseHeaders.LeaseToken, Guid.NewGuid().ToString());
                defaults.Add(WorkerLeaseHeaders.LeaseFence, "1");
            });

        _ = handler.Captured.Should().NotBeEmpty();
        foreach (CapturedRequest request in handler.Captured)
        {
            AssertSingleValuedWorkerHeaders(request, apiKey, serviceId, leaseToken, leaseFence);
        }
    }

    [Fact(DisplayName = "A denied renewal cancels the job and preserves local work instead of uploading")]
    public async Task Renewal_StaleFenceConflict_CancelsJobAndPreservesRecoverableWork()
    {
        Guid jobId = Guid.NewGuid();
        Guid serviceId = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        const long leaseFence = 3;
        string jobDirectory = CreateJobOutput(jobId);

        // Simulate the server rejecting the renewal because the fencing token it holds is no
        // longer current (409 Conflict, as SlicerApiProblems.LeaseConflict returns for
        // "stale_fencing_token" / "lease_conflict" / "lease_expired").
        StubHandler handler = new(HttpStatusCode.Conflict);
        RecordingLogger<HttpJobPollerService> logger = new();
        (RenewalRecordingPoller poller, WorkerStateService workerState) = CreatePoller(handler, logger, leaseDurationSeconds: 30);
        workerState.SetRegisteredService(serviceId, "test-worker-api-key");

        // The pipeline runs until the job's own token is cancelled, standing in for a long slice.
        poller.WaitForCancellation = true;

        Func<Task> act = () => poller.RunAsync(jobId, jobDirectory, leaseToken, leaseFence);

        // Losing the lease is a handled terminal outcome, not an unhandled crash.
        _ = await act.Should().NotThrowAsync();

        _ = handler.Captured.Should().Contain(request => request.Path.EndsWith("/renew-lease", StringComparison.Ordinal));
        _ = logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase),
            "a denied renewal must be surfaced loudly rather than silently swallowed");
        _ = logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("lost its lease", StringComparison.OrdinalIgnoreCase),
            "losing the lease is a durable, operator-visible failure");

        // Stale work must never be published: no artifact upload and no completion once the fence
        // this worker holds has been superseded.
        _ = handler.Captured.Should().NotContain(
            request => request.Path.EndsWith("/artifacts", StringComparison.Ordinal),
            "an artifact published under a stale fence would overwrite the new owner's result");
        _ = handler.Captured.Should().NotContain(
            request => request.Path.EndsWith("/complete", StringComparison.Ordinal));
        _ = handler.Captured.Should().NotContain(
            request => request.Path.EndsWith("/fail", StringComparison.Ordinal),
            "a mutation under a dead lease would be denied, so the worker must not send one");

        // Retention semantics: ambiguous local work is preserved, never deleted.
        _ = Directory.Exists(jobDirectory).Should().BeTrue("local work is retained when the outcome is ambiguous");
    }

    [Fact(DisplayName = "A mutation is refused outright when the worker holds no registration")]
    public async Task Mutation_WithoutRegistration_FailsClosedWithoutSendingAnything()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);

        StubHandler handler = new(HttpStatusCode.NoContent);
        RecordingLogger<HttpJobPollerService> logger = new();
        (RenewalRecordingPoller poller, _) = CreatePoller(handler, logger, leaseDurationSeconds: 30);
        poller.PipelineWaitTimeout = TimeSpan.FromSeconds(1);

        // No SetRegisteredService call: the worker has no credential to present.
        await poller.RunAsync(jobId, jobDirectory, Guid.NewGuid(), leaseFence: 1);

        _ = handler.Captured.Should().BeEmpty("an unauthenticated worker must not emit any job mutation");
        _ = Directory.Exists(jobDirectory).Should().BeTrue("nothing was acknowledged, so local work is retained");
    }

    /// <summary>
    /// Asserts the five-header worker mutation contract: each header present exactly once, with the
    /// expected value.
    /// </summary>
    /// <param name="request">The captured outgoing request.</param>
    /// <param name="apiKey">The registry-issued worker credential.</param>
    /// <param name="serviceId">The registry-issued worker service identity.</param>
    /// <param name="leaseToken">The lease token the job was claimed under.</param>
    /// <param name="leaseFence">The fencing counter the job was claimed under.</param>
    private static void AssertSingleValuedWorkerHeaders(
        CapturedRequest request,
        string apiKey,
        Guid serviceId,
        Guid leaseToken,
        long leaseFence)
    {
        AssertSingleHeaderValue(request, WorkerLeaseHeaders.WorkerKey, apiKey);
        AssertSingleHeaderValue(request, WorkerLeaseHeaders.WorkerId, serviceId.ToString());
        AssertSingleHeaderValue(request, WorkerClaimHeaders.ClaimToken, leaseToken.ToString());
        AssertSingleHeaderValue(request, WorkerLeaseHeaders.LeaseToken, leaseToken.ToString());
        AssertSingleHeaderValue(
            request,
            WorkerLeaseHeaders.LeaseFence,
            leaseFence.ToString(CultureInfo.InvariantCulture));
    }

    private static void AssertSingleHeaderValue(CapturedRequest request, string headerName, string expected)
    {
        _ = request.Headers.Should().ContainKey(
            headerName,
            "{0} must present {1}",
            request.Path,
            headerName);
        _ = request.Headers[headerName].Should().ContainSingle(
            "{0} must present exactly one {1} value, not an ambiguous list",
            request.Path,
            headerName).Which.Should().Be(expected);
    }

    private string CreateJobOutput(Guid jobId)
    {
        string jobDirectory = Path.Combine(_workingDirectory, jobId.ToString());
        string outputDirectory = Path.Combine(jobDirectory, "output");
        _ = Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "result.gcode"), "; produced gcode\nG28\n");
        return jobDirectory;
    }

    private static (RenewalRecordingPoller Poller, WorkerStateService WorkerState) CreatePoller(
        StubHandler handler,
        RecordingLogger<HttpJobPollerService> logger,
        int leaseDurationSeconds)
    {
        ServiceCollection services = new();
        _ = services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        ServiceProvider provider = services.BuildServiceProvider();
        WorkerStateService workerState = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:LeaseDurationSeconds"] = leaseDurationSeconds.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        RenewalRecordingPoller poller = new(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider,
            logger,
            workerState,
            configuration,
            handler);
        return (poller, workerState);
    }

    /// <summary>
    /// Drives <see cref="HttpJobPollerService"/>'s job lifecycle for a single synthetic job, holding
    /// the pipeline open until the renewal loop has actually sent (and the stub has captured) a
    /// renew-lease request, so the test never races the background renewal loop.
    /// </summary>
    private sealed class RenewalRecordingPoller : HttpJobPollerService
    {
        private readonly IConfiguration _configuration;
        private readonly StubHandler _handler;
        private readonly IWorkerStateService _workerState;
        private string _jobDirectory = string.Empty;

        public RenewalRecordingPoller(
            IHttpClientFactory httpClientFactory,
            IServiceProvider serviceProvider,
            ILogger<HttpJobPollerService> logger,
            IWorkerStateService workerState,
            IConfiguration configuration,
            StubHandler handler)
            : base(httpClientFactory, serviceProvider, logger, workerState, configuration)
        {
            _configuration = configuration;
            _handler = handler;
            _workerState = workerState;
        }

        /// <summary>
        /// When set, the fake pipeline blocks until the job's own token is cancelled, standing in for
        /// a long-running slice that the lease-renewal loop must be able to interrupt.
        /// </summary>
        public bool WaitForCancellation { get; set; }

        /// <summary>How long the fake pipeline waits for the renewal loop before giving up.</summary>
        public TimeSpan PipelineWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public async Task RunAsync(
            Guid jobId,
            string jobDirectory,
            Guid leaseToken,
            long leaseFence,
            Action<System.Net.Http.Headers.HttpRequestHeaders>? configureDefaults = null)
        {
            _jobDirectory = jobDirectory;
            _configuration["Worker:WorkingDirectory"] ??= Path.GetDirectoryName(jobDirectory);
            _workerState.SetJobWorkDirectory(jobId, jobDirectory);
            using HttpClient client = new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost"),
            };
            configureDefaults?.Invoke(client.DefaultRequestHeaders);

            DistributedSlicingJob job = new()
            {
                Id = jobId,
                ModelFileName = "model.stl",
                EngineType = SlicerEngineType.OrcaSlicer,
                ClaimToken = leaseToken,
                LeaseToken = leaseToken,
                LeaseFence = leaseFence,
            };

            await InvokeHandleJobAsync(job, client);
        }

        protected override async Task<SlicingResult> ExecutePipelineAsync(
            DistributedSlicingJob job,
            IServiceProvider scopeServices,
            CancellationToken ct)
        {
            if (WaitForCancellation)
            {
                // Stand in for a long slice: run until the poller cancels the job because the lease
                // is gone. Cancellation must propagate out of the pipeline.
                await Task.Delay(Timeout.Infinite, ct);
            }

            // Wait for the background renewal loop to actually fire before letting the pipeline
            // "finish", so the test deterministically observes the renewal request rather than
            // racing it.
            _ = await _handler.RenewCapturedTask.WaitAsync(PipelineWaitTimeout, ct);

            return new SlicingResult
            {
                Success = true,
                ResultFileUrl = new Uri(Path.GetFullPath(Path.Combine(_jobDirectory, "output", "result.gcode"))),
                OutputFileSizeBytes = 32,
            };
        }

        protected override string[] GetWorkerCapabilities() => ["orcaslicer"];

        private Task InvokeHandleJobAsync(DistributedSlicingJob job, HttpClient client)
        {
            System.Reflection.MethodInfo method = typeof(HttpJobPollerService).GetMethod(
                "HandleJobAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("HandleJobAsync is missing.");
            return (Task)method.Invoke(this, [job, client, CancellationToken.None])!;
        }
    }

    private sealed class StubHttpClientFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>An outgoing worker request exactly as it appeared on the wire.</summary>
    /// <param name="Method">HTTP method.</param>
    /// <param name="Path">Absolute request path.</param>
    /// <param name="Headers">Every header value the receiving API would observe.</param>
    /// <param name="ContentType">Media type of the body, when there is one.</param>
    /// <param name="Body">Body text for non-multipart requests.</param>
    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
        string? ContentType,
        string Body);

    private sealed class StubHandler(HttpStatusCode renewStatusCode) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<CapturedRequest> _renewCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<CapturedRequest> _captured = [];

        private readonly Lock _capturedLock = new();

        public IReadOnlyList<CapturedRequest> Captured
        {
            get
            {
                lock (_capturedLock)
                {
                    return _captured.ToArray();
                }
            }
        }

        public Task<CapturedRequest> RenewCapturedTask => _renewCaptured.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            // Capture what the receiving API would actually observe: HttpClient has already merged
            // its default headers into the request by the time the handler sees it.
            Dictionary<string, IReadOnlyList<string>> headers = request.Headers.ToDictionary(
                header => header.Key,
                header => (IReadOnlyList<string>)header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            string body = request.Content is null || path.EndsWith("/artifacts", StringComparison.Ordinal)
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            CapturedRequest captured = new(
                request.Method,
                path,
                headers,
                request.Content?.Headers.ContentType?.MediaType,
                body);

            lock (_capturedLock)
            {
                _captured.Add(captured);
            }

            if (path.EndsWith("/renew-lease", StringComparison.Ordinal))
            {
                _ = _renewCaptured.TrySetResult(captured);
                return new HttpResponseMessage(renewStatusCode);
            }

            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent($$"""{"id":"{{Guid.NewGuid()}}"}""", Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>Captures log entries so tests can assert on what was actually logged.</summary>
    /// <typeparam name="T">The category type.</typeparam>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly Lock _entriesLock = new();

        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entriesLock)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_entriesLock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
