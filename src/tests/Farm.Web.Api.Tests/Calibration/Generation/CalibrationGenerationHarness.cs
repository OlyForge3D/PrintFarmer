using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Services.Gcode;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Wires the real durable generation path over two isolated SQLite databases: the core context that
/// owns the calibration aggregate and the slicer context that owns jobs, artifacts and workers.
/// </summary>
/// <remarks>
/// Every service in the path is the production implementation. Only the printer status reader, the
/// folder service and the telemetry sink are substituted, because they are infrastructure edges that
/// carry no generation behaviour.
/// </remarks>
internal sealed class CalibrationGenerationHarness : IDisposable
{
    /// <summary>The pinned container digest a healthy attested worker publishes.</summary>
    public const string ContainerDigest = CalibrationGenerationSeed.ContainerDigest;

    /// <summary>The pinned binary digest a healthy attested worker publishes.</summary>
    public const string BinaryDigest = CalibrationGenerationSeed.BinaryDigest;

    /// <summary>The stable operation identifier the attempt aggregate creates its orchestration with.</summary>
    public const string AttemptOperationId = CalibrationGenerationSeed.AttemptOperationId;

    private readonly string _rootPath;
    private readonly string _coreConnectionString;
    private readonly string _slicerConnectionString;
    private readonly Guid _folderId = Guid.NewGuid();

    private CalibrationGenerationHarness(string rootPath)
    {
        _rootPath = rootPath;
        ArtifactRoot = Path.Join(rootPath, "artifacts");
        GcodeRoot = Path.Join(rootPath, "gcode");
        ModelRoot = Path.Join(rootPath, "models");
        _ = Directory.CreateDirectory(ArtifactRoot);
        _ = Directory.CreateDirectory(GcodeRoot);
        _ = Directory.CreateDirectory(ModelRoot);
        _coreConnectionString =
            $"Data Source={Path.Join(rootPath, "core.db")};Pooling=false;Default Timeout=30";
        _slicerConnectionString =
            $"Data Source={Path.Join(rootPath, "slicer.db")};Pooling=false;Default Timeout=30";
    }

    public string ArtifactRoot { get; }

    public string GcodeRoot { get; }

    public string ModelRoot { get; }

    public GcodePromotionReconcilerState PromotionState { get; } = new();

    public CalibrationGenerationRecoveryState RecoveryState { get; } = new();

    public static async Task<CalibrationGenerationHarness> CreateAsync()
    {
        CalibrationGenerationHarness harness = new(Path.Join(
            Path.GetTempPath(),
            $"pf-calibration-generation-{Guid.NewGuid():N}"));
        await using (AppDbContext core = harness.CreateCoreContext())
        {
            _ = await core.Database.EnsureCreatedAsync();
            _ = core.Set<FolderNode>().Add(new FolderNode
            {
                Id = harness._folderId,
                Path = "/",
                FolderType = "gcode",
            });
            _ = await core.SaveChangesAsync();
        }

        await using SlicerDbContext slicer = harness.CreateSlicerContext();
        _ = await slicer.Database.EnsureCreatedAsync();
        return harness;
    }

    public AppDbContext CreateCoreContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_coreConnectionString).Options);

    public SlicerDbContext CreateSlicerContext() =>
        new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(_slicerConnectionString).Options);

    /// <summary>Builds the saga over a fresh scope, exactly as a request or recovery pass would.</summary>
    /// <param name="options">Which production hops are routable in the built scope.</param>
    /// <returns>The saga under test.</returns>
    public ICalibrationGenerationSaga CreateSaga(CalibrationGenerationHarnessOptions? options = null)
    {
        CalibrationGenerationHarnessOptions resolved = options ?? new CalibrationGenerationHarnessOptions();
        CalibrationSlicerCompatibilityPolicy compatibilityPolicy =
            new(resolved.SupportedSlicerVersions);
        AppDbContext core = CreateCoreContext();
        SlicerContextFactory slicerFactory = new(_slicerConnectionString);
        IArtifactsRepository artifactsRepository = new EfArtifactsRepository(slicerFactory);
        IArtifactsService artifacts = CreateArtifactsService(artifactsRepository);
        ISliceJobRepository sliceJobs = new EfSliceJobRepository(CreateSlicerContext());
        IModel3DFileRepository models = new EfModel3DFileRepository(CreateSlicerContext());
        if (resolved.ModelRepositoryDecorator is not null)
        {
            models = resolved.ModelRepositoryDecorator(models);
        }

        IStoragePathService storagePaths = CreateStoragePaths();
        IModelStorageResolver modelStorage = new Model3DStorageResolver(
            models,
            storagePaths,
            NullLogger<Model3DStorageResolver>.Instance);
        IGcodeArtifactPromoter promoter = new GcodeArtifactPromoter(
            core,
            CreateGcodeFilesService(core),
            storagePaths,
            PromotionState,
            NullLogger<GcodeArtifactPromoter>.Instance,
            resolved.PromotionRoutable ? artifacts : null,
            resolved.PromotionRoutable ? artifactsRepository : null,
            resolved.PromotionRoutable ? sliceJobs : null);

        return new CalibrationGenerationSaga(
            core,
            CreateProjectService(core),
            new CalibrationSpecificationCompiler(TimeProvider.System, compatibilityPolicy),
            new CalibrationModelValidator(),
            resolved.PlanCompiler ?? new OrcaCalibrationPlanCompiler(compatibilityPolicy),
            new KlipperCalibrationGcodeGenerator(compatibilityPolicy),
            new CalibrationGcodeAnnotator(),
            new CalibrationGcodeSafetyValidator(compatibilityPolicy),
            BuildProbe(resolved, core, slicerFactory, promoter, modelStorage, sliceJobs, artifacts, artifactsRepository),
            promoter,
            storagePaths,
            TimeProvider.System,
            NullLogger<CalibrationGenerationSaga>.Instance,
            resolved.SliceSubmissionRoutable ? sliceJobs : null,
            resolved.ArtifactSourceRoutable ? artifacts : null,
            resolved.ModelStorageRoutable ? modelStorage : null,
            resolved.ModelStorageRoutable ? models : null);
    }

    /// <summary>Builds only the capability probe, for per-hop capability assertions.</summary>
    /// <param name="options">Which hops are routable.</param>
    /// <returns>The probe under test.</returns>
    public ICalibrationGenerationCapabilityProbe CreateCapabilityProbe(
        CalibrationGenerationHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AppDbContext core = CreateCoreContext();
        SlicerContextFactory slicerFactory = new(_slicerConnectionString);
        IArtifactsRepository artifactsRepository = new EfArtifactsRepository(slicerFactory);
        IArtifactsService artifacts = CreateArtifactsService(artifactsRepository);
        ISliceJobRepository sliceJobs = new EfSliceJobRepository(CreateSlicerContext());
        IModel3DFileRepository models = new EfModel3DFileRepository(CreateSlicerContext());
        IStoragePathService storagePaths = CreateStoragePaths();
        IModelStorageResolver modelStorage = new Model3DStorageResolver(
            models,
            storagePaths,
            NullLogger<Model3DStorageResolver>.Instance);
        IGcodeArtifactPromoter promoter = new GcodeArtifactPromoter(
            core,
            CreateGcodeFilesService(core),
            storagePaths,
            PromotionState,
            NullLogger<GcodeArtifactPromoter>.Instance,
            options.PromotionRoutable ? artifacts : null,
            options.PromotionRoutable ? artifactsRepository : null,
            options.PromotionRoutable ? sliceJobs : null);
        return BuildProbe(
            options,
            core,
            slicerFactory,
            promoter,
            modelStorage,
            sliceJobs,
            artifacts,
            artifactsRepository);
    }

    /// <summary>Seeds a complete, generation-ready calibration project and attempt.</summary>
    /// <param name="method">The canonical calibration method.</param>
    /// <param name="ownerId">Optional explicit owner.</param>
    /// <param name="tamperSpecification">Stores a specification the recompile cannot reproduce.</param>
    /// <param name="profiles">Exact native profiles to store, or <see langword="null"/> for the canonical set.</param>
    /// <returns>The seeded fixture.</returns>
    public Task<CalibrationGenerationFixture> SeedAttemptAsync(
        string method = CalibrationMethodNames.Temperature,
        Guid? ownerId = null,
        bool tamperSpecification = false,
        CalibrationGenerationSeed.ProfileSet? profiles = null,
        CalibrationPinnedSlicerIdentity? pinnedIdentity = null) =>
        CalibrationGenerationSeed.SeedAsync(
            CreateCoreContext,
            method,
            ownerId ?? Guid.NewGuid(),
            tamperSpecification,
            profiles,
            pinnedIdentity: pinnedIdentity);

    /// <summary>Registers an online worker that attests the pinned upstream build identity.</summary>
    /// <param name="containerDigest">Container digest published by the worker's service.</param>
    /// <param name="binaryDigest">Binary digest published by the worker's service.</param>
    /// <param name="version">Reported slicer version.</param>
    /// <returns>The registered worker identity.</returns>
    public async Task<Guid> AddAttestedWorkerAsync(
        string? containerDigest = ContainerDigest,
        string? binaryDigest = BinaryDigest,
        string version = CalibrationContractConstants.SlicerVersion)
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = CalibrationGenerationSeed.BuildAttestationJson(
            containerDigest,
            binaryDigest,
            version);

        await using SlicerDbContext slicer = CreateSlicerContext();
        _ = slicer.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "pinned-orca-service",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = version,
            Host = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            MaxConcurrentJobs = 2,
            Status = WorkerStatus.Online,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = slicer.Workers.Add(new Worker
        {
            Id = workerId,
            ServiceId = serviceId.ToString(),
            Name = "pinned-orca-worker",
            EndpointUrl = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            Version = version,
            ApiKey = "registry-issued-worker-key",
            Status = WorkerStatus.Online,
            TotalSlots = 2,
            ActiveJobs = 0,
            LastHeartbeat = DateTime.UtcNow,
            RegisteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await slicer.SaveChangesAsync();
        return workerId;
    }

    /// <summary>Reads the durable orchestration row.</summary>
    /// <param name="orchestrationId">The orchestration identity.</param>
    /// <returns>The persisted row.</returns>
    public async Task<CalibrationOrchestration> GetOrchestrationAsync(Guid orchestrationId)
    {
        await using AppDbContext core = CreateCoreContext();
        return await core.CalibrationOrchestrations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orchestrationId);
    }

    /// <summary>Counts submitted slice jobs of an orchestration.</summary>
    /// <param name="orchestrationId">The orchestration identity.</param>
    /// <returns>The number of durable jobs.</returns>
    public async Task<int> CountSliceJobsAsync(Guid orchestrationId)
    {
        await using SlicerDbContext slicer = CreateSlicerContext();
        return await slicer.SliceJobs
            .AsNoTracking()
            .CountAsync(candidate => candidate.CalibrationOrchestrationId == orchestrationId);
    }

    /// <summary>Reads the promoted library file of a completed run.</summary>
    /// <param name="gcodeFileId">The promoted file identity.</param>
    /// <returns>The promoted file.</returns>
    public async Task<GcodeFile> GetGcodeFileAsync(Guid gcodeFileId)
    {
        await using AppDbContext core = CreateCoreContext();
        return await core.GcodeFiles.AsNoTracking().SingleAsync(file => file.Id == gcodeFileId);
    }

    /// <summary>Counts promoted library files.</summary>
    /// <returns>The number of promoted files.</returns>
    public async Task<int> CountGcodeFilesAsync()
    {
        await using AppDbContext core = CreateCoreContext();
        return await core.GcodeFiles.AsNoTracking().CountAsync();
    }

    /// <summary>Reads every attempt event recorded for an attempt.</summary>
    /// <param name="attemptId">The attempt identity.</param>
    /// <returns>The events in sequence order.</returns>
    public async Task<IReadOnlyList<CalibrationAttemptEvent>> ListAttemptEventsAsync(Guid attemptId)
    {
        await using AppDbContext core = CreateCoreContext();
        return await core.CalibrationAttemptEvents
            .AsNoTracking()
            .Where(@event => @event.AttemptId == attemptId)
            .OrderBy(@event => @event.Sequence)
            .ToListAsync();
    }

    /// <summary>Counts journal rows written for a project.</summary>
    /// <param name="projectId">The project identity.</param>
    /// <returns>The number of change rows.</returns>
    public async Task<int> CountChangesAsync(Guid projectId)
    {
        await using AppDbContext core = CreateCoreContext();
        return await core.CalibrationChanges.AsNoTracking().CountAsync(change => change.ProjectId == projectId);
    }

    /// <summary>Rewrites the durable row to reproduce a specific interrupted state.</summary>
    /// <param name="orchestrationId">The orchestration identity.</param>
    /// <param name="mutate">The mutation applied to the durable row.</param>
    /// <returns>A task that completes when the row is rewritten.</returns>
    public async Task MutateOrchestrationAsync(Guid orchestrationId, Action<CalibrationOrchestration> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await using AppDbContext core = CreateCoreContext();
        CalibrationOrchestration orchestration = await core.CalibrationOrchestrations
            .SingleAsync(candidate => candidate.Id == orchestrationId);
        mutate(orchestration);
        orchestration.Revision++;
        _ = await core.SaveChangesAsync();
    }

    /// <summary>Rewrites a registered worker's durable row, e.g. to simulate it claiming a job or going offline.</summary>
    /// <param name="workerId">The worker identity.</param>
    /// <param name="mutate">The mutation applied to the durable row.</param>
    /// <returns>A task that completes when the row is rewritten.</returns>
    public async Task MutateWorkerAsync(Guid workerId, Action<Worker> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await using SlicerDbContext slicer = CreateSlicerContext();
        Worker worker = await slicer.Workers.SingleAsync(candidate => candidate.Id == workerId);
        mutate(worker);
        _ = await slicer.SaveChangesAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temporary file must never fail an otherwise passing test.
        }
    }

    private ICalibrationGenerationCapabilityProbe BuildProbe(
        CalibrationGenerationHarnessOptions options,
        AppDbContext core,
        IDbContextFactory<SlicerDbContext> slicerFactory,
        IGcodeArtifactPromoter promoter,
        IModelStorageResolver modelStorage,
        ISliceJobRepository sliceJobs,
        IArtifactsService artifacts,
        IArtifactsRepository artifactsRepository)
    {
        ServiceCollection services = [];
        _ = services.AddSingleton(core);
        if (options.SlicerFactoryRegistered)
        {
            _ = services.AddSingleton(slicerFactory);
        }

        if (options.CapabilityClient is not null)
        {
            _ = services.AddSingleton(options.CapabilityClient);
        }

        _ = services.AddSingleton(promoter);
        if (options.DeterministicCoreAvailable)
        {
            _ = services.AddCalibrationGeneration();
        }

        if (options.ModelStorageRoutable)
        {
            _ = services.AddSingleton(modelStorage);
        }

        if (options.SliceSubmissionRoutable)
        {
            _ = services.AddSingleton(sliceJobs);
        }

        if (options.ArtifactSourceRoutable)
        {
            _ = services.AddSingleton(artifacts);
            _ = services.AddSingleton(artifactsRepository);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        CalibrationSlicerCompatibilityPolicy compatibilityPolicy =
            new(options.SupportedSlicerVersions);
        return new CalibrationGenerationCapabilityProbe(
            BuildConfiguration(options),
            provider,
            RecoveryState,
            NullLogger<CalibrationGenerationCapabilityProbe>.Instance,
            compatibilityPolicy);
    }

    private static ICalibrationProjectService CreateProjectService(AppDbContext core) =>
        new CalibrationProjectService(
            core,
            new Mock<ICalibrationContextResolver>(MockBehavior.Loose).Object,
            new Mock<ICalibrationBlobStore>(MockBehavior.Loose).Object,
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static IConfiguration BuildConfiguration(CalibrationGenerationHarnessOptions options) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slicer:Enabled"] = options.SlicingEnabled ? "true" : "false",
                ["DEPLOYMENT_MODE"] = options.DeploymentMode,
            })
            .Build();

    private IArtifactsService CreateArtifactsService(IArtifactsRepository repository) =>
        new ArtifactsService(
            CreateHostEnvironment(),
            repository,
            Options.Create(new ArtifactStorageSettings { RootPath = ArtifactRoot }),
            new ArtifactsMetrics());

    private IWebHostEnvironment CreateHostEnvironment()
    {
        Mock<IWebHostEnvironment> environment = new(MockBehavior.Loose);
        _ = environment.SetupGet(host => host.ContentRootPath).Returns(_rootPath);
        _ = environment.SetupGet(host => host.EnvironmentName).Returns("Testing");
        return environment.Object;
    }

    private IStoragePathService CreateStoragePaths()
    {
        Mock<IStoragePathService> storagePaths = new(MockBehavior.Loose);
        _ = storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(GcodeRoot);
        _ = storagePaths.Setup(service => service.GetThumbnailDirectory()).Returns(GcodeRoot);
        _ = storagePaths.Setup(service => service.GetModelUploadDirectory()).Returns(ModelRoot);
        return storagePaths.Object;
    }

    private IGcodeFilesService CreateGcodeFilesService(AppDbContext core)
    {
        Mock<IGcodeMetadataExtractorService> metadata = new(MockBehavior.Loose);
        _ = metadata.Setup(service => service.ExtractMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync(new GcodeMetadataExtracted());

        Mock<IGcodeThumbnailExtractorService> thumbnails = new(MockBehavior.Loose);
        _ = thumbnails.Setup(service => service.ExtractAndSaveThumbnailAsync(
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        Mock<IFolderManagementService> folders = new(MockBehavior.Loose);
        _ = folders.Setup(service => service.GetOrCreateFolderAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderNode { Id = _folderId, Path = "/", FolderType = "gcode" });

        return new GcodeFilesService(
            new EfGcodeRepository(core),
            new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
            NullLogger<GcodeFilesService>.Instance,
            CreateStoragePaths(),
            metadata.Object,
            thumbnails.Object,
            folders.Object,
            new Mock<IStoredFileOperationsService>(MockBehavior.Loose).Object,
            new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);
    }

    private sealed class SlicerContextFactory(string connectionString) : IDbContextFactory<SlicerDbContext>
    {
        public SlicerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connectionString).Options);
    }
}

/// <summary>Which production hops the harness makes routable for one built scope.</summary>
internal sealed record CalibrationGenerationHarnessOptions
{
    /// <summary>Whether the deterministic generation core is registered.</summary>
    public bool DeterministicCoreAvailable { get; init; } = true;

    /// <summary>Whether authorized stored model resolution is routable.</summary>
    public bool ModelStorageRoutable { get; init; } = true;

    /// <summary>Whether the canonical slice submission path is routable.</summary>
    public bool SliceSubmissionRoutable { get; init; } = true;

    /// <summary>Whether artifacts are readable and writable.</summary>
    public bool ArtifactSourceRoutable { get; init; } = true;

    /// <summary>Whether the promotion hop has its slicer dependencies.</summary>
    public bool PromotionRoutable { get; init; } = true;

    /// <summary>Whether the deployment advertises slicing at all.</summary>
    public bool SlicingEnabled { get; init; } = true;

    /// <summary>Deployment topology reported by configuration.</summary>
    public string DeploymentMode { get; init; } = "monolith";

    /// <summary>Configured bounded upstream OrcaSlicer version allow-list.</summary>
    public IReadOnlyList<string> SupportedSlicerVersions { get; init; } =
        [CalibrationContractConstants.SlicerVersion];

    /// <summary>
    /// The plan compiler the saga is built with, or <see langword="null"/> for the production one.
    /// </summary>
    /// <remarks>
    /// Only a decorator over the production compiler belongs here: it lets a test observe the plan
    /// a real pass compiled without changing what the saga does with it.
    /// </remarks>
    public IOrcaCalibrationPlanCompiler? PlanCompiler { get; init; }

    /// <summary>Optional test-only decorator around the production model repository.</summary>
    public Func<IModel3DFileRepository, IModel3DFileRepository>? ModelRepositoryDecorator { get; init; }

    /// <summary>
    /// Whether the harness registers a local <c>IDbContextFactory&lt;SlicerDbContext&gt;</c>, as a
    /// monolith deployment does. Set to <see langword="false"/> to reproduce a split/microservices
    /// deployment, where the probe must fall back to <see cref="CapabilityClient"/> (issue #1848).
    /// </summary>
    public bool SlicerFactoryRegistered { get; init; } = true;

    /// <summary>
    /// Optional <see cref="ISlicerHostCapabilityClient"/> substitute to register instead of a local
    /// slicer factory, exercising the probe's split-deployment delegation path.
    /// </summary>
    public ISlicerHostCapabilityClient? CapabilityClient { get; init; }
}

// CalibrationGenerationFixture lives in CalibrationGenerationFixture.cs so the pinned-worker smoke
// harness can compile it without dragging in this file's in-process service graph.
