using PrinterDiscovery.BackgroundServices;
using PrinterDiscovery.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var apiBaseUrl = builder.Configuration["Discovery:ApiBaseUrl"] ?? "http://api:5245";
var enablePeriodicDiscovery = builder.Configuration.GetValue<bool>("Discovery:EnablePeriodicDiscovery", true);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// Discovery services
builder.Services.AddScoped<INetworkScanner, NetworkScanner>();
builder.Services.AddScoped<INetworkDiscoveryService, NetworkDiscoveryService>();

// HTTP client for network scanning
builder.Services.AddHttpClient<NetworkScanner>();

// API client for registering discovered printers with central API
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IApiClient>(sp => sp.GetRequiredService<ApiClient>());

// Add periodic discovery background service (if enabled)
if (enablePeriodicDiscovery)
{
    builder.Services.AddHostedService<PeriodicDiscoveryBackgroundService>();
}

// Add heartbeat background service to notify API of service availability
builder.Services.AddHostedService<HeartbeatBackgroundService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.MapControllers();

// Log startup configuration
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Printer Discovery Service Starting ===");
logger.LogInformation("API Base URL: {ApiBaseUrl}", apiBaseUrl);
logger.LogInformation("Periodic Discovery: {Enabled}", enablePeriodicDiscovery ? "Enabled" : "Disabled");
logger.LogInformation("Scan Interval: {Interval}s", builder.Configuration.GetValue<int>("Discovery:ScanIntervalSeconds", 300));
logger.LogInformation("Manual scan endpoint: POST /api/discovery/scan");
logger.LogInformation("Health check endpoint: GET /api/discovery/health");

app.Run();

