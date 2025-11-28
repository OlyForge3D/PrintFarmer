using Farm.Infrastructure.Discovery;
using PrinterDiscovery.BackgroundServices;
using PrinterDiscovery.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Configuration
string apiBaseUrl = builder.Configuration["Discovery:ApiBaseUrl"] ?? "http://api:5245";

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Register discovery probes as services
builder.Services.AddSingleton<INetworkDiscoveryProbe, MoonrakerDiscoveryProbe>();
builder.Services.AddSingleton<INetworkDiscoveryProbe, PrusaLinkDiscoveryProbe>();
builder.Services.AddSingleton<INetworkDiscoveryProbe, OctoPrintDiscoveryProbe>();
builder.Services.AddSingleton<INetworkDiscoveryProbe, SdcpDiscoveryProbe>();

// Register shared core discovery service
builder.Services.AddSingleton<ICoreNetworkDiscoveryService, CoreNetworkDiscoveryService>();

// Network discovery service (uses the shared core service)
builder.Services.AddScoped<INetworkDiscoveryService, NetworkDiscoveryService>();

// Session manager for tracking active discovery sessions (singleton for cross-request cancellation)
builder.Services.AddSingleton<IDiscoverySessionManager, DiscoverySessionManager>();

// SignalR progress broadcaster for streaming discovery
builder.Services.AddSingleton<IDiscoveryProgressBroadcaster, DiscoveryProgressBroadcaster>();

// Streaming discovery service with progress updates (Scoped to match IApiClient dependency)
builder.Services.AddScoped<IStreamingDiscoveryService, StreamingDiscoveryService>();

// API client for registering discovered printers with central API
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IApiClient>(sp => sp.GetRequiredService<ApiClient>());

// Add IHttpClientFactory for background services
builder.Services.AddHttpClient();

// Add periodic discovery background service (checks settings dynamically from API)
builder.Services.AddHostedService<PeriodicDiscoveryBackgroundService>();

// Add heartbeat background service to notify API of service availability
builder.Services.AddHostedService<HeartbeatBackgroundService>();

WebApplication app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

app.UseRouting();
app.MapControllers();

// Log startup configuration
ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Printer Discovery Service Starting ===");
logger.LogInformation("API Base URL: {ApiBaseUrl}", apiBaseUrl);
logger.LogInformation("Background scanning: Controlled by API settings (BackgroundScanEnabled)");
logger.LogInformation("Manual scan endpoint: POST /api/discovery/scan");
logger.LogInformation("Health check endpoint: GET /api/discovery/health");

app.Run();
