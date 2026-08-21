using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Covers the worker claim-to-output profile handoff with an external fake OrcaSlicer process.
/// </summary>
public sealed class ProfileHandoffIntegrationTests : IAsyncDisposable
{
    private const string ProfileSelectionJson =
        """{"machineProfileName":"Test Machine","processProfileName":"Test Process","filamentProfileName":"Test Filament"}""";

    private readonly string _testRoot =
        Path.Join(Path.GetTempPath(), $"printfarmer-profile-handoff-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ClaimedNamedProfiles_InvokesOrcaAndCompletesWithEffectiveHashes(
        bool emitPipeProgress)
    {
        _ = Directory.CreateDirectory(_testRoot);
        string captureDirectory = Path.Join(_testRoot, "capture");
        _ = Directory.CreateDirectory(captureDirectory);
        string fakeOrcaPath = await CreateFakeOrcaAsync(captureDirectory, emitPipeProgress);

        Guid workerId = Guid.NewGuid();
        var handler = new WorkerApiHandler();
        var clientFactory = new StubHttpClientFactory(handler);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerApi:BaseUrl"] = "http://localhost",
                ["Worker:PollIntervalSeconds"] = "1",
                ["Worker:LeaseDurationSeconds"] = "300",
                ["Worker:WorkingDirectory"] = Path.Join(_testRoot, "work"),
                ["Worker:OrcaSlicerPath"] = fakeOrcaPath,
            })
            .Build();
        var workerState = new WorkerStateService();
        workerState.SetRegisteredService(workerId, "worker-secret");

        using HttpClient pipelineClient = new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var pipeline = new OrcaSlicingPipelineService(
            pipelineClient,
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            workerState);
        ServiceCollection services = new();
        _ = services.AddSingleton<ISlicerProfilesService>(new StubProfilesService());
        _ = services.AddSingleton<ISlicingPipelineService>(pipeline);
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var poller = new TestPoller(
            clientFactory,
            serviceProvider,
            workerState,
            configuration);

        await poller.StartAsync(CancellationToken.None);
        TerminalRequest terminal;
        try
        {
            terminal = await handler.TerminalRequest.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
            poller.Dispose();
        }

        _ = terminal.Path.Should().EndWith("/complete", terminal.Body);
        _ = terminal.Body.Should().NotContain(nameof(ArgumentNullException));
        _ = handler.ArtifactUploaded.Should().BeTrue("the fake slicer output must reach artifact upload");
        _ = poller.ExecutedJob.Should().NotBeNull();
        _ = poller.ExecutedJob!.SlicerProfileJson.Should().Be(ProfileSelectionJson);
        _ = poller.ExecutedJob.Profile.Should().NotBeNull();
        _ = poller.ExecutedJob.Profile!.MachineProfile!.Name.Should().Be("Test Machine");

        string machineJson = await File.ReadAllTextAsync(Path.Join(captureDirectory, "machine.json"));
        string processJson = await File.ReadAllTextAsync(Path.Join(captureDirectory, "process.json"));
        string filamentJson = await File.ReadAllTextAsync(Path.Join(captureDirectory, "filament.json"));
        _ = machineJson.Should().Contain("\"printer_model\"");
        _ = machineJson.Should().NotContain("\"machineProfile\"");
        _ = processJson.Should().Contain("\"layer_height\"");
        _ = processJson.Should().NotContain("\"processProfile\"");
        _ = filamentJson.Should().Contain("\"filament_type\"");
        _ = filamentJson.Should().NotContain("\"filamentProfile\"");
        _ = File.Exists(Path.Join(captureDirectory, "orca-invoked.txt")).Should().BeTrue();

        // ── issue #1768 ──────────────────────────────────────────────────────
        // OrcaSlicer decides whether a process preset may be used with a machine preset by
        // comparing each entry of the process document's `compatible_printers` against the
        // MACHINE document's system preset name. When the machine document's `from` is not
        // "system" it derives that name from `inherits` rather than `name`, so emitting the
        // vendor bundle's internal base ("fdm_machine_common") there made OrcaSlicer reject
        // such submissions with CLI_PROCESS_NOT_COMPATIBLE (-17) about a second in, before
        // slicing any geometry. Proven against Phrozen Arco 0.4 nozzle, a `from`: "User"
        // preset. Presets shipping `from`: "system" resolve by name and are unaffected, and
        // process profiles carrying only `compatible_printers_condition` fail for a separate
        // reason tracked in #1795.
        //
        // These assertions run against the documents GenerateProfileJsonFilesAsync actually
        // wrote, so reverting the fix at its call site fails this test.
        using JsonDocument writtenMachine = JsonDocument.Parse(machineJson);
        string emittedInherits = writtenMachine.RootElement.GetProperty("inherits").GetString()!;

        _ = emittedInherits.Should().Be(
            "Test Machine",
            "the emitted machine document must declare the system preset it snapshots, which is the profile's Name");
        _ = emittedInherits.Should().NotBe(
            "fdm_machine_common",
            "the vendor bundle's internal base is never listed as a compatible printer");
        _ = emittedInherits.Should().NotBe(
            "Test Machine Model",
            "the printer model is a plausible-looking but wrong choice — OrcaSlicer matches on the preset name");

        // The full invariant OrcaSlicer enforces, asserted across both pipeline-produced documents.
        using JsonDocument writtenProcess = JsonDocument.Parse(processJson);
        string[] compatiblePrinters = writtenProcess.RootElement
            .GetProperty("compatible_printers")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        _ = compatiblePrinters.Should().Contain(
            emittedInherits,
            "otherwise OrcaSlicer exits -17 (CLI_PROCESS_NOT_COMPATIBLE) without slicing");

        CompleteSliceJobRequest? completed = JsonSerializer.Deserialize<CompleteSliceJobRequest>(
            terminal.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = completed.Should().NotBeNull();
        _ = completed!.MachineProfileSha256.Should().Be(Sha256(machineJson));
        _ = completed.ProcessProfileSha256.Should().Be(Sha256(processJson));
        _ = completed.FilamentProfileSha256.Should().Be(Sha256(filamentJson));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<string> CreateFakeOrcaAsync(
        string captureDirectory,
        bool emitPipeProgress)
    {
        if (OperatingSystem.IsWindows())
        {
            string scriptPath = Path.Join(_testRoot, "fake-orca.cmd");
            string script = $"""
                @echo off
                copy /Y "%CD%\machine.json" "{Path.Join(captureDirectory, "machine.json")}" >nul
                copy /Y "%CD%\process.json" "{Path.Join(captureDirectory, "process.json")}" >nul
                copy /Y "%CD%\filament.json" "{Path.Join(captureDirectory, "filament.json")}" >nul
                echo invoked>"{Path.Join(captureDirectory, "orca-invoked.txt")}"
                (
                  echo ; estimated printing time = 120s
                  echo ; filament used = 1g
                  echo ; layer_count = 2
                  echo G28
                ) > "%CD%\output\plate_1.gcode"
                exit /b 0
                """;
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return scriptPath;
        }

        string unixScriptPath = Path.Join(_testRoot, "fake-orca");
        string progressCommand = emitPipeProgress
            ? "printf '%s\\n' '{\"total_percent\":50,\"message\":\"Testing\"}' > \"$PWD/progress.pipe\""
            : ":";
        string unixScript = $"""
            #!/bin/sh
            set -eu
            cp "$PWD/machine.json" "{Path.Join(captureDirectory, "machine.json")}"
            cp "$PWD/process.json" "{Path.Join(captureDirectory, "process.json")}"
            cp "$PWD/filament.json" "{Path.Join(captureDirectory, "filament.json")}"
            {progressCommand}
            printf 'invoked\n' > "{Path.Join(captureDirectory, "orca-invoked.txt")}"
            printf '; estimated printing time = 120s\n; filament used = 1g\n; layer_count = 2\nG28\n' > "$PWD/output/plate_1.gcode"
            """;
        await File.WriteAllTextAsync(
            unixScriptPath,
            unixScript.ReplaceLineEndings("\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetUnixFileMode(
            unixScriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return unixScriptPath;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TestPoller(
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        IWorkerStateService workerState,
        IConfiguration configuration)
        : HttpJobPollerService(
            httpClientFactory,
            serviceProvider,
            NullLogger<HttpJobPollerService>.Instance,
            workerState,
            configuration)
    {
        public DistributedSlicingJob? ExecutedJob { get; private set; }

        protected override Task<SlicingResult> ExecutePipelineAsync(
            DistributedSlicingJob job,
            IServiceProvider scopeServices,
            CancellationToken ct)
        {
            ExecutedJob = job;
            return scopeServices.GetRequiredService<ISlicingPipelineService>().ProcessJobAsync(job, ct);
        }

        protected override string[] GetWorkerCapabilities() => ["orcaslicer"];
    }

    private sealed class StubProfilesService : ISlicerProfilesService
    {
        public Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineModelProfileDto>>([]);

        public Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineProfileDto>>(
            [
                new MachineProfileDto
                {
                    Name = "Test Machine",

                    // Deliberately distinct from Name. The emitted machine document's `inherits`
                    // must carry the profile's NAME (the system preset OrcaSlicer matches
                    // `compatible_printers` against), not its printer model. Keeping these two
                    // values different is what lets the assertion in the test above tell the
                    // correct property from a plausible-looking wrong one. See issue #1768.
                    PrinterModel = "Test Machine Model",
                    Settings = new Dictionary<string, object>
                    {
                        ["printer_model"] = "Test Machine Model",
                        ["nozzle_diameter"] = new List<string> { "0.4" },

                        // Stock vendor profiles arrive carrying the bundle's internal base here;
                        // reproducing that is what makes the rewrite observable.
                        ["inherits"] = "fdm_machine_common",
                    },
                },
            ]);

        public Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<FilamentProfileDto>>(
            [
                new FilamentProfileDto
                {
                    Name = "Test Filament",
                    Material = "PLA",
                    Settings = new Dictionary<string, object>
                    {
                        ["filament_type"] = new List<string> { "PLA" },
                    },
                },
            ]);

        public Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<ProcessProfileDto>>(
            [
                new ProcessProfileDto
                {
                    Name = "Test Process",
                    Settings = new Dictionary<string, object>
                    {
                        ["layer_height"] = "0.2",

                        // OrcaSlicer matches these entries against the MACHINE document's
                        // `inherits` value, so this is the other half of the invariant the
                        // test above asserts on the pipeline-produced documents.
                        ["compatible_printers"] = new List<string> { "Test Machine" },
                    },
                },
            ]);
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class WorkerApiHandler : HttpMessageHandler
    {
        private readonly Guid _artifactId = Guid.NewGuid();
        private readonly Guid _claimToken = Guid.NewGuid();
        private readonly Guid _jobId = Guid.NewGuid();
        private readonly Guid _leaseToken = Guid.NewGuid();
        private readonly TaskCompletionSource<TerminalRequest> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimCount;

        public bool ArtifactUploaded { get; private set; }

        public Task<TerminalRequest> TerminalRequest => _terminal.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/api/slice/claim", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _claimCount) > 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new WorkerSliceJobResponse
                    {
                        Id = _jobId,
                        ClaimToken = _claimToken,
                        Status = "Processing",
                        ModelFileUrl = $"/api/slice/{_jobId}/model",
                        ModelFileName = "test-model.stl",
                        SlicerEngine = SlicerEngineType.OrcaSlicer,
                        SlicerProfileJson = ProfileSelectionJson,
                        LeaseToken = _leaseToken,
                        LeaseFence = 1,
                    }),
                };
            }

            if (request.Method == HttpMethod.Get &&
                string.Equals(path, $"/api/slice/{_jobId}/model", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("solid test\nendsolid test\n")),
                };
            }

            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                ArtifactUploaded = true;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { id = _artifactId }),
                };
            }

            if (request.Method == HttpMethod.Post &&
                (path.EndsWith("/complete", StringComparison.Ordinal) ||
                 path.EndsWith("/fail", StringComparison.Ordinal)))
            {
                string body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                _ = _terminal.TrySetResult(new TerminalRequest(path, body));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { }),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed record TerminalRequest(string Path, string Body);
}
