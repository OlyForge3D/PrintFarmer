using Farm.Web.Server.Data;
using Farm.Web.Server.Services;
using Farm.Web.Server.Hubs;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Shared;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Configure JSON options for .NET 9 compatibility
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = false;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure JSON options for minimal APIs
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=farm.db"));

builder.Services.AddHttpClient<MoonrakerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<PrusaLinkClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<SpoolmanService>();
builder.Services.AddScoped<SdcpClient>();
builder.Services.AddSignalR();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<MoonrakerSubscriptionService>();
}
builder.Services.AddSingleton<PresetService>();
builder.Services.AddScoped<DatabaseSeeder>();

builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Allow container/deployment scenarios to control DB init without EF migrations
    var initMode = Environment.GetEnvironmentVariable("DB_INIT_MODE")
                  ?? app.Configuration["Db:InitMode"]
                  ?? string.Empty;
    var disableEfMigrations = string.Equals(Environment.GetEnvironmentVariable("DISABLE_EF_MIGRATIONS"), "1", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(app.Configuration["DISABLE_EF_MIGRATIONS"], "true", StringComparison.OrdinalIgnoreCase);
    var provider = db.Database.ProviderName;
    if (provider != null && provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // Use standard EF migrations for all databases (including SQLite)
        if (disableEfMigrations || string.Equals(initMode, "EnsureCreated", StringComparison.OrdinalIgnoreCase))
        {
            db.Database.EnsureCreated();
        }
        else
        {
            db.Database.Migrate();
        }

        // EF-based seeding for catalog data (idempotent)
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAllAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<PrinterHub>("/hubs/printers");
// Minimal API for presets
app.MapGet("/api/presets", (PresetService svc) => Results.Ok(svc.GetPresets()));
app.MapPost("/api/presets", (PresetService svc, FilamentPresetsDto body) => { svc.SavePresets(body); return Results.NoContent(); });
// Basic health endpoint for UI ping and tests
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
