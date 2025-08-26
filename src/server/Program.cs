using Farm.Web.Server.Data;
using Farm.Web.Server.Services;
using Farm.Web.Server.Hubs;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=farm.db"));

builder.Services.AddHttpClient<MoonrakerClient>();
builder.Services.AddHttpClient<PrusaLinkClient>();
builder.Services.AddHttpClient<SpoolmanService>();
builder.Services.AddSignalR();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<MoonrakerSubscriptionService>();
}
builder.Services.AddSingleton<PresetService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var provider = db.Database.ProviderName;
    if (provider != null && (provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase) || app.Environment.IsEnvironment("Testing")))
    {
        db.Database.EnsureCreated();
    }
    else if (db.Database.IsSqlite())
    {
        // --- Migration safety check for missing columns (SQLite only) ---
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // Check if Printers table exists first
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Printers';";
            var exists = cmd.ExecuteScalar()?.ToString() == "Printers";
            if (exists)
            {
                // Add Backend column if missing
                cmd.CommandText = "PRAGMA table_info(Printers);";
                using var reader = cmd.ExecuteReader();
                bool hasBackend = false, hasApiKey = false, hasServerUrl = false, hasMoonrakerUrl = false, hasOriginalServerUrl = false, hasOriginalHostName = false;
                while (reader.Read())
                {
                    var col = reader[1]?.ToString();
                    if (col == "Backend") hasBackend = true;
                    if (col == "ApiKey") hasApiKey = true;
                    if (col == "ServerUrl") hasServerUrl = true;
                    if (col == "MoonrakerUrl") hasMoonrakerUrl = true;
                    if (col == "OriginalServerUrl") hasOriginalServerUrl = true;
                    if (col == "OriginalHostName") hasOriginalHostName = true;
                }
                reader.Close();
                // Rename MoonrakerUrl -> ServerUrl (or add ServerUrl) if needed
                if (!hasServerUrl)
                {
                    if (hasMoonrakerUrl)
                    {
                        try
                        {
                            cmd.CommandText = "ALTER TABLE Printers RENAME COLUMN MoonrakerUrl TO ServerUrl;";
                            cmd.ExecuteNonQuery();
                            hasServerUrl = true;
                        }
                        catch
                        {
                            // Fallback: add ServerUrl and copy data
                            try
                            {
                                cmd.CommandText = "ALTER TABLE Printers ADD COLUMN ServerUrl TEXT;";
                                cmd.ExecuteNonQuery();
                                cmd.CommandText = "UPDATE Printers SET ServerUrl = MoonrakerUrl WHERE ServerUrl IS NULL OR ServerUrl = ''";
                                cmd.ExecuteNonQuery();
                                hasServerUrl = true;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        try { cmd.CommandText = "ALTER TABLE Printers ADD COLUMN ServerUrl TEXT"; cmd.ExecuteNonQuery(); hasServerUrl = true; } catch { }
                    }
                }
                // Rename OriginalHostName -> OriginalServerUrl (or add OriginalServerUrl) if needed
                if (!hasOriginalServerUrl)
                {
                    if (hasOriginalHostName)
                    {
                        try { cmd.CommandText = "ALTER TABLE Printers RENAME COLUMN OriginalHostName TO OriginalServerUrl;"; cmd.ExecuteNonQuery(); hasOriginalServerUrl = true; }
                        catch { try { cmd.CommandText = "ALTER TABLE Printers ADD COLUMN OriginalServerUrl TEXT"; cmd.ExecuteNonQuery(); hasOriginalServerUrl = true; } catch { } }
                    }
                    else
                    {
                        try { cmd.CommandText = "ALTER TABLE Printers ADD COLUMN OriginalServerUrl TEXT"; cmd.ExecuteNonQuery(); hasOriginalServerUrl = true; } catch { }
                    }
                }
                if (!hasBackend)
                {
                    cmd.CommandText = "ALTER TABLE Printers ADD COLUMN Backend INTEGER DEFAULT 0;";
                    cmd.ExecuteNonQuery();
                }
                if (!hasApiKey)
                {
                    cmd.CommandText = "ALTER TABLE Printers ADD COLUMN ApiKey TEXT NULL;";
                    cmd.ExecuteNonQuery();
                }
            }
            else
            {
                // Create Printers table with the latest schema (idempotent for fresh DBs)
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Printers (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    ServerUrl TEXT,
                    OriginalServerUrl TEXT,
                    Notes TEXT NULL,
                    Backend INTEGER NOT NULL DEFAULT 0,
                    ApiKey TEXT NULL,
                    ManufacturerId TEXT NULL,
                    ModelId TEXT NULL,
                    DateAcquired TEXT NULL
                );";
                cmd.ExecuteNonQuery();
                // Helpful indexes (match migrations when present)
                try { cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Printers_ManufacturerId ON Printers(ManufacturerId);"; cmd.ExecuteNonQuery(); } catch { }
                try { cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Printers_ModelId ON Printers(ModelId);"; cmd.ExecuteNonQuery(); } catch { }
            }
        }
        conn.Close();
        db.Database.Migrate();
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
