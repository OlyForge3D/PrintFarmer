using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Seeds real Moonraker printer records for isolated emulator validation stacks.
/// </summary>
public sealed class MoonrakerEmulatorSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<MoonrakerEmulatorSeedSettings> options,
    ILogger<MoonrakerEmulatorSeeder> logger) : BackgroundService
{
    // Serializes TrySeedAsync so the background ExecuteAsync retry loop can never overlap with a
    // concurrent admin-triggered ResetAsync call (or two overlapping resets): without this, both
    // callers could race past the same hash-lookup-then-insert check in
    // GetOrCreate*ProfileAsync, and while the repositories' unique Hash index prevents duplicate
    // rows, the losing writer's DbUpdateException retry still costs an extra round trip that a
    // same-process lock avoids entirely. This only serializes calls within this single hosted
    // service instance; it does not protect against a second application instance seeding the
    // same database concurrently, which the unique-index + retry path still covers.
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    /// <inheritdoc />
    public override void Dispose()
    {
        _seedLock.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Number of transient-failure retry attempts <see cref="ResetAsync"/> tolerates before
    /// giving up. On split/microservices hosts the API's own <c>SlicerDbContext</c> connection
    /// can race a freshly-started stack's slicer-host container, which owns applying that
    /// schema's migrations independently of the API's own readiness (#1858): the API can report
    /// itself healthy and start accepting admin-triggered reset calls before slicer-host has
    /// finished migrating. This bounded retry absorbs that narrow startup window without masking
    /// a genuinely broken stack indefinitely.
    /// </summary>
    private const int ResetRetryAttempts = 20;

    /// <summary>
    /// Restores the deterministic database state used by emulator-backed browser tests.
    /// </summary>
    public async Task<bool> ResetAsync(CancellationToken cancellationToken)
    {
        MoonrakerEmulatorSeedSettings settings = options.Value;
        if (!settings.Enabled || settings.Printers.Count == 0)
        {
            return false;
        }

        for (int attempt = 1; attempt <= ResetRetryAttempts; attempt++)
        {
            try
            {
                return await TrySeedAsync(settings, resetRuntimeState: true, cancellationToken);
            }
            catch (Exception exception) when (
                attempt < ResetRetryAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    exception,
                    "Moonraker emulator reset is waiting for database initialization (attempt {Attempt}/{MaxAttempts})",
                    attempt,
                    ResetRetryAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        // Unreachable: the final attempt (attempt == ResetRetryAttempts) has no retry guard on
        // its catch clause, so a still-failing exception propagates out of the try block above
        // instead of falling through to here.
        throw new UnreachableException();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MoonrakerEmulatorSeedSettings settings = options.Value;
        if (!settings.Enabled || settings.Printers.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TrySeedAsync(settings, resetRuntimeState: false, stoppingToken))
                {
                    return;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    exception,
                    "Moonraker emulator seed is waiting for database initialization");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    private async Task<bool> TrySeedAsync(
        MoonrakerEmulatorSeedSettings settings,
        bool resetRuntimeState,
        CancellationToken cancellationToken)
    {
        await _seedLock.WaitAsync(cancellationToken);
        try
        {
            return await TrySeedCoreAsync(settings, resetRuntimeState, cancellationToken);
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private async Task<bool> TrySeedCoreAsync(
        MoonrakerEmulatorSeedSettings settings,
        bool resetRuntimeState,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ICatalogRepository catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();
        IPrintersService printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();
        IMachineProfileRepository machineProfiles =
            scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
        IProcessProfileRepository processProfiles =
            scope.ServiceProvider.GetRequiredService<IProcessProfileRepository>();
        IFilamentProfileRepository filamentProfiles =
            scope.ServiceProvider.GetRequiredService<IFilamentProfileRepository>();
        Dictionary<Guid, CalibrationProfileTrio> profileTrioCache = [];

        Guid? unknownManufacturerId = await catalog.GetUnknownManufacturerIdAsync(cancellationToken);
        Guid? unknownModelId = await catalog.GetUnknownModelIdAsync(cancellationToken);
        if (unknownManufacturerId is null || unknownModelId is null)
        {
            return false;
        }

        List<Printer> printers = await unitOfWork.Printers.GetAllAsync(cancellationToken);
        if (resetRuntimeState)
        {
            foreach (Printer discoveryFixture in printers.Where(IsDeterministicDiscoveryFixture))
            {
                await unitOfWork.Printers.RemoveAsync(discoveryFixture, cancellationToken);
            }

            printers.RemoveAll(IsDeterministicDiscoveryFixture);
        }

        List<PrintJob> jobs = await unitOfWork.Queue.GetAllAsync(cancellationToken);
        HashSet<Guid> seedPrinterIds = settings.Printers.Select(seed => seed.Id).ToHashSet();
        HashSet<Guid> activeSeedJobIds = settings.Printers
            .Where(seed => seed.ActiveJobId.HasValue)
            .Select(seed => seed.ActiveJobId!.Value)
            .ToHashSet();

        if (resetRuntimeState)
        {
            foreach (PrintJob job in jobs.Where(job =>
                         job.AssignedPrinterId is Guid printerId &&
                         seedPrinterIds.Contains(printerId) &&
                         !activeSeedJobIds.Contains(job.Id) &&
                         job.Status is PrintJobStatus.Queued or
                             PrintJobStatus.Assigned or
                             PrintJobStatus.Starting or
                             PrintJobStatus.Printing or
                             PrintJobStatus.Paused))
            {
                job.Status = PrintJobStatus.Cancelled;
                job.ActualEndTime = DateTime.UtcNow;
                job.UpdatedAt = DateTime.UtcNow;
            }
        }

        foreach (MoonrakerEmulatorPrinterSeed seed in settings.Printers)
        {
            ValidateSeed(seed);

            Printer? existing = printers.FirstOrDefault(printer =>
                printer.Id == seed.Id ||
                string.Equals(printer.ServerUrl, seed.ServerUrl, StringComparison.OrdinalIgnoreCase));
            Printer printer;
            Guid modelId;
            if (existing is not null)
            {
                Printer? tracked = await unitOfWork.Printers.FindByIdWithToolheadsAsync(
                    existing.Id,
                    cancellationToken);
                if (tracked is null)
                {
                    return false;
                }

                tracked.Name = seed.Name;
                tracked.ServerUrl = seed.ServerUrl;
                tracked.OriginalServerUrl = seed.ServerUrl;
                tracked.BackendPort = 7125;
                tracked.FrontendPort = 7125;
                tracked.Backend = (int)PrinterBackend.Moonraker;
                tracked.IsEnabled = seed.IsEnabled;
                tracked.DispatchState =
                    await unitOfWork.Printers.FindDispatchStateAsync(
                        tracked.Id,
                        cancellationToken);
                printer = tracked;
                modelId = tracked.ModelId;
            }
            else
            {
                (Guid manufacturerId, Guid resolvedModelId) = await ResolveCatalogIdsAsync(
                    catalog,
                    seed,
                    unknownManufacturerId.Value,
                    unknownModelId.Value,
                    cancellationToken);
                modelId = resolvedModelId;

                printer = new Printer
                {
                    Id = seed.Id,
                    Name = seed.Name,
                    ServerUrl = seed.ServerUrl,
                    OriginalServerUrl = seed.ServerUrl,
                    BackendPort = 7125,
                    FrontendPort = 7125,
                    Backend = (int)PrinterBackend.Moonraker,
                    IsEnabled = seed.IsEnabled,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                };

                await unitOfWork.Printers.AddAsync(printer, cancellationToken);
                printers.Add(printer);
            }

            printer.DispatchState ??= new PrinterDispatchState
            {
                PrinterId = printer.Id,
            };
            if (resetRuntimeState)
            {
                ResetDispatchState(printer.DispatchState);
            }

            await SeedActiveJobAsync(unitOfWork, jobs, printer, seed, cancellationToken);

            CalibrationProfileTrio trio = await EnsureCalibrationProfileTrioAsync(
                machineProfiles,
                processProfiles,
                filamentProfiles,
                modelId,
                profileTrioCache,
                cancellationToken);
            ApplyCalibrationEligibilityDefaults(unitOfWork, printer, trio, isNewPrinter: existing is null);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (MoonrakerEmulatorPrinterSeed seed in settings.Printers.Where(seed =>
                     !seed.Name.Equals("Moonraker Offline", StringComparison.Ordinal)))
        {
            await printersService.RefreshCameraUrlsAsync(seed.Id, cancellationToken);
        }

        logger.LogInformation(
            "Seeded {Count} deterministic Moonraker emulator printers",
            settings.Printers.Count);
        return true;
    }

    private static bool IsDeterministicDiscoveryFixture(Printer printer) =>
        Uri.TryCreate(printer.OriginalServerUrl ?? printer.ServerUrl, UriKind.Absolute, out Uri? uri) &&
        uri.Host.StartsWith("moonraker-discovery-", StringComparison.OrdinalIgnoreCase);

    private static void ResetDispatchState(PrinterDispatchState state)
    {
        state.AutoDispatchState = AutoDispatchState.None;
        state.BedPreConfirmed = false;
        state.AcknowledgedJobId = null;
        state.AcknowledgedAtUtc = null;
        state.AcknowledgedBySubject = null;
        state.AcknowledgementIdempotencyKey = null;
        state.AcknowledgementExpiresAtUtc = null;
        state.AcknowledgedJobRowVersion = null;
        state.AcknowledgedQueueRevision = null;
        state.AcknowledgedPrinterConfigRevision = null;
        state.ActiveJobId = null;
        state.ActiveDispatchAttemptId = null;
        state.PhysicalControlCommandId = null;
        state.PhysicalControlAttemptId = null;
        state.PhysicalControlOperation = null;
        state.PhysicalControlActorSubject = null;
        state.PhysicalControlStartedAtUtc = null;
        state.PhysicalControlRequiresReconciliation = false;
    }

    private static async Task SeedActiveJobAsync(
        IUnitOfWork unitOfWork,
        List<PrintJob> jobs,
        Printer printer,
        MoonrakerEmulatorPrinterSeed seed,
        CancellationToken cancellationToken)
    {
        if (seed.ActiveJobId is not Guid activeJobId || seed.ActiveJobStatus is not PrintJobStatus status)
        {
            printer.DispatchState!.ActiveJobId = null;
            printer.DispatchState.ActiveDispatchAttemptId = null;
            return;
        }

        PrintJob? job = jobs.FirstOrDefault(candidate => candidate.Id == activeJobId);
        if (job is null)
        {
            DateTime now = DateTime.UtcNow;
            job = new PrintJob
            {
                Id = activeJobId,
                Name = "benchy.gcode",
                AssignedPrinterId = printer.Id,
                Status = status,
                ActualStartTime = now.AddMinutes(-2),
                CreatedAt = now.AddMinutes(-3),
                UpdatedAt = now,
                QueuedAt = now.AddMinutes(-3),
            };
            await unitOfWork.Queue.AddWithoutSaveAsync(job, cancellationToken);
            jobs.Add(job);
        }
        else
        {
            job.AssignedPrinterId = printer.Id;
            job.Status = status;
        }

        DateTime resetNow = DateTime.UtcNow;
        job.ActualStartTime = resetNow.AddMinutes(-2);
        job.ActualEndTime = null;
        job.UpdatedAt = resetNow;

        printer.DispatchState!.ActiveJobId = job.Id;
        printer.DispatchState.ActiveDispatchAttemptId = null;
    }

    private static async Task<(Guid ManufacturerId, Guid ModelId)> ResolveCatalogIdsAsync(
        ICatalogRepository catalog,
        MoonrakerEmulatorPrinterSeed seed,
        Guid unknownManufacturerId,
        Guid unknownModelId,
        CancellationToken cancellationToken)
    {
        Manufacturer? manufacturer = await catalog.FindManufacturerByNameAsync(
            seed.Manufacturer,
            cancellationToken);
        if (manufacturer is null)
        {
            return (unknownManufacturerId, unknownModelId);
        }

        PrinterModel? model = await catalog.FindModelByNameAsync(
            seed.Model,
            manufacturer.Id,
            cancellationToken);
        return (manufacturer.Id, model?.Id ?? unknownModelId);
    }

    /// <summary>
    /// Identifiers of the shared machine/process/filament profile trio backing calibration
    /// eligibility for a given catalog printer model.
    /// </summary>
    private readonly record struct CalibrationProfileTrio(
        Guid MachineProfileId,
        Guid ProcessProfileId,
        Guid FilamentProfileId,
        string MachineProfileName);

    /// <summary>
    /// Finds or creates the OrcaSlicer machine/process/filament profile trio that lets the
    /// daily-validation emulator printers demonstrate calibration eligibility end-to-end
    /// (#1851). All seeded printers share a single catalog model (Voron 2.4 300), so one
    /// content-hash-keyed trio per model is enough; profiles are looked up by hash first so
    /// repeated seed/reset passes stay idempotent and never grow duplicate rows.
    /// </summary>
    private static async Task<CalibrationProfileTrio> EnsureCalibrationProfileTrioAsync(
        IMachineProfileRepository machineProfiles,
        IProcessProfileRepository processProfiles,
        IFilamentProfileRepository filamentProfiles,
        Guid modelId,
        Dictionary<Guid, CalibrationProfileTrio> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(modelId, out CalibrationProfileTrio cached))
        {
            return cached;
        }

        DateTime nowUtc = DateTime.UtcNow;
        const string machineProfileName = "Voron 2.4 300 (Emulator Calibration)";

        // The model id is embedded in every RawJson payload (not just a random tiebreaker) so the
        // content-addressed Hash is naturally scoped per catalog model: a hash lookup can never
        // return a profile seeded for a different model, and re-seeding against the SAME model
        // always reproduces the identical, idempotent RawJson/Hash pair.
        string machineJson =
            $$"""{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"printer_variant":"Voron 2.4 300","printer_model_id":"{{modelId}}"}""";
        string machineHash = ComputeSha256(machineJson);
        MachineProfile machineProfile = await GetOrCreateMachineProfileAsync(
            machineProfiles,
            machineHash,
            () => new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = machineProfileName,
                Manufacturer = "Voron",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = machineJson,
                Hash = machineHash,
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            },
            cancellationToken);

        string processJson =
            $$"""{"layer_height":0.2,"infill_density":20,"process_variant":"Voron 2.4 300 Calibration","printer_model_id":"{{modelId}}"}""";
        string processHash = ComputeSha256(processJson);
        ProcessProfile processProfile = await GetOrCreateProcessProfileAsync(
            processProfiles,
            processHash,
            () => new ProcessProfile
            {
                Id = Guid.NewGuid(),
                Name = "Voron 2.4 300 Calibration Process",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = processJson,
                Hash = processHash,
                CompatiblePrinters = machineProfile.Name,
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            },
            cancellationToken);

        string filamentJson =
            $$"""{"filament_max_volumetric_speed":12,"filament_variant":"Voron 2.4 300 PLA","printer_model_id":"{{modelId}}"}""";
        string filamentHash = ComputeSha256(filamentJson);
        FilamentProfile filamentProfile = await GetOrCreateFilamentProfileAsync(
            filamentProfiles,
            filamentHash,
            () => new FilamentProfile
            {
                Id = Guid.NewGuid(),
                Name = "Voron 2.4 300 Calibration PLA",
                Material = "PLA",
                Manufacturer = "Generic",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                NozzleTemperature = 210,
                BedTemperature = 60,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = filamentJson,
                Hash = filamentHash,
                CompatiblePrinters = machineProfile.Name,
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            },
            cancellationToken);

        CalibrationProfileTrio trio = new(
            machineProfile.Id,
            processProfile.Id,
            filamentProfile.Id,
            machineProfile.Name);
        cache[modelId] = trio;
        return trio;
    }

    /// <summary>
    /// Looks up a profile by its content-addressed hash and creates it when absent, tolerating a
    /// concurrent seed/reset race: if two overlapping calls both miss the hash lookup and both
    /// insert, the losing insert hits the repository's unique Hash index and throws
    /// <see cref="DbUpdateException"/> — in that case the row the winner just committed is
    /// re-queried and reused instead of the failure propagating to the caller (which, for
    /// <c>ResetAsync</c>, has no retry of its own).
    /// </summary>
    private static async Task<MachineProfile> GetOrCreateMachineProfileAsync(
        IMachineProfileRepository repository,
        string hash,
        Func<MachineProfile> createProfile,
        CancellationToken cancellationToken)
    {
        MachineProfile? existing = await repository.GetByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        MachineProfile created = createProfile();
        try
        {
            await repository.AddAsync(created, cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            MachineProfile? winner = await repository.GetByHashAsync(hash, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    /// <summary>See <see cref="GetOrCreateMachineProfileAsync"/> for the race-tolerance rationale.</summary>
    private static async Task<ProcessProfile> GetOrCreateProcessProfileAsync(
        IProcessProfileRepository repository,
        string hash,
        Func<ProcessProfile> createProfile,
        CancellationToken cancellationToken)
    {
        ProcessProfile? existing = await repository.GetByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        ProcessProfile created = createProfile();
        try
        {
            await repository.AddAsync(created, cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            ProcessProfile? winner = await repository.GetByHashAsync(hash, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    /// <summary>See <see cref="GetOrCreateMachineProfileAsync"/> for the race-tolerance rationale.</summary>
    private static async Task<FilamentProfile> GetOrCreateFilamentProfileAsync(
        IFilamentProfileRepository repository,
        string hash,
        Func<FilamentProfile> createProfile,
        CancellationToken cancellationToken)
    {
        FilamentProfile? existing = await repository.GetByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        FilamentProfile created = createProfile();
        try
        {
            await repository.AddAsync(created, cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            FilamentProfile? winner = await repository.GetByHashAsync(hash, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    /// <summary>
    /// Populates every calibration-eligibility column <c>PrinterCalibrationContextService</c>
    /// requires (firmware identity, slicer identity, hardware/motion specs, and a physical
    /// toolhead) so seeded emulator printers report <c>eligible: true</c> instead of the ~40
    /// missing-input rejections filed as #1851. The eligibility gate itself is never touched —
    /// this only supplies the data it already requires.
    /// </summary>
    /// <param name="unitOfWork">
    /// Provides the printer repository used to append a toolhead for an already-tracked printer.
    /// </param>
    /// <param name="printer">The printer to populate calibration-eligibility fields on.</param>
    /// <param name="trio">The resolved machine/process/filament calibration profile trio.</param>
    /// <param name="isNewPrinter">
    /// <see langword="true"/> for a printer not yet tracked by EF Core (safe to append a child
    /// <see cref="Toolhead"/> directly to <see cref="Printer.Toolheads"/>); <see langword="false"/>
    /// for an already-tracked printer, where the toolhead must go through
    /// <c>IPrintersRepository.AddToolheads</c> to avoid marking the parent row Modified
    /// and tripping optimistic-concurrency RowVersion checks.
    /// </param>
    private static void ApplyCalibrationEligibilityDefaults(
        IUnitOfWork unitOfWork,
        Printer printer,
        CalibrationProfileTrio trio,
        bool isNewPrinter)
    {
        DateTime nowUtc = DateTime.UtcNow;

        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.12.0";
        printer.FirmwareDetectionVersion = "printer-info-v1";
        printer.FirmwareDetectionConfidence = 1m;
        printer.FirmwareDetectedAtUtc = nowUtc;
        printer.FirmwareIdentityVerified = true;
        printer.BackendVersion = "v0.9.3";
        printer.BackendApiVersion = "v1";

        printer.MaxBuildVolumeX = 250;
        printer.MaxBuildVolumeY = 250;
        printer.MaxBuildVolumeZ = 250;
        printer.BedOriginX = 0;
        printer.BedOriginY = 0;
        printer.PrintablePolygonJson =
            """[{"x":0,"y":0},{"x":250,"y":0},{"x":250,"y":250},{"x":0,"y":250}]""";
        printer.ExcludedRegionsJson = "[]";
        printer.CalibrationMotionType = CalibrationMotionType.CoreXY;
        printer.MaxPrintSpeed = 300;
        printer.MaxTravelSpeed = 500;
        printer.MaxAcceleration = 10000;
        printer.MaxTravelAcceleration = 12000;
        printer.CalibrationHasHeatedBed = true;
        printer.MaxBedTemp = 120;
        printer.CalibrationHasEnclosure = false;
        printer.CalibrationHasHeatedChamber = false;
        printer.ActiveToolheadIndex = 0;
        printer.SupportsPressureAdvance = true;
        printer.SupportsFirmwareRetraction = true;
        printer.CalibrationHardwareVerifiedAtUtc = nowUtc;

        printer.CalibrationSlicerEngine = CalibrationContractConstants.SlicerEngine;
        printer.CalibrationSlicerDistribution = CalibrationContractConstants.SlicerDistribution;
        printer.CalibrationSlicerVersion = CalibrationContractConstants.SlicerVersion;
        printer.CalibrationProfileFormat = CalibrationContractConstants.ProfileFormat;
        printer.CalibrationMachineProfileId = trio.MachineProfileId;
        printer.CalibrationProcessProfileId = trio.ProcessProfileId;
        printer.CalibrationFilamentProfileId = trio.FilamentProfileId;

        // Guard on ANY existing physical toolhead, not just index 0: these emulator-seeded
        // fixture printers are exclusively created and managed by this seeder and never gain
        // toolheads through any other code path, so "no physical toolhead yet" and "exactly one
        // physical toolhead" are equivalent here — this only prevents re-adding T0 on a repeat
        // seed/reset pass, it is not a general-purpose toolhead-count enforcement for arbitrary
        // printers (which is intentionally out of scope for a seeder; the eligibility gate itself
        // is the correct place to reject printers with unsupported extra toolheads).
        if (!printer.Toolheads.Any(toolhead => toolhead.ToolheadType == ToolheadType.Physical))
        {
            Toolhead toolhead = new()
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = "T0",
                Index = 0,
                IsPrimary = true,
                ToolheadType = ToolheadType.Physical,
                OffsetX = 0,
                OffsetY = 0,
                OffsetZ = 0,
                NozzleDiameter = 0.4,
                NozzleType = NozzleType.Brass,
                NozzleMaterial = "brass",
                NozzleMaxTemperature = 300,
                NozzleIsHardened = false,
                HotendMaxTemperature = 300,
                MaxVolumetricFlow = 15,
                DriveType = "direct",
                IsDirectDrive = true,
                ExtruderGearRatio = "50:10",
                SupportedMaterials = ["PLA", "PETG"],
            };

            if (isNewPrinter)
            {
                printer.Toolheads.Add(toolhead);
            }
            else
            {
                unitOfWork.Printers.AddToolheads([toolhead]);
            }
        }
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateSeed(MoonrakerEmulatorPrinterSeed seed)
    {
        if (seed.Id == Guid.Empty)
        {
            throw new InvalidOperationException("Moonraker emulator printer IDs must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(seed.Name))
        {
            throw new InvalidOperationException("Moonraker emulator printer names are required.");
        }

        if (!Uri.TryCreate(seed.ServerUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"Moonraker emulator printer '{seed.Name}' has an invalid server URL.");
        }
    }
}
