using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
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
    /// <summary>
    /// Restores the deterministic database state used by emulator-backed browser tests.
    /// </summary>
    public async Task<bool> ResetAsync(CancellationToken cancellationToken)
    {
        MoonrakerEmulatorSeedSettings settings = options.Value;
        return settings.Enabled &&
            settings.Printers.Count > 0 &&
            await TrySeedAsync(settings, resetRuntimeState: true, cancellationToken);
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
        using IServiceScope scope = scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ICatalogRepository catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();
        IPrintersService printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();

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
            if (existing is not null)
            {
                Printer? tracked = await unitOfWork.Printers.FindByIdAsync(
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
            }
            else
            {
                (Guid manufacturerId, Guid modelId) = await ResolveCatalogIdsAsync(
                    catalog,
                    seed,
                    unknownManufacturerId.Value,
                    unknownModelId.Value,
                    cancellationToken);

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
