using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Seeds configured test printers into the database on startup when the emulator is enabled.
/// Runs once and stops — not a long-running background service.
/// </summary>
public sealed class TestEmulatorSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<TestEmulatorSettings> options,
    TestEmulatorStateManager stateManager,
    ILogger<TestEmulatorSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TestEmulatorSettings settings = options.Value;
        if (!settings.Enabled || settings.Printers.Count == 0)
        {
            return;
        }

        logger.LogInformation("TestEmulatorSeeder: seeding {Count} test printers", settings.Printers.Count);

        using IServiceScope scope = scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ICatalogRepository catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();

        // Resolve default "Unknown" manufacturer and model (seeded by DatabaseInitializer)
        Guid? unknownMfgId = await catalog.GetUnknownManufacturerIdAsync(cancellationToken);
        Guid? unknownModelId = await catalog.GetUnknownModelIdAsync(cancellationToken);

        if (!unknownMfgId.HasValue || !unknownModelId.HasValue)
        {
            logger.LogError("TestEmulatorSeeder: Unknown manufacturer/model not found — database may not be seeded yet");
            return;
        }

        // Load all printers once outside the loop to avoid N×GetAllAsync calls
        List<Printer> allPrinters = await unitOfWork.Printers.GetAllAsync(cancellationToken);

        foreach (EmulatedPrinterConfig config in settings.Printers)
        {
            Guid printerId;

            // Find an existing emulator printer by name + server URL prefix
            Printer? existing = allPrinters.FirstOrDefault(p =>
                p.Name == config.Name && p.ServerUrl.StartsWith("http://testemulator-", StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                printerId = existing.Id;
                logger.LogDebug("TestEmulatorSeeder: printer '{Name}' already exists (id={Id})", config.Name, printerId);
            }
            else
            {
                printerId = Guid.NewGuid();
                var printer = new Printer
                {
                    Id = printerId,
                    Name = config.Name,
                    ServerUrl = $"http://testemulator-{printerId}",
                    BackendPort = 80,
                    Backend = BackendTypeId,
                    IsEnabled = true,
                    ManufacturerId = unknownMfgId.Value,
                    ModelId = unknownModelId.Value,
                };

                await unitOfWork.Printers.AddAsync(printer, cancellationToken);
                logger.LogInformation("TestEmulatorSeeder: created printer '{Name}' (id={Id})", config.Name, printerId);
            }

            // Register state in the emulator state manager
            EmulatorPrinterState initialState = ParseInitialState(config.InitialState);
            var state = new EmulatedPrinterState
            {
                State = initialState,
                Progress = config.Progress,
                PrintDurationSeconds = config.PrintDurationSeconds > 0 ? config.PrintDurationSeconds : 60,
            };

            if (initialState == EmulatorPrinterState.Printing)
            {
                // Back-calculate PrintStartedAt based on progress and duration
                double elapsedForProgress = (config.Progress / 100.0) * state.PrintDurationSeconds;
                state.PrintStartedAt = DateTime.UtcNow.AddSeconds(-elapsedForProgress);
                state.JobName = "test-print-benchy.gcode";
            }

            stateManager.Register(printerId, state);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("TestEmulatorSeeder: seeding complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Backend int value for TestEmulator — matches the 100 in BackendPluginAttribute
    private const int BackendTypeId = 100;

    private static EmulatorPrinterState ParseInitialState(string state) =>
        state?.ToLowerInvariant() switch
        {
            "printing" => EmulatorPrinterState.Printing,
            "paused" => EmulatorPrinterState.Paused,
            "error" => EmulatorPrinterState.Error,
            "offline" => EmulatorPrinterState.Offline,
            "complete" => EmulatorPrinterState.Complete,
            _ => EmulatorPrinterState.Idle
        };
}
