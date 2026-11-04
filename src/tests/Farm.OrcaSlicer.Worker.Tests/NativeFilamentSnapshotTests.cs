using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class NativeFilamentSnapshotTests : IDisposable
{
    private const string MachineJson = """{"name":"machine"}""";
    private const string ProcessJson = """{"name":"process"}""";
    private const string FirstJson = """{"name":"Same name","nozzle_temperature":["215"]}""";
    private const string SecondJson = """{"name":"Same name","nozzle_temperature":["245"]}""";
    private readonly string _workDir = Path.Join(
        Directory.GetCurrentDirectory(), "TestResults", $"native-filament-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExecuteAsync_ExplicitClaimSnapshots_ReachesWriterWithoutNamedProfileResolution()
    {
        NativeFilamentProfile[] snapshots = [Snapshot(SecondJson), Snapshot(FirstJson)];
        var claim = new WorkerSliceJobResponse
        {
            Id = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1,
            ModelFileUrl = "/api/slice/model",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = """{"machineProfileName":"machine","processProfileName":"process","filamentProfileName":"Same name"}""",
            MachineProfileJson = MachineJson,
            ProcessProfileJson = ProcessJson,
            FilamentProfileJson = FirstJson,
            ExtruderFilamentProfilesJson = JsonSerializer.Serialize(snapshots),
        };
        using var handler = new ClaimHandler(claim);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlicerApi:BaseUrl"] = "http://localhost",
            ["Worker:PollIntervalSeconds"] = "1",
        }).Build();
        var workerState = new WorkerStateService();
        workerState.SetRegisteredService(Guid.NewGuid(), "test-worker-key");
        int namedResolutionCount = 0;
        var services = new ServiceCollection();
        _ = services.AddSingleton<ISlicerProfilesService>(_ =>
        {
            Interlocked.Increment(ref namedResolutionCount);
            throw new InvalidOperationException("Explicit snapshots must not resolve profile names.");
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        _ = Directory.CreateDirectory(_workDir);
        using var poller = new SnapshotPoller(
            new ClaimClientFactory(handler), provider, workerState, configuration, _workDir);

        await poller.StartAsync(CancellationToken.None);
        try
        {
            (DistributedSlicingJob job, Dictionary<string, string> paths) =
                await poller.Written.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _ = job.NativeProfiles!.ExtruderFilaments.Should().Equal(snapshots);
            _ = namedResolutionCount.Should().Be(0);
            _ = paths["filament"].Should().Be(string.Join(";", paths["filament_0"], paths["filament_1"]));
            _ = (await File.ReadAllTextAsync(paths["filament_0"])).Should().Be(SecondJson);
            _ = (await File.ReadAllTextAsync(paths["filament_1"])).Should().Be(FirstJson);
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteNativeProfilesAsync_ClaimRoundTrip_PreservesOrderedDocumentsAndPaths(bool reverse)
    {
        NativeFilamentProfile[] snapshots = reverse
            ? [Snapshot(SecondJson), Snapshot(FirstJson)]
            : [Snapshot(FirstJson), Snapshot(SecondJson)];
        var claim = new WorkerSliceJobResponse
        {
            MachineProfileJson = MachineJson,
            ProcessProfileJson = ProcessJson,
            FilamentProfileJson = FirstJson,
            ExtruderFilamentProfilesJson = JsonSerializer.Serialize(snapshots, JsonSerializerOptions.Web),
        };
        WorkerSliceJobResponse roundTripped = JsonSerializer.Deserialize<WorkerSliceJobResponse>(
            JsonSerializer.Serialize(claim, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        NativeSlicerProfiles profiles = NativeSlicerProfiles.FromJob(
            roundTripped.MachineProfileJson, roundTripped.ProcessProfileJson, roundTripped.FilamentProfileJson,
            roundTripped.MachineProfileSha256, roundTripped.ProcessProfileSha256, roundTripped.FilamentProfileSha256,
            roundTripped.ExtruderFilamentProfilesJson)!;
        _ = Directory.CreateDirectory(_workDir);

        Dictionary<string, string> paths = await OrcaSlicingPipelineService.WriteNativeProfilesAsync(
            profiles, _workDir, CancellationToken.None);

        _ = profiles.ExtruderFilaments.Should().Equal(snapshots);
        _ = paths["filament"].Should().Be(string.Join(";", paths["filament_0"], paths["filament_1"]));
        for (int i = 0; i < snapshots.Length; i++)
        {
            string path = paths[$"filament_{i}"];
            _ = Path.GetFileName(path).Should().Be($"filament_{i}.json");
            string written = await File.ReadAllTextAsync(path);
            _ = written.Should().Be(snapshots[i].Json);
            _ = NativeSlicerProfiles.ComputeSha256(written).Should().Be(snapshots[i].Sha256);
        }

        _ = (await File.ReadAllTextAsync(paths["machine"])).Should().Be(MachineJson);
        _ = (await File.ReadAllTextAsync(paths["process"])).Should().Be(ProcessJson);
        _ = File.Exists(Path.Join(_workDir, "filament.json")).Should().BeFalse();
    }

    [Fact]
    public async Task WriteNativeProfilesAsync_SingleExplicitSnapshot_UsesIndexedPath()
    {
        NativeSlicerProfiles profiles = LegacyProfiles() with { ExtruderFilaments = [Snapshot(SecondJson)] };
        _ = Directory.CreateDirectory(_workDir);

        Dictionary<string, string> paths = await OrcaSlicingPipelineService.WriteNativeProfilesAsync(
            profiles, _workDir, CancellationToken.None);

        _ = paths["filament"].Should().Be(Path.Join(_workDir, "filament_0.json"));
        _ = paths["filament_0"].Should().Be(paths["filament"]);
        _ = (await File.ReadAllTextAsync(paths["filament"])).Should().Be(SecondJson);
        _ = File.Exists(Path.Join(_workDir, "filament.json")).Should().BeFalse();
    }

    [Fact]
    public async Task WriteNativeProfilesAsync_CorruptSecondDigest_RejectsBeforeWritingAnyProfiles()
    {
        NativeSlicerProfiles profiles = LegacyProfiles() with
        {
            ExtruderFilaments = [Snapshot(FirstJson), new NativeFilamentProfile(SecondJson, new string('0', 64))]
        };
        _ = Directory.CreateDirectory(_workDir);

        Func<Task> write = () => OrcaSlicingPipelineService.WriteNativeProfilesAsync(
            profiles, _workDir, CancellationToken.None);

        _ = await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*filament_1*digest*");
        _ = Directory.GetFiles(_workDir).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteNativeProfilesAsync_EmptyExplicitList_RejectsRatherThanUsingLegacyFilament()
    {
        NativeSlicerProfiles profiles = LegacyProfiles() with { ExtruderFilaments = [] };
        _ = Directory.CreateDirectory(_workDir);

        Func<Task> write = () => OrcaSlicingPipelineService.WriteNativeProfilesAsync(
            profiles, _workDir, CancellationToken.None);

        _ = await write.Should().ThrowAsync<InvalidOperationException>();
        _ = Directory.GetFiles(_workDir).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteNativeProfilesAsync_LegacySixArgumentProfiles_PreservesSingleFile()
    {
        NativeSlicerProfiles profiles = LegacyProfiles();
        _ = Directory.CreateDirectory(_workDir);

        Dictionary<string, string> paths = await OrcaSlicingPipelineService.WriteNativeProfilesAsync(
            profiles, _workDir, CancellationToken.None);

        _ = profiles.ExtruderFilaments.Should().BeNull();
        _ = paths["filament"].Should().Be(Path.Join(_workDir, "filament.json"));
        _ = (await File.ReadAllTextAsync(paths["filament"])).Should().Be(FirstJson);
        _ = paths.Should().NotContainKey("filament_0");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"json\":\"{}\",\"sha256\":\"bad\"}]")]
    [InlineData("not json")]
    public void FromJob_MalformedExplicitList_RejectsRatherThanFallingBack(string snapshotsJson)
    {
        Action read = () => NativeSlicerProfiles.FromJob(
            MachineJson, ProcessJson, FirstJson, null, null, null, snapshotsJson);

        _ = read.Should().Throw<Exception>()
            .Where(exception => exception is JsonException || exception is InvalidOperationException);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void FromJob_MalformedSnapshotDocument_RejectsRatherThanFallingBack(string document)
    {
        string snapshotsJson = JsonSerializer.Serialize(new[] { Snapshot(document) });
        Action read = () => NativeSlicerProfiles.FromJob(
            MachineJson, ProcessJson, FirstJson, null, null, null, snapshotsJson);

        _ = read.Should().Throw<Exception>()
            .Where(exception => exception is JsonException || exception is InvalidOperationException);
    }

    [Fact]
    public void FromJob_ExplicitListWithoutMachine_RejectsRatherThanFallingBack()
    {
        Action read = () => NativeSlicerProfiles.FromJob(
            null, ProcessJson, FirstJson, null, null, null,
            JsonSerializer.Serialize(new[] { Snapshot(FirstJson) }));

        _ = read.Should().Throw<InvalidOperationException>();
    }

    private static NativeFilamentProfile Snapshot(string json) =>
        new(json, NativeSlicerProfiles.ComputeSha256(json));

    private static NativeSlicerProfiles LegacyProfiles() =>
        NativeSlicerProfiles.FromJob(MachineJson, ProcessJson, FirstJson, null, null, null)!;

    private sealed class ClaimHandler(WorkerSliceJobResponse claim) : HttpMessageHandler
    {
        private int _claims;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/slice/claim" &&
                Interlocked.Increment(ref _claims) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(claim) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class ClaimClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SnapshotPoller(
        IHttpClientFactory clients,
        IServiceProvider services,
        IWorkerStateService state,
        IConfiguration configuration,
        string workDir)
        : HttpJobPollerService(clients, services, NullLogger<HttpJobPollerService>.Instance, state, configuration)
    {
        public TaskCompletionSource<(DistributedSlicingJob Job, Dictionary<string, string> Paths)> Written { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string[] GetWorkerCapabilities() => ["orcaslicer:native-profile-selections-v1"];

        protected override async Task<SlicingResult> ExecutePipelineAsync(
            DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct)
        {
            try
            {
                Dictionary<string, string> paths =
                    await OrcaSlicingPipelineService.WriteNativeProfilesAsync(job.NativeProfiles, workDir, ct);
                _ = Written.TrySetResult((job, paths));
            }
            catch (Exception exception)
            {
                _ = Written.TrySetException(exception);
                throw;
            }

            throw new InvalidOperationException("Stop after exercising the claim-to-profile-writer handoff.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }
    }
}
