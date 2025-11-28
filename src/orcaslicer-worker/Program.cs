using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core; // shared worker core abstractions (IWorkerStateService, WorkerStateService, IProgressReporter, HttpProgressReporter, GracefulShutdownService, ISlicingPipelineService)
using Farm.Infrastructure; // For AllProfilesResponseDto
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker;

internal static class WorkerConstants
{
    public static readonly string[] Capabilities = ["orcaslicer", "stl-processing", "gcode-generation"];
}

public static class Program
{
    // Cached JsonSerializerOptions for performance (CA1869)
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole();

        // Add controllers support
        _ = builder.Services.AddControllers();

        // HTTP clients (for API communication, artifact upload, and slicing pipeline)
        _ = builder.Services.AddHttpClient(); // Required for HttpJobPollerService
        _ = builder.Services.AddHttpClient<HttpProgressReporter>(); // shared core implementation
        _ = builder.Services.AddHttpClient<OrcaSlicingPipelineService>(); // engine-specific pipeline
        _ = builder.Services.AddHttpClient<SlicerRegistrationClient>(); // registration client

        // Worker services (shared core + engine specific)
        _ = builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>(); // shared
        _ = builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>(); // engine specific
        _ = builder.Services.AddScoped<ISlicingPipelineService, OrcaSlicingPipelineService>(); // engine pipeline implements shared interface
        _ = builder.Services.AddScoped<IProgressReporter, HttpProgressReporter>(); // shared
        _ = builder.Services.AddSingleton<ISlicerRegistrationClient, SlicerRegistrationClient>(); // registration
        _ = builder.Services.AddSingleton<ISlicerProfilesService, OrcaProfilesService>(); // generic profiles interface, implemented by OrcaSlicer


        // Telemetry: provide a PrintFarmer telemetry implementation so UnifiedLoggingService can be constructed
        _ = builder.Services.AddSingleton<IPrintFarmerTelemetryService, PrintFarmerTelemetryService>();
        _ = builder.Services.AddScoped<IUnifiedLoggingService, UnifiedLoggingService>();

        // Background services (shared graceful shutdown + queue consumer derived)
        _ = builder.Services.AddHostedService<GracefulShutdownService>(); // shared
        _ = builder.Services.AddHostedService<QueueConsumerService>(); // derived
        _ = builder.Services.AddHostedService<RegistrationBackgroundService>(); // registration & heartbeat

        // Health checks
        _ = builder.Services.AddHealthChecks()
            .AddCheck<WorkerLivenessHealthCheck>("liveness")
            .AddCheck<WorkerReadinessHealthCheck>("readiness")
            .AddCheck<OrcaBinaryHealthCheck>("orca_binary");

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            _ = app.UseDeveloperExceptionPage();
        }

        // Enable routing and controller mapping
        _ = app.UseRouting();
        _ = app.MapControllers();

        _ = app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = c => c.Name == "liveness",
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
                    timestamp = DateTime.UtcNow
                }));
            }
        });

        string? relaxedEnv = Environment.GetEnvironmentVariable("WORKER_RELAXED_READINESS");
        bool relaxedReadiness = !string.IsNullOrEmpty(relaxedEnv) && relaxedEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (relaxedReadiness)
        {
            app.Logger.LogWarning("WORKER_RELAXED_READINESS=true -> orca_binary will be excluded from readiness evaluation.");
        }

        _ = app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary"),
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ready" : "not-ready",
                    relaxed = relaxedReadiness,
                    timestamp = DateTime.UtcNow,
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new { status = e.Value.Status.ToString(), description = e.Value.Description }
                    )
                }));
            }
        });

        _ = app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = c => (c.Name == "readiness" || c.Name == "orca_binary") && (!relaxedReadiness || c.Name != "orca_binary")
        });

        _ = app.MapGet("/", (IOrcaBinaryDetector detector) => Results.Ok(new
        {
            service = "orcaslicer-worker",
            version = "1.0.0",
            status = "running",
            realBinary = detector.IsRealBinaryPresent(),
            capabilities = WorkerConstants.Capabilities
        }));

        _ = app.MapGet("/version", async (IOrcaBinaryDetector detector) =>
        {
            string? orcaVersion = await detector.GetVersionAsync();
            return Results.Ok(new
            {
                orcaslicerVersion = orcaVersion,
                workerVersion = "1.0.0",
                timestamp = DateTime.UtcNow
            });
        });

        IOrcaBinaryDetector orcaDetector = app.Services.GetRequiredService<IOrcaBinaryDetector>();
        if (!orcaDetector.IsRealBinaryPresent())
        {
            app.Logger.LogWarning("OrcaSlicer binary not present (stub in use) - readiness will be unhealthy for orca_binary.");
        }

        // Preload profiles for catalog manufacturers at startup and measure timing
        _ = Task.Run(async () =>
        {
            try
            {
                app.Logger.LogInformation("Starting OrcaSlicer profile preload for catalog manufacturers...");
                Stopwatch stopwatch = Stopwatch.StartNew();

                ISlicerProfilesService profileService = app.Services.GetRequiredService<ISlicerProfilesService>();

                // First load all machines to get the list of available manufacturers
                Stopwatch machineStart = Stopwatch.StartNew();
                IList<MachineProfileDto> machines = await profileService.ListAvailableMachineProfilesAsync();
                machineStart.Stop();

                // Get the set of manufacturers available in OrcaSlicer profiles
                HashSet<string> availableManufacturers = machines
                    .Where(m => !string.IsNullOrEmpty(m.Manufacturer))
                    .Select(m => m.Manufacturer!)
                    .Distinct()
                    .ToHashSet();

                app.Logger.LogInformation("Found {ManufacturerCount} manufacturers with {MachineCount} machine profiles in {ElapsedMilliseconds}ms", availableManufacturers.Count, machines.Count, machineStart.ElapsedMilliseconds);

                // Load catalog manufacturers via HTTP (call the API)
                HttpClient httpClient = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
                string catalogUrl = Environment.GetEnvironmentVariable("CATALOG_API_URL") ?? "http://localhost:5245";

                try
                {
                    HttpResponseMessage response = await httpClient.GetAsync(new Uri($"{catalogUrl}/api/catalog/manufacturers")).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        List<ManufacturerDto>? manufacturerDtos = JsonSerializer.Deserialize<List<ManufacturerDto>>(
                            content,
                            s_jsonOptions
                        );

                        HashSet<string> catalogManufacturers = manufacturerDtos?
                            .Select(m => m.Name)
                            .ToHashSet() ?? [];

                        app.Logger.LogInformation("Catalog has {CatalogManufacturerCount} manufacturers", catalogManufacturers.Count);

                        // Load filament and process profiles only for manufacturers in catalog
                        Stopwatch filamentStart = Stopwatch.StartNew();
                        IList<FilamentProfileDto> filaments = await profileService.ListAvailableFilamentProfilesAsync().ConfigureAwait(false);
                        int catalogFilaments = filaments
                            .Count(f => string.IsNullOrEmpty(f.Manufacturer) || catalogManufacturers.Contains(f.Manufacturer));
                        filamentStart.Stop();

                        Stopwatch processStart = Stopwatch.StartNew();
                        IList<ProcessProfileDto> processes = await profileService.ListAvailableProcessProfilesAsync();
                        processStart.Stop();

                        stopwatch.Stop();

                        app.Logger.LogInformation(
                            "OrcaSlicer profiles preloaded in {TotalElapsed}ms: {MachineCount} machines ({MachineElapsed}ms), {CatalogFilaments}/{TotalFilaments} filaments for catalog ({FilamentElapsed}ms), {ProcessCount} processes ({ProcessElapsed}ms)",
                            stopwatch.ElapsedMilliseconds,
                            machines.Count,
                            machineStart.ElapsedMilliseconds,
                            catalogFilaments,
                            filaments.Count,
                            filamentStart.ElapsedMilliseconds,
                            processes.Count,
                            processStart.ElapsedMilliseconds
                        );
                    }
                    else
                    {
                        app.Logger.LogWarning("Failed to fetch catalog manufacturers: {StatusCode}. Skipping filtered preload.", response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning("Error fetching catalog manufacturers: {Exception}. Loading all profiles instead.", ex.Message);

                    // Fallback: load all profiles if catalog API is unavailable
                    Stopwatch filamentStart = Stopwatch.StartNew();
                    IList<FilamentProfileDto> filaments = await profileService.ListAvailableFilamentProfilesAsync();
                    filamentStart.Stop();

                    Stopwatch processStart = Stopwatch.StartNew();
                    IList<ProcessProfileDto> processes = await profileService.ListAvailableProcessProfilesAsync();
                    processStart.Stop();

                    stopwatch.Stop();

                    app.Logger.LogInformation(
                        "OrcaSlicer profiles preloaded (fallback) in {TotalElapsed}ms: {MachineCount} machines ({MachineElapsed}ms), {FilamentCount} filaments ({FilamentElapsed}ms), {ProcessCount} processes ({ProcessElapsed}ms)",
                        stopwatch.ElapsedMilliseconds,
                        machines.Count,
                        machineStart.ElapsedMilliseconds,
                        filaments.Count,
                        filamentStart.ElapsedMilliseconds,
                        processes.Count,
                        processStart.ElapsedMilliseconds
                    );
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogError("Error preloading OrcaSlicer profiles: {Exception}", ex.Message);
            }
        });

        app.Run();
    }
}
