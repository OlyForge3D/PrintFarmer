using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Tests.Calibration.Generation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// The mandatory operational gate for calibration generation: the published, digest-pinned OrcaSlicer
/// worker container has to complete a real calibration run against a real listening API before the
/// deployment is allowed to advertise generation as operational.
/// </summary>
/// <remarks>
/// <para>
/// Every hop is production code over production transport. The worker registers through
/// <c>POST /api/slicers/register</c> with <c>X-Slicer-Api-Key</c>, claims with its registry-issued
/// <c>X-Worker-Key</c> and <c>X-Worker-Id</c>, works under an active lease and fencing token, downloads
/// the stored model over the authenticated worker route, runs the pinned OrcaSlicer build with the exact
/// native profiles the snapshot recorded, uploads its artifact and completes the job. The saga then
/// reconciles, annotates, safety-validates and promotes the verified artifact into an immutable
/// G-code file.
/// </para>
/// <para>
/// Nothing here fabricates slicer output, inserts a worker registration directly, or hands the container
/// an address it cannot dial. Capability is asserted false before an attested healthy worker exists and
/// is only allowed to be true once every real hop has answered.
/// </para>
/// </remarks>
[Trait("Category", CalibrationPinnedWorkerSmokeTests.SmokeCategory)]
public sealed class CalibrationPinnedWorkerSmokeTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>Explicit trait value the operational gate filters on.</summary>
    public const string SmokeCategory = "PinnedOrcaSmoke";

    private static readonly Guid OwnerUserId = new("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan WorkerStartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttestationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SliceTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(40);

    private readonly ITestOutputHelper _output = output ?? throw new ArgumentNullException(nameof(output));

    private string _workspace = string.Empty;
    private KestrelCalibrationApiHost _api = null!;

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            $"pfarm-orca-smoke-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_workspace);
        _api = KestrelCalibrationApiHost.Start(_workspace);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // A worker still holding a handle must not turn cleanup into a test failure.
        }
    }

    [Fact(DisplayName = "The published pinned worker completes a real calibration run, or generation stays unavailable")]
    public async Task PublishedPinnedWorker_CompletesRealRun_OrGenerationStaysUnavailable()
    {
        using CancellationTokenSource timeout = new(OverallTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await _api.WaitUntilHealthyAsync(TimeSpan.FromMinutes(2), cancellationToken);

        // 1. Capability must be false while no attested pinned worker exists.
        CalibrationGenerationCapabilityDto beforeWorker = await ReadCapabilityAsync(cancellationToken);
        _output.WriteLine("Capability before any worker: " + Describe(beforeWorker));
        _output.WriteLine("Artifact hop: " + await DescribeArtifactHopAsync(cancellationToken));
        _ = beforeWorker.PinnedWorkerAvailable.Should().BeFalse(
            "no worker has attested the pinned build yet");
        _ = beforeWorker.Operational.Should().BeFalse();
        _ = beforeWorker.UnavailableCode.Should().Be(
            CalibrationGenerationProblemCodes.PinnedWorkerUnavailable,
            "every other generation hop must already be healthy so the gate measures the worker alone");

        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment.GetEnvironmentVariable);
        gate = await ConfirmDockerAsync(gate, cancellationToken);
        _output.WriteLine(gate.Describe());

        if (!gate.CanRun)
        {
            // The only honest outcome for an environment that cannot execute the published image is a
            // named blocker plus a capability that never flipped. When the workflow demands the gate,
            // the same blocker fails the run instead.
            _ = gate.IsRequired.Should().BeFalse(
                "the operational gate was required but could not execute: " + gate.BlockReason);
            return;
        }

        // 2. Pull and run the published image strictly by its registry manifest digest.
        PinnedOrcaWorkerContainer.CommandResult pull =
            await PinnedOrcaWorkerContainer.PullAsync(gate.ImageReference, cancellationToken);
        _ = pull.ExitCode.Should().Be(0, "the published pinned worker must be pullable by digest: " + pull.Describe());

        IReadOnlyList<string> repositoryDigests =
            await PinnedOrcaWorkerContainer.ReadRepositoryDigestsAsync(gate.ImageReference, cancellationToken);
        _ = repositoryDigests.Should().Contain(
            digest => digest.EndsWith("@" + gate.Digest, StringComparison.Ordinal),
            "the pulled image must carry exactly the published registry manifest digest");

        await using PinnedOrcaWorkerContainer worker = await PinnedOrcaWorkerContainer.StartAsync(
            gate.ImageReference,
            gate.Digest!,
            _api.BaseAddress,
            _api.WorkerSharedKey,
            cancellationToken);
        await worker.WaitUntilReachableAsync(WorkerStartTimeout, cancellationToken);

        // 3. The worker registers itself over the production registration route and its attestation
        //    must carry the runtime-injected registry digest, not a build-time constant.
        CalibrationGenerationCapabilityDto attested =
            await WaitForAttestedWorkerAsync(worker, AttestationTimeout, cancellationToken);
        _ = attested.PinnedWorkerAvailable.Should().BeTrue();
        _ = attested.Operational.Should().BeTrue(attested.UnavailableCode);

        (Guid workerId, Guid serviceId, string containerDigest, string binaryDigest) =
            await ReadRegisteredAttestationAsync(cancellationToken);
        _ = containerDigest.Should().Be(
            gate.Digest,
            "the worker must attest the immutable registry digest it was started with");
        _ = binaryDigest.Should().NotBeNullOrWhiteSpace(
            "the pinned image must attest the OrcaSlicer payload it installed");

        // 4. A tiny deterministic model is uploaded through production upload storage and proven to
        //    round-trip byte for byte over the authenticated download route.
        byte[] fixtureBytes = BuildDeterministicCubeStl(sideMillimeters: 20f);
        UploadedModel uploaded = await UploadModelAsync(fixtureBytes, cancellationToken);
        _ = uploaded.DownloadedBytes.Should().Equal(
            fixtureBytes,
            "production upload storage must return exactly the uploaded bytes");
        _ = uploaded.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(fixtureBytes)));

        // 5. Seed the immutable calibration aggregate with the exact native profiles this very
        //    container publishes, so the slicer receives its own documents back, hash verified.
        PinnedOrcaProfileSelection profiles =
            await PinnedOrcaProfileCatalog.SelectAsync(worker.BaseAddress, cancellationToken);
        CalibrationGenerationFixture fixture = await SeedAsync(
            uploaded,
            profiles,
            new CalibrationPinnedSlicerIdentity(
                CalibrationContractConstants.SlicerVersion,
                CalibrationContractConstants.SlicerDistribution,
                containerDigest,
                binaryDigest,
                serviceId));

        // 6. The authenticated human caller starts the durable saga over the production route.
        using HttpClient caller = CreateCallerClient();
        using HttpRequestMessage generate = new(
            HttpMethod.Post,
            $"/api/calibration-projects/{fixture.ProjectId}/attempts/{fixture.AttemptId}/generate-job")
        {
            Content = JsonContent.Create(fixture.Request()),
        };
        generate.Headers.Add("Idempotency-Key", $"orca-smoke-{fixture.OrchestrationId:N}");
        using HttpResponseMessage accepted = await caller.SendAsync(generate, cancellationToken);
        string acceptedBody = await accepted.Content.ReadAsStringAsync(cancellationToken);
        _ = accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, acceptedBody);

        // 7. Drive the durable saga and let the real worker claim, slice and complete the job.
        CalibrationOrchestrationStatusDto status =
            await RunToCompletionAsync(fixture, caller, worker, cancellationToken);
        _ = status.Status.Should().Be(
            nameof(CalibrationOrchestrationStatus.Completed),
            status.LastErrorCode ?? "(no error code)");
        _ = status.WorkerId.Should().Be(workerId, "the attested worker must be the one that ran the job");
        _ = status.SlicerContainerDigest.Should().Be(gate.Digest);
        _ = status.SlicerBinarySha256.Should().Be(binaryDigest);
        _ = status.SpecificationSha256.Should().Be(fixture.Specification.Sha256);
        _ = status.SourceArtifactId.Should().NotBeNull();
        _ = status.FinalArtifactId.Should().NotBeNull();
        _ = status.FinalArtifactId!.Value.Should().NotBe(status.SourceArtifactId!.Value);
        _ = status.GcodeFileId.Should().NotBeNull();

        // 8. The real worker upload exists in server-side artifact storage with matching worker and
        //    server digests and a non-zero byte count.
        using (IServiceScope sourceScope = _api.ListeningServices.CreateScope())
        {
            IArtifactsService artifacts =
                sourceScope.ServiceProvider.GetRequiredService<IArtifactsService>();
            (Artifact artifact, string fullPath)? source =
                await artifacts.GetWithPathAsync(status.SourceArtifactId!.Value, cancellationToken);
            _ = source.Should().NotBeNull();
            _ = File.Exists(source!.Value.fullPath).Should().BeTrue();
            _ = source.Value.artifact.SizeBytes.Should().BeGreaterThan(0);
            _ = source.Value.artifact.DeclaredSha256.Should().Be(source.Value.artifact.Sha256);
            byte[] sourceBytes = await File.ReadAllBytesAsync(
                source.Value.fullPath,
                cancellationToken);
            _ = Convert.ToHexString(SHA256.HashData(sourceBytes))
                .Should().Be(source.Value.artifact.Sha256);
        }

        // 9. The verified artifact is downloadable over the authenticated route and its bytes are the
        //    bytes that were promoted into the immutable G-code library.
        byte[] artifactBytes = await DownloadArtifactAsync(
            caller,
            status.FinalArtifactId!.Value,
            cancellationToken);
        _ = artifactBytes.Should().NotBeEmpty();
        string artifactSha256 = Convert.ToHexString(SHA256.HashData(artifactBytes));

        (IServiceScope scope, AppDbContext core) = CreateCoreScope();
        using (scope)
        {
            GcodeFile promoted = await core.GcodeFiles
                .AsNoTracking()
                .SingleAsync(file => file.Id == status.GcodeFileId!.Value, cancellationToken);
            _ = promoted.IsImmutable.Should().BeTrue();
            _ = promoted.CalibrationAttemptId.Should().Be(fixture.AttemptId);
            _ = promoted.CalibrationOrchestrationId.Should().Be(fixture.OrchestrationId);
            _ = promoted.SpecificationSha256.Should().Be(fixture.Specification.Sha256);
            _ = promoted.PinnedSlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
            _ = promoted.SlicerContainerDigest.Should().Be(gate.Digest);
            _ = promoted.ContentSha256.Should().Be(
                artifactSha256,
                "the promoted library entry must be the very bytes the authenticated artifact route serves");

            SliceJob job = await ReadSliceJobAsync(fixture.OrchestrationId, cancellationToken);
            _ = job.Status.Should().Be(SliceJobStatus.Completed);
            _ = job.WorkerId.Should().Be(workerId.ToString());

            // The worker receives, verifies and reports the effective documents: the exact upstream
            // baselines with the forbidden command and notes keys neutralized by the plan compiler.
            OrcaEffectiveProfileDocument machine =
                OrcaEffectiveProfileFactory.Derive(profiles.MachineJson);
            OrcaEffectiveProfileDocument process =
                OrcaEffectiveProfileFactory.Derive(profiles.ProcessJson);
            OrcaEffectiveProfileDocument filament =
                OrcaEffectiveProfileFactory.Derive(profiles.FilamentJson);
            _ = job.MachineProfileSha256.Should().Be(machine.Sha256);
            _ = job.ProcessProfileSha256.Should().Be(process.Sha256);
            _ = job.FilamentProfileSha256.Should().Be(filament.Sha256);
            _ = job.MachineProfileJson.Should().Be(machine.Json);
            _ = job.SlicerContainerDigest.Should().Be(gate.Digest);

            // The immutable snapshot still holds the untouched upstream documents and their digests.
            PrinterConfigurationSnapshot snapshot = await core.PrinterConfigurationSnapshots
                .AsNoTracking()
                .SingleAsync(row => row.AttemptId == fixture.AttemptId, cancellationToken);
            _ = snapshot.ExactMachineProfileJson.Should().Be(profiles.MachineJson);
            _ = snapshot.MachineProfileSha256.Should()
                .Be(CalibrationCanonicalJson.ComputeTextSha256(profiles.MachineJson));
            _output.WriteLine(
                "Neutralized machine profile keys: " +
                (machine.NeutralizedKeys.Count == 0
                    ? "(none)"
                    : string.Join(", ", machine.NeutralizedKeys)));
        }

        _output.WriteLine(
            $"Pinned worker smoke completed: image={gate.ImageReference}, orchestration={fixture.OrchestrationId}, " +
            $"gcodeFile={status.GcodeFileId}, gcodeBytes={artifactBytes.Length}.");
    }

    /// <summary>
    /// Names why the artifact hop is unroutable, which is the most common reason a deployment cannot
    /// promote a verified calibration artifact.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An operator-facing description that carries no path or credential.</returns>
    private async Task<string> DescribeArtifactHopAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _api.ListeningServices.CreateScope();
        IArtifactsService? artifacts = scope.ServiceProvider.GetService<IArtifactsService>();
        IArtifactsRepository? repository = scope.ServiceProvider.GetService<IArtifactsRepository>();
        if (artifacts is null || repository is null)
        {
            return $"artifactsService={(artifacts is null ? "missing" : "present")}, " +
                $"artifactsRepository={(repository is null ? "missing" : "present")}";
        }

        try
        {
            _ = await artifacts.ListByJobAsync(Guid.Empty, cancellationToken);
            return "artifact source answered a real query";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"artifact source query failed with {exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string Describe(CalibrationGenerationCapabilityDto capability) =>
        $"operational={capability.Operational}, core={capability.DeterministicCoreAvailable}, " +
        $"modelStorage={capability.ModelStorageRoutable}, sliceSubmission={capability.SliceSubmissionRoutable}, " +
        $"artifactSource={capability.ArtifactSourceRoutable}, pinnedWorker={capability.PinnedWorkerAvailable}, " +
        $"promotion={capability.PromotionOperational}, orchestrationStore={capability.OrchestrationStoreAvailable}, " +
        $"recovery={capability.RecoveryHealthy}, code={capability.UnavailableCode ?? "(none)"}";

    private static async Task<PinnedOrcaSmokeGate> ConfirmDockerAsync(
        PinnedOrcaSmokeGate gate,
        CancellationToken cancellationToken) =>
        !gate.CanRun || await PinnedOrcaWorkerContainer.HasDockerAsync(cancellationToken)
            ? gate
            : gate with
            {
                Image = null,
                Digest = null,
                BlockReason = "no usable docker command was found, so the published pinned worker cannot be executed.",
            };

    private (IServiceScope Scope, AppDbContext Context) CreateCoreScope()
    {
        IServiceScope scope = _api.ListeningServices.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private HttpClient CreateCallerClient()
    {
        HttpClient client = _api.CreateListeningClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", OwnerUserId.ToString());
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                PrintFarmerPermissions.Calibration.Generate,
                PrintFarmerPermissions.Calibration.Read,
                PrintFarmerPermissions.Slicing.Submit,
                PrintFarmerPermissions.Slicing.ReadArtifact,
                PrintFarmerPermissions.Queue.Read));
        return client;
    }

    private async Task<CalibrationGenerationCapabilityDto> ReadCapabilityAsync(
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _api.ListeningServices.CreateScope();
        ICalibrationGenerationCapabilityProbe probe = scope.ServiceProvider
            .GetRequiredService<ICalibrationGenerationCapabilityProbe>();
        return await probe.GetCapabilityAsync(cancellationToken);
    }

    private async Task<CalibrationGenerationCapabilityDto> WaitForAttestedWorkerAsync(
        PinnedOrcaWorkerContainer worker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        CalibrationGenerationCapabilityDto capability = await ReadCapabilityAsync(cancellationToken);
        while (DateTime.UtcNow < deadline && !capability.PinnedWorkerAvailable)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            capability = await ReadCapabilityAsync(cancellationToken);
        }

        if (!capability.PinnedWorkerAvailable)
        {
            throw new TimeoutException(
                "The published pinned worker never produced an accepted attestation " +
                $"(capability code: {capability.UnavailableCode}). " +
                await worker.ReadScrubbedLogsAsync(CancellationToken.None));
        }

        return capability;
    }

    private async Task<(Guid WorkerId, Guid ServiceId, string ContainerDigest, string BinaryDigest)>
        ReadRegisteredAttestationAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _api.ListeningServices.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SlicerService service = await slicer.SlicerServices
            .AsNoTracking()
            .OrderByDescending(candidate => candidate.LastSeen)
            .FirstAsync(candidate => candidate.SlicerType == (int)SlicerType.OrcaSlicer, cancellationToken);
        Worker registered = await slicer.Workers
            .AsNoTracking()
            .FirstAsync(candidate => candidate.ServiceId == service.Id.ToString(), cancellationToken);

        return CalibrationSlicerAttestation.TryRead(
                service.CapabilitiesJson,
                out string? containerDigest,
                out string? binaryDigest)
            ? (registered.Id, service.Id, containerDigest, binaryDigest)
            : throw new InvalidOperationException(
                "The registered pinned worker published no readable slicer attestation.");
    }

    private async Task<UploadedModel> UploadModelAsync(byte[] content, CancellationToken cancellationToken)
    {
        using HttpClient caller = CreateCallerClient();
        using MultipartFormDataContent form = [];
        using ByteArrayContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/stl");
        form.Add(file, "modelFile", "calibration-smoke-cube.stl");

        using HttpResponseMessage response = await caller.PostAsync(
            new Uri("/api/3d-models/upload", UriKind.Relative),
            form,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        using JsonDocument document = JsonDocument.Parse(body);
        Guid modelId = document.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage download = await caller.GetAsync(
            new Uri($"/api/3d-models/file/{modelId}", UriKind.Relative),
            cancellationToken);
        _ = download.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await download.Content.ReadAsStringAsync(cancellationToken));
        byte[] downloaded = await download.Content.ReadAsByteArrayAsync(cancellationToken);

        using IServiceScope scope = _api.ListeningServices.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Model3D stored = await slicer.Models3D
            .AsNoTracking()
            .SingleAsync(model => model.Id == modelId, cancellationToken);
        _ = stored.UploadedByUserId.Should().Be(OwnerUserId);

        return new UploadedModel(
            modelId,
            Convert.ToHexString(SHA256.HashData(content)),
            content.LongLength,
            downloaded);
    }

    private async Task<CalibrationGenerationFixture> SeedAsync(
        UploadedModel uploaded,
        PinnedOrcaProfileSelection profiles,
        CalibrationPinnedSlicerIdentity pinnedIdentity) =>
        await CalibrationGenerationSeed.SeedAsync(
            CreateSeedContext,
            CalibrationMethodNames.FinalVerification,
            OwnerUserId,
            tamperSpecification: false,
            new CalibrationGenerationSeed.ProfileSet(
                profiles.MachineJson,
                profiles.ProcessJson,
                profiles.FilamentJson,
                profiles.NozzleDiameterMillimeters),
            new CalibrationModelReference(
                uploaded.Model3DId,
                uploaded.Sha256.ToLowerInvariant(),
                CalibrationModelFormats.Stl,
                "calibration-smoke-cube.stl",
                uploaded.SizeBytes,
                "imported"),
            pinnedIdentity);

    private AppDbContext CreateSeedContext()
    {
        IServiceScope scope = _api.ListeningServices.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    private async Task<CalibrationOrchestrationStatusDto> RunToCompletionAsync(
        CalibrationGenerationFixture fixture,
        HttpClient caller,
        PinnedOrcaWorkerContainer worker,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + SliceTimeout;
        CalibrationOrchestrationStatusDto status = await AdvanceAsync(fixture, caller, cancellationToken);
        while (DateTime.UtcNow < deadline &&
            status.Status != nameof(CalibrationOrchestrationStatus.Completed) &&
            status.Status != nameof(CalibrationOrchestrationStatus.Failed) &&
            status.Status != nameof(CalibrationOrchestrationStatus.Cancelled))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            status = await AdvanceAsync(fixture, caller, cancellationToken);
        }

        if (status.Status != nameof(CalibrationOrchestrationStatus.Completed))
        {
            _output.WriteLine(await worker.ReadScrubbedLogsAsync(CancellationToken.None));
        }

        return status;
    }

    private async Task<CalibrationOrchestrationStatusDto> AdvanceAsync(
        CalibrationGenerationFixture fixture,
        HttpClient caller,
        CancellationToken cancellationToken)
    {
        using (IServiceScope scope = _api.ListeningServices.CreateScope())
        {
            ICalibrationGenerationSaga saga = scope.ServiceProvider
                .GetRequiredService<ICalibrationGenerationSaga>();
            _ = await saga.ResumeAsync(fixture.OrchestrationId, cancellationToken);
        }

        using HttpResponseMessage response = await caller.GetAsync(
            new Uri($"/api/calibration-orchestrations/{fixture.OrchestrationId}", UriKind.Relative),
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<CalibrationOrchestrationStatusDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<SliceJob> ReadSliceJobAsync(Guid orchestrationId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _api.ListeningServices.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return await slicer.SliceJobs
            .AsNoTracking()
            .SingleAsync(job => job.CalibrationOrchestrationId == orchestrationId, cancellationToken);
    }

    private static async Task<byte[]> DownloadArtifactAsync(
        HttpClient caller,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await caller.GetAsync(
            new Uri($"/api/artifacts/{artifactId}", UriKind.Relative),
            cancellationToken);
        _ = response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(cancellationToken));
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Builds a watertight axis-aligned cube as a binary STL.
    /// </summary>
    /// <param name="sideMillimeters">Edge length of the cube.</param>
    /// <returns>The deterministic STL bytes.</returns>
    /// <remarks>
    /// The fixture is generated here rather than committed so nothing arbitrary can be smuggled into
    /// the slicer: the same twelve triangles are produced on every run.
    /// </remarks>
    private static byte[] BuildDeterministicCubeStl(float sideMillimeters)
    {
        float[][] corners =
        [
            [0, 0, 0], [sideMillimeters, 0, 0], [sideMillimeters, sideMillimeters, 0], [0, sideMillimeters, 0],
            [0, 0, sideMillimeters], [sideMillimeters, 0, sideMillimeters],
            [sideMillimeters, sideMillimeters, sideMillimeters], [0, sideMillimeters, sideMillimeters],
        ];
        int[][] triangles =
        [
            [0, 3, 2], [0, 2, 1],
            [4, 5, 6], [4, 6, 7],
            [0, 1, 5], [0, 5, 4],
            [1, 2, 6], [1, 6, 5],
            [2, 3, 7], [2, 7, 6],
            [3, 0, 4], [3, 4, 7],
        ];

        byte[] content = new byte[80 + 4 + (triangles.Length * 50)];
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(80), (uint)triangles.Length);
        int offset = 84;
        foreach (int[] triangle in triangles)
        {
            float[] normal = Normal(corners[triangle[0]], corners[triangle[1]], corners[triangle[2]]);
            WriteVector(content, ref offset, normal);
            WriteVector(content, ref offset, corners[triangle[0]]);
            WriteVector(content, ref offset, corners[triangle[1]]);
            WriteVector(content, ref offset, corners[triangle[2]]);
            offset += 2;
        }

        return content;
    }

    private static void WriteVector(byte[] content, ref int offset, float[] vector)
    {
        foreach (float component in vector)
        {
            BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset), component);
            offset += 4;
        }
    }

    private static float[] Normal(float[] a, float[] b, float[] c)
    {
        float[] first = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
        float[] second = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
        float[] cross =
        [
            (first[1] * second[2]) - (first[2] * second[1]),
            (first[2] * second[0]) - (first[0] * second[2]),
            (first[0] * second[1]) - (first[1] * second[0]),
        ];
        float length = MathF.Sqrt((cross[0] * cross[0]) + (cross[1] * cross[1]) + (cross[2] * cross[2]));
        return length == 0 ? [0, 0, 0] : [cross[0] / length, cross[1] / length, cross[2] / length];
    }

    /// <summary>A model that was uploaded through production upload storage.</summary>
    /// <param name="Model3DId">Stored model identity.</param>
    /// <param name="Sha256">Uppercase hexadecimal SHA-256 of the uploaded bytes.</param>
    /// <param name="SizeBytes">Length of the uploaded bytes.</param>
    /// <param name="DownloadedBytes">Bytes the authenticated download route returned.</param>
    private sealed record UploadedModel(
        Guid Model3DId,
        string Sha256,
        long SizeBytes,
        byte[] DownloadedBytes);
}
