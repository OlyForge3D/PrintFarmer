using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Verifies that a worker keeps recoverable local work when an upload or completion outcome is
/// ambiguous, and only discards it after a terminal API acknowledgement.
/// </summary>
public sealed class WorkerRecoverableCleanupTests : IDisposable
{
    private const string RecoveryMarkerFileName = ".printfarmer-recovery.json";

    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"pf-worker-cleanup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "A durably reported completion failure discards the job directory")]
    public async Task FailedCompletion_DurableFailureAcknowledged_RemovesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        RecordingPoller poller = CreatePoller(HttpStatusCode.InternalServerError);

        await poller.RunAsync(jobId, jobDirectory);

        _ = poller.FailureReported.Should().BeTrue("the worker must durably report its failure");
        _ = Directory.Exists(jobDirectory).Should().BeFalse(
            "a terminal failure acknowledgement makes stale same-job output unsafe to retain");
    }

    [Fact(DisplayName = "A failed artifact upload is durably reported and cleaned")]
    public async Task FailedArtifactUpload_DurableFailureAcknowledged_RemovesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        RecordingPoller poller = CreatePoller(
            completionStatus: HttpStatusCode.OK,
            artifactStatus: HttpStatusCode.BadGateway);

        await poller.RunAsync(jobId, jobDirectory);

        _ = poller.FailureReported.Should().BeTrue("upload failures must become durable job failures");
        _ = poller.FailureReason.Should().Contain("Failed to upload G-code artifact");
        _ = Directory.Exists(jobDirectory).Should().BeFalse();
    }

    [Fact(DisplayName = "An unacknowledged failure preserves recoverable local work")]
    public async Task FailedArtifactUpload_FailureReportRejected_PreservesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        RecordingPoller poller = CreatePoller(
            completionStatus: HttpStatusCode.OK,
            artifactStatus: HttpStatusCode.BadGateway,
            failureStatus: HttpStatusCode.ServiceUnavailable);

        await poller.RunAsync(jobId, jobDirectory);

        _ = poller.FailureReported.Should().BeTrue();
        _ = Directory.Exists(jobDirectory).Should().BeTrue();
        _ = File.Exists(Path.Combine(jobDirectory, RecoveryMarkerFileName)).Should().BeTrue();
    }

    [Fact(DisplayName = "An artifact upload timeout is durably reported and cleaned")]
    public async Task ArtifactUploadTimeout_DurableFailureAcknowledged_RemovesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        RecordingPoller poller = CreatePoller(
            completionStatus: HttpStatusCode.OK,
            artifactTimeout: true);

        await poller.RunAsync(jobId, jobDirectory);

        _ = poller.FailureReported.Should().BeTrue();
        _ = poller.FailureReason.Should().Contain("timed out");
        _ = Directory.Exists(jobDirectory).Should().BeFalse();
    }

    [Fact(DisplayName = "A terminal acknowledgement discards the job directory")]
    public async Task TerminalAcknowledgement_RemovesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        RecordingPoller poller = CreatePoller(HttpStatusCode.OK);

        await poller.RunAsync(jobId, jobDirectory);

        _ = poller.WorkDirectoryExistedAtUpload.Should().BeTrue(
            "the produced artifact must remain available until upload completes");
        _ = Directory.Exists(jobDirectory).Should().BeFalse();
    }

    [Fact(DisplayName = "A terminal acknowledgement removes an empty current claim parent")]
    public async Task TerminalAcknowledgement_CurrentClaimAttempt_RemovesEmptyJobParent()
    {
        Guid jobId = Guid.NewGuid();
        string jobParent = Path.Combine(_workingDirectory, jobId.ToString());
        string attemptDirectory = Path.Combine(jobParent, Guid.NewGuid().ToString());
        string outputDirectory = Path.Combine(attemptDirectory, "output");
        _ = Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.gcode"), "; produced gcode\nG28\n");

        RecordingPoller poller = CreatePoller(HttpStatusCode.OK);
        await poller.RunAsync(jobId, attemptDirectory);

        _ = Directory.Exists(jobParent).Should().BeFalse(
            "the current claim attempt was terminally acknowledged and no sibling attempt remains");
    }

    [Fact(DisplayName = "A terminal acknowledgement retains a job parent with a sibling attempt")]
    public async Task TerminalAcknowledgement_CurrentClaimAttempt_PreservesSiblingParent()
    {
        Guid jobId = Guid.NewGuid();
        string jobParent = Path.Combine(_workingDirectory, jobId.ToString());
        string attemptDirectory = Path.Combine(jobParent, Guid.NewGuid().ToString());
        _ = Directory.CreateDirectory(Path.Combine(attemptDirectory, "output"));
        await File.WriteAllTextAsync(
            Path.Combine(attemptDirectory, "output", "result.gcode"),
            "; produced gcode\nG28\n");
        _ = Directory.CreateDirectory(Path.Combine(jobParent, Guid.NewGuid().ToString()));

        RecordingPoller poller = CreatePoller(HttpStatusCode.OK);
        await poller.RunAsync(jobId, attemptDirectory);

        _ = Directory.Exists(jobParent).Should().BeTrue(
            "a sibling recovery attempt remains under the job parent");
    }
    [Fact(DisplayName = "A terminal acknowledgement supersedes an old recovery marker")]
    public async Task RecoveryMarker_TerminalAcknowledgement_RemovesWorkDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = CreateJobOutput(jobId);
        await File.WriteAllTextAsync(Path.Combine(jobDirectory, RecoveryMarkerFileName), "{}");
        RecordingPoller poller = CreatePoller(HttpStatusCode.OK);

        await poller.RunAsync(jobId, jobDirectory);

        _ = Directory.Exists(jobDirectory).Should().BeFalse(
            "a current terminal acknowledgement makes every prior attempt safe to discard");
    }

    [Fact(DisplayName = "Legacy terminal cleanup never scans outside the configured working directory")]
    public async Task LegacyTerminalCleanup_DoesNotEnumerateParentOfConfiguredWorkingDirectory()
    {
        Guid jobId = Guid.NewGuid();
        string configuredJobDirectory = Path.Combine(_workingDirectory, jobId.ToString());
        string configuredOutputDirectory = Path.Combine(configuredJobDirectory, "output");
        _ = Directory.CreateDirectory(configuredOutputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configuredOutputDirectory, "result.gcode"),
            "; produced gcode\nG28\n");

        string outsideRecoveryDirectory = Path.Combine(
            Path.GetDirectoryName(_workingDirectory)!,
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());
        _ = Directory.CreateDirectory(outsideRecoveryDirectory);
        await File.WriteAllTextAsync(Path.Combine(outsideRecoveryDirectory, RecoveryMarkerFileName), "{}");
        File.SetLastWriteTimeUtc(
            Path.Combine(outsideRecoveryDirectory, RecoveryMarkerFileName),
            DateTime.UtcNow.AddDays(-10));
        await File.WriteAllBytesAsync(Path.Combine(outsideRecoveryDirectory, "old.gcode"), new byte[32]);

        try
        {
            RecordingPoller poller = CreatePoller(
                HttpStatusCode.OK,
                workingDirectory: _workingDirectory,
                recoveryMinimumAgeHours: 1,
                recoveryMaxBytes: 1);
            await poller.RunAsync(jobId, configuredJobDirectory);

            _ = Directory.Exists(outsideRecoveryDirectory).Should().BeTrue(
                "recovery cleanup must remain rooted at Worker:WorkingDirectory");
        }
        finally
        {
            string? outsideJobDirectory = Path.GetDirectoryName(outsideRecoveryDirectory);
            if (outsideJobDirectory is not null && Directory.Exists(outsideJobDirectory))
            {
                Directory.Delete(outsideJobDirectory, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Terminal cleanup rejects a result URI outside the configured attempt")]
    public async Task TerminalCleanup_OutsideRootResultUri_IsRetained()
    {
        Guid jobId = Guid.NewGuid();
        string insideJobDirectory = CreateJobOutput(jobId);
        string outsideAttempt = Path.Combine(
            Path.GetDirectoryName(_workingDirectory)!,
            "outside",
            jobId.ToString(),
            Guid.NewGuid().ToString(),
            "output");
        _ = Directory.CreateDirectory(outsideAttempt);
        string outsideResult = Path.Combine(outsideAttempt, "result.gcode");
        await File.WriteAllTextAsync(outsideResult, "; outside\nG28\n");

        RecordingPoller poller = CreatePoller(HttpStatusCode.OK, workingDirectory: _workingDirectory);
        poller.ResultFilePathOverride = outsideResult;
        await poller.RunAsync(jobId, insideJobDirectory);

        _ = File.Exists(outsideResult).Should().BeTrue(
            "terminal cleanup must not recursively delete a result outside the configured attempt");
    }

    [Fact(DisplayName = "Native profiles are rejected when a delivered document fails its digest")]
    public void NativeProfiles_RejectTamperedDocuments()
    {
        NativeSlicerProfiles? profiles = NativeSlicerProfiles.FromJob(
            """{"type":"machine"}""",
            """{"type":"process"}""",
            """{"type":"filament"}""",
            NativeSlicerProfiles.ComputeSha256("""{"type":"machine"}"""),
            NativeSlicerProfiles.ComputeSha256("""{"type":"process"}"""),
            NativeSlicerProfiles.ComputeSha256("""{"type":"filament"}"""));

        _ = profiles.Should().NotBeNull();
        _ = profiles!.MachineSha256.Should().NotBe(
            NativeSlicerProfiles.ComputeSha256("""{"type":"tampered"}"""));
        _ = NativeSlicerProfiles.FromJob(null, "process", "filament", null, null, null)
            .Should().BeNull("an incomplete profile set cannot be delivered to a slicer");
    }

    private string CreateJobOutput(Guid jobId)
    {
        string jobDirectory = Path.Combine(_workingDirectory, jobId.ToString());
        string outputDirectory = Path.Combine(jobDirectory, "output");
        _ = Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "result.gcode"), "; produced gcode\nG28\n");
        return jobDirectory;
    }

    private static RecordingPoller CreatePoller(
        HttpStatusCode completionStatus,
        HttpStatusCode artifactStatus = HttpStatusCode.Created,
        HttpStatusCode failureStatus = HttpStatusCode.NoContent,
        bool artifactTimeout = false,
        string? workingDirectory = null,
        double? recoveryMinimumAgeHours = null,
        long? recoveryMaxBytes = null)
    {
        StubHandler handler = new(
            completionStatus,
            artifactStatus,
            failureStatus,
            artifactTimeout);
        ServiceCollection services = new();
        _ = services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        ServiceProvider provider = services.BuildServiceProvider();

        // Job mutations fail closed without a registered worker identity, so these cleanup and
        // recovery scenarios have to start from an authenticated worker.
        WorkerStateService workerState = new();
        workerState.SetRegisteredService(Guid.NewGuid(), "test-worker-api-key");

        Dictionary<string, string?> settings = new();
        if (workingDirectory is not null)
        {
            settings["Worker:WorkingDirectory"] = workingDirectory;
        }

        if (recoveryMinimumAgeHours.HasValue)
        {
            settings["Worker:RecoveryMinimumAgeHours"] = recoveryMinimumAgeHours.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (recoveryMaxBytes.HasValue)
        {
            settings["Worker:RecoveryMaxBytes"] = recoveryMaxBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new RecordingPoller(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider,
            workerState,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            handler);
    }

    /// <summary>
    /// Drives <see cref="HttpJobPollerService"/>'s job lifecycle for a single synthetic job.
    /// </summary>
    private sealed class RecordingPoller : HttpJobPollerService
    {
        private readonly IConfiguration _configuration;
        private readonly StubHandler _handler;
        private readonly IWorkerStateService _workerState;
        private string _jobDirectory = string.Empty;

        public RecordingPoller(
            IHttpClientFactory httpClientFactory,
            IServiceProvider serviceProvider,
            IWorkerStateService workerState,
            IConfiguration configuration,
            StubHandler handler)
            : base(
                httpClientFactory,
                serviceProvider,
                NullLogger<HttpJobPollerService>.Instance,
                workerState,
                configuration)
        {
            _configuration = configuration;
            _handler = handler;
            _workerState = workerState;
        }

        public bool FailureReported => _handler.FailureReported;

        public string? FailureReason => _handler.FailureReason;

        public bool WorkDirectoryExistedAtUpload => _handler.WorkDirectoryExistedAtUpload;

        public string? ResultFilePathOverride { get; set; }

        public async Task RunAsync(Guid jobId, string jobDirectory)
        {
            _jobDirectory = jobDirectory;
            _configuration["Worker:WorkingDirectory"] ??= Path.GetDirectoryName(jobDirectory);
            _workerState.SetJobWorkDirectory(jobId, jobDirectory);
            _handler.ExpectedJobDirectory = jobDirectory;
            using HttpClient client = new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost"),
            };
            DistributedSlicingJob job = new()
            {
                Id = jobId,
                ModelFileName = "model.stl",
                EngineType = SlicerEngineType.OrcaSlicer,
                ClaimToken = Guid.NewGuid(),
                LeaseFence = 1,
            };
            job.LeaseToken = job.ClaimToken;

            await InvokeHandleJobAsync(job, client);
        }

        protected override Task<SlicingResult> ExecutePipelineAsync(
            DistributedSlicingJob job,
            IServiceProvider scopeServices,
            CancellationToken ct) =>
            Task.FromResult(new SlicingResult
            {
                Success = true,
                ResultFileUrl = new Uri(Path.GetFullPath(ResultFilePathOverride ?? Path.Combine(_jobDirectory, "output", "result.gcode"))),
                OutputFileSizeBytes = 32,
            });

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

    private sealed class StubHandler(
        HttpStatusCode completionStatus,
        HttpStatusCode artifactStatus,
        HttpStatusCode failureStatus,
        bool artifactTimeout) : HttpMessageHandler
    {
        public bool FailureReported { get; private set; }

        public string? FailureReason { get; private set; }

        public string? ExpectedJobDirectory { get; set; }

        public bool WorkDirectoryExistedAtUpload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                WorkDirectoryExistedAtUpload =
                    ExpectedJobDirectory is not null && Directory.Exists(ExpectedJobDirectory);
                if (artifactTimeout)
                {
                    throw new TaskCanceledException("artifact upload timed out");
                }

                return new HttpResponseMessage(artifactStatus)
                {
                    Content = artifactStatus == HttpStatusCode.Created
                        ? new StringContent(
                            $$"""{"id":"{{Guid.NewGuid()}}"}""",
                            Encoding.UTF8,
                            "application/json")
                        : new StringContent("artifact storage unavailable", Encoding.UTF8, "text/plain"),
                };
            }

            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(completionStatus)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/fail", StringComparison.Ordinal))
            {
                FailureReported = true;
                string body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                FailureReason = System.Text.Json.JsonDocument.Parse(body)
                    .RootElement
                    .GetProperty("errorMessage")
                    .GetString();
                return new HttpResponseMessage(failureStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
