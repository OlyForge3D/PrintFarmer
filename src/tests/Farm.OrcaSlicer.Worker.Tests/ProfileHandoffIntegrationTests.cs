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

    /// <summary>
    /// The same selection plus a submission `overrides` object that tries to relax the process
    /// profile's compatibility constraint and to name an unrelated printer outright.
    /// </summary>
    private const string ProfileSelectionWithHostileOverridesJson =
        """
        {"machineProfileName":"Test Machine","processProfileName":"Test Process",
         "filamentProfileName":"Test Filament",
         "overrides":{"layer_height":"0.3",
                      "compatible_printers_condition":"name=~/.*/",
                      "compatible_printers":"[\"Some Other Printer\"]"}}
        """;

    private readonly string _testRoot =
        Path.Join(Path.GetTempPath(), $"printfarmer-profile-handoff-{Guid.NewGuid():N}");

    // `machineFrom` selects which branch of OrcaSlicer's system-preset-name derivation the run
    // exercises: "system" makes it read the machine document's `name`, "User" makes it read the
    // `inherits` value that issue #1768's rewrite puts there. Both must end up naming the same
    // preset in the process document's `compatible_printers`, or the job dies at the gate.
    [Theory]
    [InlineData(false, "system")]
    [InlineData(true, "system")]
    [InlineData(false, "User")]
    [InlineData(true, "User")]
    public async Task ExecuteAsync_ClaimedNamedProfiles_InvokesOrcaAndCompletesWithEffectiveHashes(
        bool emitPipeProgress,
        string machineFrom)
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
        _ = services.AddSingleton<ISlicerProfilesService>(new StubProfilesService(machineFrom));
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

        // ── issues #1768 and #1795, asserted together ────────────────────────
        // OrcaSlicer decides whether a process preset may be used with a machine preset by
        // iterating ONLY the process document's `compatible_printers` array and comparing each
        // entry against the MACHINE document's system preset name. Two independent things have to
        // be right for that comparison to succeed, and this test pins both on the documents
        // GenerateProfileJsonFilesAsync actually wrote:
        //
        //   #1768 — the machine document must declare the system preset it snapshots. When its
        //           `from` is not "system", OrcaSlicer derives that name from `inherits`, so
        //           emitting the vendor bundle's internal base ("fdm_machine_common") there made
        //           it reject the submission. Proven against Phrozen Arco 0.4 nozzle.
        //   #1795 — `compatible_printers_condition` is never evaluated on the --load-settings
        //           path, and the empty-array auto-pass sits in a different branch, so a stock
        //           profile expressing compatibility purely through the condition presented an
        //           EMPTY list to the gate. That is the whole Prusa MK4S and CORE One family.
        //
        // The two interact: the value injected for #1795 must be the name OrcaSlicer will read
        // back out of the #1768-rewritten machine document, which is why this theory runs both
        // `from` branches. Deriving it from the cached profile instead of the emitted document
        // would name the vendor base on the "User" branch and the gate would still fail.
        using JsonDocument writtenMachine = JsonDocument.Parse(machineJson);
        using JsonDocument writtenProcess = JsonDocument.Parse(processJson);

        // #1768: `inherits` names the system preset, not the vendor base and not the model.
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

        // The exact name OrcaSlicer derives, per CLI::run: `name` when `from` is "system",
        // `inherits` otherwise.
        _ = writtenMachine.RootElement.GetProperty("from").GetString().Should().Be(machineFrom);
        string systemPresetName = machineFrom == "system"
            ? writtenMachine.RootElement.GetProperty("name").GetString()!
            : emittedInherits;
        _ = systemPresetName.Should().Be("Test Machine");

        // #1795: the process document must name it. The stub arrives with an EMPTY array plus a
        // condition — the exact resolved shape of a stock MK4S / CORE One profile.
        _ = writtenProcess.RootElement.TryGetProperty("compatible_printers", out JsonElement compatible)
            .Should().BeTrue();
        string[] compatiblePrinters = compatible
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        _ = compatiblePrinters.Should().NotBeEmpty(
            "the resolved document arrives with an EMPTY array, which is exactly what the gate " +
            "iterates and finds nothing in");
        _ = compatiblePrinters.Should().Contain(
            systemPresetName,
            "otherwise OrcaSlicer exits -17 (CLI_PROCESS_NOT_COMPATIBLE) without slicing");

        // The condition itself is preserved — this materializes its result, it does not erase it.
        _ = writtenProcess.RootElement.GetProperty("compatible_printers_condition").GetString()
            .Should().Be("printer_notes=~/.*TEST_MACHINE.*/ and nozzle_diameter[0]==0.4");

        CompleteSliceJobRequest? completed = JsonSerializer.Deserialize<CompleteSliceJobRequest>(
            terminal.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = completed.Should().NotBeNull();
        _ = completed!.MachineProfileSha256.Should().Be(Sha256(machineJson));
        _ = completed.ProcessProfileSha256.Should().Be(Sha256(processJson));
        _ = completed.FilamentProfileSha256.Should().Be(Sha256(filamentJson));
    }

    /// <summary>
    /// A submission's <c>overrides</c> object writes arbitrary keys into the process settings
    /// (<c>HttpJobPollerService.ResolveProfileFromJsonAsync</c>), and the profile it writes into
    /// comes straight out of a shared cache that later jobs — including other users' jobs — reuse.
    /// Neither the running job nor any later one may have its compatibility decision changed by it.
    /// </summary>
    [Fact(DisplayName = "A submission's overrides can neither authorize an incompatible pairing nor poison the cached profile")]
    public async Task ExecuteAsync_HostileOverrides_CannotAuthorizeOrPoisonCompatibility()
    {
        _ = Directory.CreateDirectory(_testRoot);
        string captureDirectory = Path.Join(_testRoot, "capture");
        _ = Directory.CreateDirectory(captureDirectory);
        string fakeOrcaPath = await CreateFakeOrcaAsync(captureDirectory, emitPipeProgress: false);

        Guid workerId = Guid.NewGuid();
        var handler = new WorkerApiHandler(ProfileSelectionWithHostileOverridesJson);
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
        var profilesService = new StubProfilesService("system");
        ServiceCollection services = new();
        _ = services.AddSingleton<ISlicerProfilesService>(profilesService);
        _ = services.AddSingleton<ISlicingPipelineService>(pipeline);
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var poller = new TestPoller(clientFactory, serviceProvider, workerState, configuration);

        await poller.StartAsync(CancellationToken.None);
        try
        {
            _ = await handler.TerminalRequest.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
            poller.Dispose();
        }

        // ── The cached profile must be exactly as it was loaded ──────────────
        // Without cloning before applying overrides, these values persist into the cache and
        // silently apply to every later job that names this profile.
        ProcessProfileDto cached = profilesService.CachedProcessProfile;
        _ = cached.Settings["layer_height"].Should().Be(
            "0.2", "a submission's override must not rewrite the shared cached profile");
        _ = cached.Settings["compatible_printers_condition"].Should().Be(
            "printer_notes=~/.*TEST_MACHINE.*/ and nozzle_diameter[0]==0.4",
            "a poisoned condition would let a later, genuinely incompatible pairing pass the gate");
        _ = cached.Settings["compatible_printers"].As<List<string>>().Should().BeEmpty(
            "a poisoned printer list would do the same");

        // ── The running job's own document must reflect the profile's real constraint ─────────
        string processJson = await File.ReadAllTextAsync(Path.Join(captureDirectory, "process.json"));
        using JsonDocument writtenProcess = JsonDocument.Parse(processJson);

        // The benign override is still applied to this job — overrides are a legitimate feature.
        _ = writtenProcess.RootElement.GetProperty("layer_height").GetString().Should().Be("0.3");

        // But the compatibility decision came from the profile's own declared condition, which
        // this machine genuinely satisfies — not from the override that matches anything.
        string[] compatiblePrinters = writtenProcess.RootElement
            .GetProperty("compatible_printers")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        _ = compatiblePrinters.Should().NotContain(
            "Some Other Printer",
            "an override naming an unrelated printer must not survive as an authorization");
    }

    /// <summary>
    /// End-to-end degradation path for issue #1800: a claimed job with a custom position but an
    /// unknown bed centre (the stub machine profile declares no <c>printable_area</c>) drives
    /// <c>PlanPlacement</c> into the <c>AutoArrange</c> fallback with <c>PositionDropped == true</c>
    /// — the requested layout is silently dropped. This asserts the redacted, client-safe signal
    /// actually reaches the real HTTP completion payload the worker sends to the API, not just the
    /// in-process <see cref="Farm.Slicer.Module.Models.SlicingResult"/>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CustomPositionWithUnknownBedCenter_ReportsLayoutNotEmbedded()
    {
        _ = Directory.CreateDirectory(_testRoot);
        string captureDirectory = Path.Join(_testRoot, "capture");
        _ = Directory.CreateDirectory(captureDirectory);
        string fakeOrcaPath = await CreateFakeOrcaAsync(captureDirectory, emitPipeProgress: false);

        Guid workerId = Guid.NewGuid();

        // A custom position with no known bed centre: inputs are STL (not 3MF, so
        // SourcePlacement never applies), and the stub machine profile's Settings carry no
        // printable_area, so TryReadBedCenterAsync resolves null and the ThreeMfProject branch
        // is unavailable. PlanPlacement therefore falls through to AutoArrange with
        // PositionDropped == true — exactly the degradation issue #1800 is about.
        const string customPositionTransform = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,20,0]}""";
        var handler = new WorkerApiHandler(modelTransformJson: customPositionTransform);
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
        _ = services.AddSingleton<ISlicerProfilesService>(new StubProfilesService("system"));
        _ = services.AddSingleton<ISlicingPipelineService>(pipeline);
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var poller = new TestPoller(clientFactory, serviceProvider, workerState, configuration);

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

        CompleteSliceJobRequest? completed = JsonSerializer.Deserialize<CompleteSliceJobRequest>(
            terminal.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = completed.Should().NotBeNull();
        _ = completed!.LayoutDegradation.Should().Be(
            LayoutDegradationReason.LayoutNotEmbedded,
            "the worker could not embed the requested position and fell back to auto-arrange, " +
            "so the redacted signal must reach the API, not just the worker's own log");
    }

    /// <summary>
    /// Drives the real poller and the real pipeline against a fake OrcaSlicer that fails exactly the
    /// way the engine did in issue #1811, and asserts what actually lands on <c>/fail</c>.
    /// </summary>
    /// <remarks>
    /// This is the junction the unit tests cannot reach. <c>OrcaSlicerFailureDiagnosticsTests</c>
    /// proves the diagnostic is composed correctly and <c>SlicerFailureReportTransmissionTests</c>
    /// proves the payload is built correctly, but only this exercises
    /// <c>RunOrcaSlicerAsync</c> reading <c>result.json</c>, throwing a classified
    /// <c>SlicerEngineFailureException</c>, and the poller's catch block turning that into the HTTP
    /// request. Deleting the <c>result.json</c> read, or throwing an unclassified exception, fails
    /// here and nowhere else.
    /// </remarks>
    [Fact(DisplayName =
        "A model the engine rejects reaches /fail with the real diagnostic and a redacted reason")]
    public async Task ExecuteAsync_EngineRejectsModel_ReportsIntactDetailAndReason()
    {
        _ = Directory.CreateDirectory(_testRoot);
        string captureDirectory = Path.Join(_testRoot, "capture");
        _ = Directory.CreateDirectory(captureDirectory);
        string fakeOrcaPath = await CreateFailingFakeOrcaAsync();

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
        _ = services.AddSingleton<ISlicerProfilesService>(new StubProfilesService("system"));
        _ = services.AddSingleton<ISlicingPipelineService>(pipeline);
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var poller = new TestPoller(clientFactory, serviceProvider, workerState, configuration);

        await poller.StartAsync(CancellationToken.None);
        TerminalRequest terminal;
        try
        {
            terminal = await handler.TerminalRequest.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
            poller.Dispose();
        }

        _ = terminal.Path.Should().EndWith("/fail", terminal.Body);

        FailSliceJobRequest? failed = JsonSerializer.Deserialize<FailSliceJobRequest>(
            terminal.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = failed.Should().NotBeNull();

        // The regression: before issue #1811 this arrived as the single word "Errors".
        _ = failed!.ErrorMessage.Should().NotBe("OrcaSlicer failed with exit code 156: Errors");
        _ = failed.ErrorMessage.Should().Contain(
            "Failed slicing the model.",
            "the engine's own error_string from result.json must survive to the API");
        _ = failed.ErrorMessage.Should().Contain("CLI_SLICING_ERROR");
        _ = failed.ErrorMessage.Should().Contain(
            "run found error, return -100",
            "the informative console line used to be dropped in favour of the first match");
        _ = failed.FailureReason.Should().Be(
            SliceFailureReason.SlicingEngineRejectedModel,
            "the redacted classification must travel with the failure, not be inferred from prose");
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

    /// <summary>
    /// A fake OrcaSlicer that fails exactly the way the real engine did in issue #1811: the bare
    /// word <c>Errors</c> on stderr, <c>run found error, return -100, exit...</c> on stdout, a
    /// <c>result.json</c> written into the output directory, no G-code, and exit status 156.
    /// </summary>
    /// <remarks>
    /// The console strings and the <c>result.json</c> body are byte-exact captures from the pinned
    /// OrcaSlicer 2.4.2 AppImage slicing <c>top.stl</c>. Both streams are written because production
    /// runs the binary through <c>xvfb-run</c>, which merges them; the pipeline reads them combined,
    /// so this fake exercises that path without needing the wrapper.
    /// </remarks>
    /// <returns>Path to the executable fake.</returns>
    private async Task<string> CreateFailingFakeOrcaAsync()
    {
        // A single-quoted JSON literal keeps the shell/cmd quoting trivial; the pipeline only reads
        // return_code, error_string and sliced_plates from it.
        const string ResultJson =
            "{\"error_string\": \"Failed slicing the model. Please verify the slicing of all " +
            "plates on Orca Slicer before uploading.\", \"return_code\": -100, \"plate_index\": 1}";

        if (OperatingSystem.IsWindows())
        {
            string scriptPath = Path.Join(_testRoot, "fake-orca-fail.cmd");
            string script = $"""
                @echo off
                echo Errors 1>&2
                echo run found error, return -100, exit...
                > "%CD%\output\result.json" echo {ResultJson.Replace("%", "%%", StringComparison.Ordinal)}
                exit /b 156
                """;
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return scriptPath;
        }

        string unixScriptPath = Path.Join(_testRoot, "fake-orca-fail");
        string unixScript = $"""
            #!/bin/sh
            printf 'Errors\n' 1>&2
            printf 'run found error, return -100, exit...\n'
            cat > "$PWD/output/result.json" <<'ORCA_RESULT_JSON'
            {ResultJson}
            ORCA_RESULT_JSON
            exit 156
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

    private sealed class StubProfilesService(string machineFrom) : ISlicerProfilesService
    {
        // The real OrcaProfilesService hands back its own cached instances on every call, so the
        // stub does too. Returning fresh objects each call would hide exactly the cross-job
        // poisoning these tests exist to catch.
        private readonly List<MachineProfileDto> _machines =
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
                    // reproducing that is what makes the #1768 rewrite observable — and what
                    // makes the #1795 injection observably wrong if it is derived from these
                    // cached settings rather than from the document actually emitted.
                    ["inherits"] = "fdm_machine_common",

                    // OrcaSlicer reads the system preset name from `name` when `from` is
                    // exactly "system", and from `inherits` otherwise. The theory runs both.
                    ["name"] = "Test Machine",
                    ["from"] = machineFrom,
                    ["printer_notes"] = new List<string> { "PRINTER_MODEL_TEST_MACHINE\nPG" },
                },
            },
        ];

        private readonly List<ProcessProfileDto> _processes =
        [
            new ProcessProfileDto
            {
                Name = "Test Process",
                CompatiblePrintersCondition =
                    "printer_notes=~/.*TEST_MACHINE.*/ and nozzle_diameter[0]==0.4",
                Settings = new Dictionary<string, object>
                {
                    ["layer_height"] = "0.2",

                    // This is the exact shape a stock Prusa MK4S / CORE One process profile
                    // has once OrcaProfilesService has resolved its inheritance chain: an
                    // EMPTY compatible_printers array plus a condition. (The source file
                    // declares no array at all, but the resolved document carries an empty
                    // one — 163 profiles in the bundled library are in this shape.) That
                    // empty list is precisely what OrcaSlicer's gate iterates and finds
                    // nothing in. See issue #1795.
                    ["compatible_printers"] = new List<string>(),
                    ["compatible_printers_condition"] =
                        "printer_notes=~/.*TEST_MACHINE.*/ and nozzle_diameter[0]==0.4",
                },
            },
        ];

        /// <summary>The cached process profile, for asserting a submission never mutated it.</summary>
        public ProcessProfileDto CachedProcessProfile => _processes[0];

        public Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineModelProfileDto>>([]);

        public Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineProfileDto>>(_machines);

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
            Task.FromResult<IList<ProcessProfileDto>>(_processes);
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

    private sealed class WorkerApiHandler(string profileSelectionJson = ProfileSelectionJson, string? modelTransformJson = null)
        : HttpMessageHandler
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
                        ModelTransformJson = modelTransformJson,
                        SlicerEngine = SlicerEngineType.OrcaSlicer,
                        SlicerProfileJson = profileSelectionJson,
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
