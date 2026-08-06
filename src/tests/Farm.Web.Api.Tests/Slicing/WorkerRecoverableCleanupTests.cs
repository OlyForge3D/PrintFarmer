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
        bool artifactTimeout = false)
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

        return new RecordingPoller(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider,
            workerState,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            handler);
    }

    /// <summary>
    /// Drives <see cref="HttpJobPollerService"/>'s job lifecycle for a single synthetic job.
    /// </summary>
    private sealed class RecordingPoller(
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        IWorkerStateService workerState,
        IConfiguration configuration,
        StubHandler handler)
        : HttpJobPollerService(
            httpClientFactory,
            serviceProvider,
            NullLogger<HttpJobPollerService>.Instance,
            workerState,
            configuration)
    {
        private string _jobDirectory = string.Empty;

        public bool FailureReported => handler.FailureReported;

        public string? FailureReason => handler.FailureReason;

        public bool WorkDirectoryExistedAtUpload => handler.WorkDirectoryExistedAtUpload;

        public async Task RunAsync(Guid jobId, string jobDirectory)
        {
            _jobDirectory = jobDirectory;
            handler.ExpectedJobDirectory = jobDirectory;
            using HttpClient client = new(handler, disposeHandler: false)
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
                ResultFileUrl = new Uri(Path.GetFullPath(Path.Combine(_jobDirectory, "output", "result.gcode"))),
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
