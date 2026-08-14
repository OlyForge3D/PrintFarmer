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
    ILogger<TestEmulatorSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TestEmulatorSettings settings = options.Value;
        if (!settings.Enabled || settings.Printers.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "TestEmulatorSeeder: waiting to seed {Count} test printers",
            settings.Printers.Count);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TrySeedAsync(settings, stoppingToken))
                {
                    return;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    exception,
                    "TestEmulatorSeeder: database is not ready; retrying");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    private async Task<bool> TrySeedAsync(
        TestEmulatorSettings settings,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ICatalogRepository catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();
        Guid? unknownMfgId = await catalog.GetUnknownManufacturerIdAsync(cancellationToken);
        Guid? unknownModelId = await catalog.GetUnknownModelIdAsync(cancellationToken);
        if (!unknownMfgId.HasValue || !unknownModelId.HasValue)
        {
            return false;
        }

        logger.LogInformation(
            "TestEmulatorSeeder: seeding {Count} test printers",
            settings.Printers.Count);

        // Load all printers once outside the loop to avoid N×GetAllAsync calls
        List<Printer> allPrinters = await unitOfWork.Printers.GetAllAsync(cancellationToken);

        foreach (EmulatedPrinterConfig config in settings.Printers)
        {
            Guid printerId;

            // Find an existing emulator printer by name + server URL prefix
            Printer? existing = allPrinters.FirstOrDefault(p =>
                p.Name == config.Name && p.ServerUrl.StartsWith(HostnamePrefix, StringComparison.OrdinalIgnoreCase));

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
                    ServerUrl = BuildServerUrl(printerId),
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
        return true;
    }

    // Backend int value for TestEmulator — matches the 100 in BackendPluginAttribute
    private const int BackendTypeId = 100;

    // Single source of truth for the internal-only hostname prefix, shared by BuildServerUrl (the
    // generator) and the existing-printer lookup above, so the two can never drift from each other.
    private const string HostnamePrefix = "http://testemulator-";

    /// <summary>
    /// Builds the internal-only, browser-unreachable ServerUrl for a seeded emulator printer.
    /// The React frontend (src/Web/ReactApp/src/common/utils/validation.ts,
    /// <c>INTERNAL_ONLY_HOSTNAME_PATTERNS</c>) detects this exact hostname shape — a "testemulator-"
    /// prefix followed by a lowercase, dashed Guid — to disable the "Open in Browser" action instead
    /// of rendering a broken link (issue #1546). Exposed as internal so the contract test in
    /// Farm.Web.Api.Tests (TestEmulatorServerUrlHostnameContractTests) can call the real production
    /// logic instead of duplicating it, guaranteeing a change here is caught if the frontend pattern
    /// isn't updated to match.
    /// </summary>
    internal static string BuildServerUrl(Guid printerId) => $"{HostnamePrefix}{printerId}";

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
