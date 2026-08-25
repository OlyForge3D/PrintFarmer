using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Regression tests for #2004: a printer whose OrcaSlicer profiles have never been imported into
/// PrintFarmer's database could not be filament-calibrated at all, because only an admin-only
/// import wizard could provision a database Guid for a catalog profile. These tests cover
/// <see cref="ProfilesService.ResolveOrImportProfileForModelAsync"/>, the new non-admin
/// resolve-or-import path, plus a regression check that the existing import wizard
/// (<see cref="ProfilesService.ImportSelectedProfilesForModelAsync"/>) still behaves correctly
/// after being changed to accept an injected <see cref="HttpClient"/> instead of creating one
/// internally.
/// </summary>
public class ProfilesServiceResolveOrImportTests
{
    private const string ManufacturerName = "Qidi Technology";
    private const string ModelName = "Qidi X-Plus 4";

    /// <summary>
    /// The common case once a model has been used at least once: the profile already exists in
    /// PrintFarmer's database, so resolution must short-circuit on the DB lookup and return the
    /// existing Guid without ever touching the catalog service or the OrcaSlicer worker. Verified
    /// here via strict mocks on both dependencies (any invocation of either throws a Moq
    /// verification failure).
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_AlreadyImported_ReturnsExistingIdWithoutTouchingCatalogOrWorker()
    {
        Guid modelId = Guid.NewGuid();
        Guid existingProfileId = Guid.NewGuid();
        MachineProfile existing = new()
        {
            Id = existingProfileId,
            Name = ModelName,
            Manufacturer = ManufacturerName,
            SlicerType = SlicerType.OrcaSlicer,
            Hash = "hash-existing"
        };

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MachineProfile> { existing });

        Mock<ICatalogService> catalogService = new(MockBehavior.Strict);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object);

        using HttpClient httpClient = new(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Worker should not be called for an already-imported profile")));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Machine, ModelName, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.Imported);
        Assert.Equal(existingProfileId, result.ProfileId);

        machineRepo.Verify(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()), Times.Once);
        catalogService.VerifyNoOtherCalls();
        slicersService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The bug's actual reproduction (#2004): a catalog model (e.g. Qidi X-Plus 4) that has never
    /// been imported must now resolve successfully in one call, with no prior admin action —
    /// auto-importing the requested profile from the OrcaSlicer worker catalog and returning its
    /// new database Guid.
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_NeverImported_AutoImportsFromWorkerAndReturnsNewId()
    {
        Guid modelId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();

        List<MachineProfile> persisted = new();
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<MachineProfile>(persisted));
        _ = machineRepo
            .Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MachineProfile?)null);
        _ = machineRepo
            .Setup(r => r.AddAsync(It.IsAny<MachineProfile>(), It.IsAny<CancellationToken>()))
            .Callback<MachineProfile, CancellationToken>((p, _) => persisted.Add(p))
            .Returns(Task.CompletedTask);

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModelDto(modelId, ModelName, manufacturerId));
        _ = catalogService
            .Setup(c => c.GetManufacturerByIdAsync(manufacturerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManufacturerDto(manufacturerId, ManufacturerName));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto> { new(Guid.NewGuid(), modelId, ModelName, "OrcaSlicer") });

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    Version = Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.SlicerVersion,
                    CapabilitiesJson = $"[\"{Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            });

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
            .Returns((string json) => (json, json, "hash-" + json.GetHashCode(StringComparison.Ordinal).ToString("x", CultureInfo.InvariantCulture)));

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            processRepoOverride: processRepo.Object,
            filamentRepoOverride: filamentRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object,
            parsingServiceOverride: parsingService.Object);

        MachineProfileDto workerMachineProfile = new()
        {
            Name = ModelName,
            Manufacturer = ManufacturerName,
            PrinterModel = ModelName
        };

        using HttpClient httpClient = CreateWorkerHttpClient(BuildWorkerProfilesResponseJson(
            ManufacturerName,
            ModelName,
            new List<MachineProfileDto> { workerMachineProfile }));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Machine, ModelName, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.Imported);
        Assert.NotNull(result.ProfileId);
        Assert.NotEqual(Guid.Empty, result.ProfileId!.Value);
        Assert.Single(persisted);
        Assert.Equal(result.ProfileId, persisted[0].Id);
    }

    /// <summary>
    /// When no OrcaSlicer worker is registered, resolution must fail gracefully with a surfaced
    /// <see cref="ResolveProfileForModelResultDto.Error"/> rather than throwing an unhandled
    /// exception — the same worker-unavailable contract the existing import wizard already
    /// honors.
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_NeverImportedAndWorkerUnavailable_ReturnsGracefulError()
    {
        Guid modelId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MachineProfile>());

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModelDto(modelId, ModelName, manufacturerId));
        _ = catalogService
            .Setup(c => c.GetManufacturerByIdAsync(manufacturerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManufacturerDto(manufacturerId, ManufacturerName));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto> { new(Guid.NewGuid(), modelId, ModelName, "OrcaSlicer") });

        // No workers registered at all — GetOrcaSlicerWorkerUrlAsync resolves to null.
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>());

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object);

        using HttpClient httpClient = new(new StubHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP call should be attempted when no worker is registered")));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Machine, ModelName, CancellationToken.None);

        Assert.Null(result.ProfileId);
        Assert.False(result.Imported);
        Assert.NotNull(result.Error);
        Assert.Contains("worker", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression check for the refactor that made <see cref="ProfilesService.ImportSelectedProfilesForModelAsync"/>
    /// accept an injected <see cref="HttpClient"/> instead of creating one internally: the existing
    /// Profile Import Wizard flow must still succeed unchanged.
    /// </summary>
    [Fact]
    public async Task ImportSelectedProfilesForModelAsync_WithInjectedHttpClient_StillSucceeds()
    {
        Guid modelId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();

        List<MachineProfile> persisted = new();
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo
            .Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MachineProfile?)null);
        _ = machineRepo
            .Setup(r => r.AddAsync(It.IsAny<MachineProfile>(), It.IsAny<CancellationToken>()))
            .Callback<MachineProfile, CancellationToken>((p, _) => persisted.Add(p))
            .Returns(Task.CompletedTask);

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModelDto(modelId, ModelName, manufacturerId));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto> { new(Guid.NewGuid(), modelId, ModelName, "OrcaSlicer") });

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    Version = Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.SlicerVersion,
                    CapabilitiesJson = $"[\"{Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            });

        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
            .Returns((string json) => (json, json, "hash-" + json.GetHashCode(StringComparison.Ordinal).ToString("x", CultureInfo.InvariantCulture)));

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object,
            parsingServiceOverride: parsingService.Object);

        MachineProfileDto workerMachineProfile = new()
        {
            Name = ModelName,
            Manufacturer = ManufacturerName,
            PrinterModel = ModelName
        };

        using HttpClient httpClient = CreateWorkerHttpClient(BuildWorkerProfilesResponseJson(
            ManufacturerName,
            ModelName,
            new List<MachineProfileDto> { workerMachineProfile }));

        SelectiveProfileImportRequest request = new()
        {
            ManufacturerName = ManufacturerName,
            SelectedMachineProfiles = new List<string> { ModelName }
        };

        SelectiveProfileImportResultDto result = await svc.ImportSelectedProfilesForModelAsync(httpClient, modelId, request, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.MachineProfilesImported);
        Assert.Equal(1, result.TotalImported);
        Assert.Single(persisted);
    }

    /// <summary>
    /// Same-named process profiles are not unique in this codebase — OrcaSlicer ships process
    /// profiles with the same display name scoped to different printer models (review finding on
    /// #2004: <c>Name</c> alone is not a unique key for <see cref="ProcessProfile"/>/
    /// <see cref="FilamentProfile"/>, mirroring <c>SliceJobController.SelectCompatibleProfile</c>).
    /// When two same-named process profiles exist — one scoped to the requested model, one scoped
    /// to a different model — resolution must return the model-scoped candidate, not just the
    /// first name match, and must not touch the worker.
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_DuplicateNameAcrossModels_ReturnsModelScopedCandidate()
    {
        Guid modelId = Guid.NewGuid();
        Guid otherModelId = Guid.NewGuid();
        const string ProcessName = "0.20mm Standard";

        Guid correctProfileId = Guid.NewGuid();
        Guid wrongProfileId = Guid.NewGuid();
        List<ProcessProfile> existingProcesses = new()
        {
            new ProcessProfile { Id = wrongProfileId, Name = ProcessName, SlicerType = SlicerType.OrcaSlicer, PrinterModelId = otherModelId, Hash = "hash-wrong" },
            new ProcessProfile { Id = correctProfileId, Name = ProcessName, SlicerType = SlicerType.OrcaSlicer, PrinterModelId = modelId, Hash = "hash-correct" }
        };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProcesses);

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MachineProfile>());

        Mock<ICatalogService> catalogService = new(MockBehavior.Strict);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(
            processRepoOverride: processRepo.Object,
            machineRepoOverride: machineRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object);

        using HttpClient httpClient = new(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Worker should not be called when a model-scoped candidate already exists")));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Process, ProcessName, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.Imported);
        Assert.Equal(correctProfileId, result.ProfileId);

        catalogService.VerifyNoOtherCalls();
        slicersService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Filament profiles are unique on <c>(Name, Material, SlicerType)</c>, not <c>Name</c> alone —
    /// OrcaSlicer legally ships same-named filaments in different materials (see
    /// <c>ProfilesServiceRealRepositorySeedTests.SeedSystemProfiles_BundleHasSameFilamentNameInTwoMaterials_ImportsBoth</c>).
    /// <see cref="FilamentProfile"/> has no <c>PrinterModelId</c>, so when two same-named filaments
    /// both declare the requested model's machine in <c>CompatiblePrinters</c> (review finding on
    /// #2004: name + CompatiblePrinters alone cannot tell the materials apart), resolution must
    /// refuse to guess which one the caller meant rather than silently handing back an arbitrary
    /// one — a caller asking to calibrate with one material must never be handed the Guid for a
    /// same-named profile in a different material.
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_AmbiguousSameNameFilamentsDifferByMaterial_DoesNotGuessWrongProfile()
    {
        Guid modelId = Guid.NewGuid();
        const string FilamentName = "Prusa Generic";
        const string MachineName = "Qidi X-Plus 4 0.4 nozzle";

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MachineProfile>
            {
                new() { Id = Guid.NewGuid(), Name = MachineName, SlicerType = SlicerType.OrcaSlicer, PrinterModelId = modelId, Hash = "hash-machine" }
            });

        List<FilamentProfile> existingFilaments = new()
        {
            new FilamentProfile { Id = Guid.NewGuid(), Name = FilamentName, Material = "PLA", SlicerType = SlicerType.OrcaSlicer, CompatiblePrinters = MachineName, Hash = "hash-pla" },
            new FilamentProfile { Id = Guid.NewGuid(), Name = FilamentName, Material = "PETG", SlicerType = SlicerType.OrcaSlicer, CompatiblePrinters = MachineName, Hash = "hash-petg" }
        };
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingFilaments);

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModelDto(modelId, ModelName, Guid.NewGuid()));

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            filamentRepoOverride: filamentRepo.Object,
            catalogServiceOverride: catalogService.Object);

        using HttpClient httpClient = new(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Worker should not be called; this test asserts the ambiguity is surfaced as an error, not a worker retry")));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Filament, FilamentName, CancellationToken.None);

        // Neither the DB lookup nor the (attempted) import may silently pick one of the two
        // ambiguous rows — an explicit error is the only acceptable outcome here.
        Assert.NotNull(result.Error);
        Assert.Null(result.ProfileId);
    }

    /// <summary>
    /// TOCTOU race (#2004 review finding): if a concurrent caller imports the same profile between
    /// this call's initial "not yet imported" lookup and its worker-backed import attempt,
    /// <c>Persist*ProfileAsync</c>'s duplicate check reports the row as <c>Skipped</c> rather than
    /// newly <c>Imported</c> — leaving <c>TotalImported == 0</c> even though the profile now exists.
    /// Resolution must re-check the DB before declaring failure, so the loser of the race still
    /// gets back a usable Guid instead of a false "not found or incompatible" error.
    /// </summary>
    [Fact]
    public async Task ResolveOrImportProfileForModelAsync_ConcurrentImportWonByAnotherCaller_ReturnsExistingIdInsteadOfError()
    {
        Guid modelId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid winningProfileId = Guid.NewGuid();

        // First lookup (short-circuit): nothing imported yet. Second lookup (after the "import"
        // attempt below reports zero new imports): another caller has since persisted the row.
        int machineLookupCall = 0;
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                machineLookupCall++;
                return machineLookupCall == 1
                    ? new List<MachineProfile>()
                    : new List<MachineProfile>
                    {
                        new() { Id = winningProfileId, Name = ModelName, SlicerType = SlicerType.OrcaSlicer, PrinterModelId = modelId, Hash = "hash-won-race" }
                    };
            });

        // Simulates the winner of the race: PersistMachineProfileAsync's duplicate check
        // (checkDuplicates: true) finds a hash match already persisted as a system OrcaSlicer
        // profile already linked to this model, so it returns false (Skipped) rather than
        // throwing or setting importResult.Error — exactly the ambiguous "Skipped" signal the
        // real race produces.
        _ = machineRepo
            .Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineProfile
            {
                Id = winningProfileId,
                Name = ModelName,
                SlicerType = SlicerType.OrcaSlicer,
                IsSystem = true,
                PrinterModelId = modelId,
                Hash = "hash-won-race"
            });

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModelDto(modelId, ModelName, manufacturerId));
        _ = catalogService
            .Setup(c => c.GetManufacturerByIdAsync(manufacturerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManufacturerDto(manufacturerId, ManufacturerName));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto> { new(Guid.NewGuid(), modelId, ModelName, "OrcaSlicer") });

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    Version = Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.SlicerVersion,
                    CapabilitiesJson = $"[\"{Farm.Infrastructure.PrinterCalibration.CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            });

        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
            .Returns((string json) => (json, json, "hash-won-race"));

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            catalogServiceOverride: catalogService.Object,
            slicersServiceOverride: slicersService.Object,
            parsingServiceOverride: parsingService.Object);

        // Worker catalog has a matching machine profile for this name, so the import attempt
        // proceeds to the persist step — where the duplicate check above reports it as already
        // persisted (Skipped, TotalImported == 0), simulating a concurrent caller having already
        // won the race for this exact profile.
        MachineProfileDto workerMachineProfile = new()
        {
            Name = ModelName,
            Manufacturer = ManufacturerName,
            PrinterModel = ModelName
        };

        using HttpClient httpClient = CreateWorkerHttpClient(BuildWorkerProfilesResponseJson(
            ManufacturerName,
            ModelName,
            new List<MachineProfileDto> { workerMachineProfile }));

        ResolveProfileForModelResultDto result = await svc.ResolveOrImportProfileForModelAsync(
            httpClient, modelId, ProfileResolutionType.Machine, ModelName, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.Imported);
        Assert.Equal(winningProfileId, result.ProfileId);
    }

    private static ProfilesService CreateService(
        IProcessProfileRepository? processRepoOverride = null,
        IMachineProfileRepository? machineRepoOverride = null,
        IFilamentProfileRepository? filamentRepoOverride = null,
        ICatalogService? catalogServiceOverride = null,
        Farm.Slicer.Module.Services.ISlicersService? slicersServiceOverride = null,
        IProfileParsingService? parsingServiceOverride = null)
    {
        Mock<IProfilesRepository> profilesRepo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processProfileRepo = new(MockBehavior.Loose);
        Mock<IMachineProfileRepository> machineProfileRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentProfileRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);

        return new ProfilesService(
            profilesRepo.Object,
            NullLogger<ProfilesService>.Instance,
            processRepoOverride ?? processProfileRepo.Object,
            machineRepoOverride ?? machineProfileRepo.Object,
            filamentRepoOverride ?? filamentProfileRepo.Object,
            unitOfWork.Object,
            catalogServiceOverride ?? catalogService.Object,
            parsingServiceOverride ?? parsingService.Object,
            hubContext.Object,
            slicersServiceOverride ?? slicersService.Object,
            aliasService.Object);
    }

    private static HttpClient CreateWorkerHttpClient(string profilesResponseJson)
    {
        return new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri!.AbsolutePath.Contains("/api/profiles/machine/", StringComparison.Ordinal))
            {
                // Alias-matching lookup (GetMachineProfilesByAliasAsync): return the same machine
                // list embedded in the full-catalog response below, as a flat array.
                using JsonDocument doc = JsonDocument.Parse(profilesResponseJson);
                foreach (JsonProperty manufacturer in doc.RootElement.GetProperty("ByHierarchy").EnumerateObject())
                {
                    foreach (JsonProperty model in manufacturer.Value.GetProperty("Models").EnumerateObject())
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(model.Value.GetProperty("MachineProfiles").GetRawText())
                        };
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profilesResponseJson)
            };
        }))
        {
            BaseAddress = null
        };
    }

    private static string BuildWorkerProfilesResponseJson(
        string manufacturerName,
        string modelGroupName,
        IReadOnlyList<MachineProfileDto> machineProfiles)
    {
        PrinterModelProfilesDto modelProfiles = new()
        {
            Name = modelGroupName,
            ModelId = "model1",
            MachineProfiles = [.. machineProfiles],
            FilamentProfiles = [],
            ProcessProfiles = []
        };

        AllProfilesResponseDto response = new()
        {
            ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>
            {
                [manufacturerName] = new ManufacturerProfilesDto
                {
                    Name = manufacturerName,
                    Models = new Dictionary<string, PrinterModelProfilesDto> { ["model1"] = modelProfiles }
                }
            }
        };

        return JsonSerializer.Serialize(response);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send(request));
        }
    }
}
