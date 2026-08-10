using System.Globalization;
using System.Net;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

[Collection("Model download environment")]
public sealed class ModelDownloadRequestTests : IDisposable
{
    private const string ApiBaseUrl = "https://slicer.example.test:5246";
    private readonly string _workingDirectory =
        Path.Join(Path.GetTempPath(), $"printfarmer-worker-request-{Guid.NewGuid():N}");

    [Fact]
    public void CreateModelDownloadRequest_ClaimScopedRoute_UsesApiBaseAndClaimHeaders()
    {
        Guid jobId = Guid.NewGuid();
        Guid serviceId = Guid.NewGuid();
        Guid claimToken = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        var state = new WorkerStateService();
        state.SetRegisteredService(serviceId, "worker-secret");
        using var httpClient = new HttpClient();
        OrcaSlicingPipelineService service = CreateService(httpClient, state);

        using HttpRequestMessage request = service.CreateModelDownloadRequest(
            $"/api/slice/{jobId:D}/models/1",
            jobId,
            1,
            claimToken,
            leaseToken,
            leaseFence: 7);

        request.RequestUri.Should().Be(
            new Uri($"{ApiBaseUrl}/api/slice/{jobId:D}/models/1"));
        request.Headers.GetValues("X-Worker-Key").Should().ContainSingle().Which
            .Should().Be("worker-secret");
        request.Headers.GetValues("X-Worker-Id").Should().ContainSingle().Which
            .Should().Be(serviceId.ToString());
        request.Headers.GetValues(WorkerClaimHeaders.ClaimToken).Should().ContainSingle().Which
            .Should().Be(claimToken.ToString());
        request.Headers.GetValues(WorkerLeaseHeaders.LeaseToken).Should().ContainSingle().Which
            .Should().Be(leaseToken.ToString());
        request.Headers.GetValues(WorkerLeaseHeaders.LeaseFence).Should().ContainSingle().Which
            .Should().Be("7");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("https://models.example.test/api/models/1")]
    [InlineData("//169.254.169.254/latest/meta-data")]
    public void CreateModelDownloadRequest_NonClaimRoute_ThrowsInvalidOperationException(
        string modelRoute)
    {
        Guid jobId = Guid.NewGuid();
        using var httpClient = new HttpClient();
        OrcaSlicingPipelineService service = CreateService(httpClient);

        Action createRequest = () => service.CreateModelDownloadRequest(
            modelRoute,
            jobId,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            leaseFence: 1);

        createRequest.Should().Throw<InvalidOperationException>()
            .WithMessage("*exact API-relative route*");
    }

    [Fact]
    public async Task FetchStlFileAsync_RedirectToDisallowedHost_ThrowsWithoutFollowing()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location =
                new Uri("http://169.254.169.254/latest/meta-data");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(httpClient);
        DistributedSlicingJob job = CreateJob();

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*redirects are not allowed*");
        handler.RequestCount.Should().Be(1);
        File.Exists(Path.Join(_workingDirectory, job.ModelFileName)).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"..\..\evil.stl")]
    [InlineData("../../evil.stl")]
    [InlineData("/tmp/evil.stl")]
    [InlineData(@"C:\temp\evil.stl")]
    public async Task FetchStlFileAsync_TraversalOrAbsoluteFileName_RejectsBeforeRequest(
        string fileName)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateOkResponse([1, 2, 3])));
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(httpClient);
        DistributedSlicingJob job = CreateJob(fileName);

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bare file name*");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void ResolveModelDestinationPath_BareFileName_ReturnsContainedAbsolutePath()
    {
        string destination = OrcaSlicingPipelineService.ResolveModelDestinationPath(
            _workingDirectory,
            "safe model.stl");

        destination.Should().Be(
            Path.GetFullPath(Path.Join(_workingDirectory, "safe_model.stl")));
        Path.GetDirectoryName(destination).Should().Be(Path.GetFullPath(_workingDirectory));
    }

    [Fact]
    public async Task FetchStlFileAsync_UnknownLengthOversizedResponse_ThrowsAndDeletesPartialFile()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(payload),
            }));
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            maxDownloadBytes: "4");
        DistributedSlicingJob job = CreateJob();

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*4-byte limit*");
        File.Exists(Path.Join(_workingDirectory, job.ModelFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task FetchStlFileAsync_ResponseExceedsTimeout_ThrowsTimeoutException()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateOkResponse([1]);
        });
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            downloadTimeoutSeconds: "1");
        DistributedSlicingJob job = CreateJob();

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*1-second timeout*");
    }

    [Fact]
    public async Task FetchStlFileAsync_BoundedResponse_WritesSanitizedFileInsideWorkDirectory()
    {
        byte[] payload = [1, 2, 3, 4];
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateOkResponse(payload)));
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            maxDownloadBytes: payload.Length.ToString(CultureInfo.InvariantCulture));
        DistributedSlicingJob job = CreateJob("model file.stl");

        string path = await service.FetchStlFileAsync(
            job,
            _workingDirectory,
            CancellationToken.None);

        path.Should().Be(Path.Join(_workingDirectory, "model_file.stl"));
        (await File.ReadAllBytesAsync(path)).Should().BeEquivalentTo(payload);
        handler.LastRequestUri.Should().Be(
            new Uri($"{ApiBaseUrl}/api/slice/{job.Id:D}/model"));
    }

    [Fact]
    public async Task FetchStlFileAsync_PollerAbsolutizedClaimRoute_DownloadsFromConfiguredOrigin()
    {
        byte[] payload = "solid poller"u8.ToArray();
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateOkResponse(payload)));
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(httpClient);
        DistributedSlicingJob job = CreateJob("poller.stl");
        job.ModelFileUrl = HttpJobPollerService.ResolveModelFileUri(
            new Uri(ApiBaseUrl),
            job.ModelFileUrl.OriginalString);

        string path = await service.FetchStlFileAsync(
            job,
            _workingDirectory,
            CancellationToken.None);

        (await File.ReadAllBytesAsync(path)).Should().BeEquivalentTo(payload);
        handler.LastRequestUri.Should().Be(job.ModelFileUrl);
    }

    [Fact]
    public async Task FetchMultipleModelsAsync_ValidClaimRoutes_DownloadsEveryModel()
    {
        DistributedSlicingJob job = CreateJob();
        List<string> routes =
        [
            $"/api/slice/{job.Id:D}/models/0",
            $"/api/slice/{job.Id:D}/models/1",
        ];
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            byte[] payload = request.RequestUri!.AbsolutePath.EndsWith("/0", StringComparison.Ordinal)
                ? "solid zero"u8.ToArray()
                : "solid one"u8.ToArray();
            return Task.FromResult(CreateOkResponse(payload));
        });
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(httpClient);

        List<string> paths = await service.FetchMultipleModelsAsync(
            job.Id,
            routes,
            job.ClaimToken,
            job.LeaseToken,
            job.LeaseFence,
            _workingDirectory,
            CancellationToken.None);

        paths.Should().HaveCount(2);
        (await File.ReadAllTextAsync(paths[0])).Should().Be("solid zero");
        (await File.ReadAllTextAsync(paths[1])).Should().Be("solid one");
        handler.RequestUris.Should().Equal(
            new Uri($"{ApiBaseUrl}/api/slice/{job.Id:D}/models/0"),
            new Uri($"{ApiBaseUrl}/api/slice/{job.Id:D}/models/1"));
    }

    [Fact]
    public async Task FetchStlFileAsync_KnownContentLengthExceedsLimit_RejectsBeforeWriting()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            HttpResponseMessage response = CreateOkResponse(payload);
            response.Content.Headers.ContentLength = payload.LongLength;
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            maxDownloadBytes: "4");
        DistributedSlicingJob job = CreateJob("known-oversized.stl");

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*4-byte limit*");
        File.Exists(Path.Join(_workingDirectory, job.ModelFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task FetchStlFileAsync_DigestMismatch_DeletesDownloadedFile()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateOkResponse("solid digest"u8.ToArray())));
        using var httpClient = new HttpClient(handler);
        OrcaSlicingPipelineService service = CreateService(httpClient);
        DistributedSlicingJob job = CreateJob("digest.stl");
        job.ModelSha256 = new string('0', 64);

        Func<Task> fetch = () =>
            service.FetchStlFileAsync(job, _workingDirectory, CancellationToken.None);

        await fetch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match the digest*");
        File.Exists(Path.Join(_workingDirectory, job.ModelFileName)).Should().BeFalse();
    }

    [Fact]
    public void CreateModelDownloadHandler_DefaultHandler_DisablesRedirects()
    {
        using SocketsHttpHandler handler = Program.CreateModelDownloadHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Theory]
    [InlineData("file:///tmp/models")]
    [InlineData("ftp://slicer.example.test")]
    [InlineData("https://user:password@slicer.example.test")]
    [InlineData("https://slicer.example.test/base")]
    [InlineData("https://slicer.example.test?tenant=one")]
    public void Constructor_UntrustedApiBase_ThrowsInvalidOperationException(
        string apiBaseUrl)
    {
        using var httpClient = new HttpClient();

        Action construct = () => CreateService(httpClient, apiBaseUrl: apiBaseUrl);

        construct.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute HTTP(S) origin*");
    }

    [Theory]
    [InlineData("0", "30")]
    [InlineData("not-a-number", "30")]
    [InlineData("1024", "0")]
    [InlineData("1024", "3601")]
    public void Constructor_InvalidDownloadLimit_ThrowsInvalidOperationException(
        string maxDownloadBytes,
        string downloadTimeoutSeconds)
    {
        using var httpClient = new HttpClient();

        Action construct = () => CreateService(
            httpClient,
            maxDownloadBytes: maxDownloadBytes,
            downloadTimeoutSeconds: downloadTimeoutSeconds);

        construct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_OmittedDownloadLimits_UsesSecureDefaults()
    {
        using var httpClient = new HttpClient();
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            maxDownloadBytes: null,
            downloadTimeoutSeconds: null);

        service.ModelDownloadMaxBytes.Should().Be(512L * 1024 * 1024);
        service.ModelDownloadTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Constructor_MaximumDownloadTimeout_IsAccepted()
    {
        using var httpClient = new HttpClient();
        OrcaSlicingPipelineService service = CreateService(
            httpClient,
            downloadTimeoutSeconds: "3600");

        service.ModelDownloadTimeout.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Constructor_WorkerApiBaseEnvironmentOverride_IsTrustedOrigin()
    {
        const string variableName = "WORKER_API_BASE_URL";
        const string environmentApiBaseUrl = "https://environment-api.example:8443";
        string? previousValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, environmentApiBaseUrl);
            using var httpClient = new HttpClient();
            OrcaSlicingPipelineService service = CreateService(
                httpClient,
                apiBaseUrl: null);
            DistributedSlicingJob job = CreateJob();

            using HttpRequestMessage request = service.CreateModelDownloadRequest(
                job.ModelFileUrl.OriginalString,
                job.Id,
                modelIndex: null,
                job.ClaimToken,
                job.LeaseToken,
                job.LeaseFence);

            service.ApiBaseUri.Should().Be(new Uri($"{environmentApiBaseUrl}/"));
            request.RequestUri.Should().Be(
                new Uri($"{environmentApiBaseUrl}/api/slice/{job.Id:D}/model"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public void AddSlicingPipelineServices_InterfaceResolution_UsesConfiguredTypedClient()
    {
        ServiceCollection services = CreatePipelineServiceCollection(CreateConfiguration());
        Program.AddSlicingPipelineServices(services);
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        Program.ValidateSlicingPipelineConfiguration(provider);
        using IServiceScope scope = provider.CreateScope();
        ISlicingPipelineService pipeline =
            scope.ServiceProvider.GetRequiredService<ISlicingPipelineService>();
        ISlicingPipelineService pipelineAgain =
            scope.ServiceProvider.GetRequiredService<ISlicingPipelineService>();

        pipelineAgain.Should().BeSameAs(pipeline);
        pipeline.Should().BeOfType<OrcaSlicingPipelineService>();
        var concrete = (OrcaSlicingPipelineService)pipeline;
        concrete.ModelDownloadHttpClientTimeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void ValidateSlicingPipelineConfiguration_InvalidSettings_FailsEagerly()
    {
        ServiceCollection services = CreatePipelineServiceCollection(
            CreateConfiguration(maxDownloadBytes: "0"));
        Program.AddSlicingPipelineServices(services);
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        Action validate = () => Program.ValidateSlicingPipelineConfiguration(provider);

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*ModelDownloadMaxBytes*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private OrcaSlicingPipelineService CreateService(
        HttpClient httpClient,
        WorkerStateService? state = null,
        string? apiBaseUrl = ApiBaseUrl,
        string? maxDownloadBytes = "1024",
        string? downloadTimeoutSeconds = "30")
    {
        state ??= new WorkerStateService();
        if (state.GetWorkerState().RegisteredServiceId is null)
        {
            state.SetRegisteredService(Guid.NewGuid(), "worker-secret");
        }

        IConfiguration configuration = CreateConfiguration(
            apiBaseUrl,
            maxDownloadBytes,
            downloadTimeoutSeconds);
        return new OrcaSlicingPipelineService(
            httpClient,
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            state);
    }

    private IConfiguration CreateConfiguration(
        string? apiBaseUrl = ApiBaseUrl,
        string? maxDownloadBytes = "1024",
        string? downloadTimeoutSeconds = "30")
    {
        Dictionary<string, string?> values = new()
        {
            ["Worker:WorkingDirectory"] = _workingDirectory,
        };

        if (apiBaseUrl is not null)
        {
            values["SlicerApi:BaseUrl"] = apiBaseUrl;
        }

        if (maxDownloadBytes is not null)
        {
            values["Worker:ModelDownloadMaxBytes"] = maxDownloadBytes;
        }

        if (downloadTimeoutSeconds is not null)
        {
            values["Worker:ModelDownloadTimeoutSeconds"] = downloadTimeoutSeconds;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private ServiceCollection CreatePipelineServiceCollection(IConfiguration configuration)
    {
        var state = new WorkerStateService();
        state.SetRegisteredService(Guid.NewGuid(), "worker-secret");

        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddSingleton(configuration);
        _ = services.AddSingleton<IWorkerStateService>(state);
        _ = services.AddScoped<IProgressReporter>(_ => new NullProgressReporter());
        return services;
    }

    private static DistributedSlicingJob CreateJob(string fileName = "model.stl")
    {
        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1,
            ModelFileName = fileName,
        };
        job.ModelFileUrl = new Uri(
            $"/api/slice/{job.Id:D}/model",
            UriKind.Relative);
        return job;
    }

    private static HttpResponseMessage CreateOkResponse(byte[] payload) => new(
        HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            RequestUris.Add(request.RequestUri!);
            HttpResponseMessage response = await responder(request, cancellationToken);
            _responses.Add(response);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (HttpResponseMessage response in _responses)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [CollectionDefinition("Model download environment", DisableParallelization = true)]
    public sealed class ModelDownloadEnvironmentCollection;

    private sealed class NullProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
