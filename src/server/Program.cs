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

builder.Services.AddHttpClient<MoonrakerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<PrusaLinkClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
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
                bool hasBackend = false, hasApiKey = false, hasServerUrl = false, hasMoonrakerUrl = false, hasOriginalServerUrl = false, hasOriginalHostName = false, hasIp = false;
                while (reader.Read())
                {
                    var col = reader[1]?.ToString();
                    if (col == "Backend") hasBackend = true;
                    if (col == "ApiKey") hasApiKey = true;
                    if (col == "ServerUrl") hasServerUrl = true;
                    if (col == "MoonrakerUrl") hasMoonrakerUrl = true;
                    if (col == "OriginalServerUrl") hasOriginalServerUrl = true;
                    if (col == "OriginalHostName") hasOriginalHostName = true;
                    if (col == "IpAddress") hasIp = true;
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
                // Backfill legacy NULL values for Backend to 0 (Moonraker) to match non-nullable EF model
                try
                {
                    cmd.CommandText = "UPDATE Printers SET Backend = 0 WHERE Backend IS NULL;";
                    cmd.ExecuteNonQuery();
                }
                catch { }
                if (!hasApiKey)
                {
                    cmd.CommandText = "ALTER TABLE Printers ADD COLUMN ApiKey TEXT NULL;";
                    cmd.ExecuteNonQuery();
                }
                if (!hasIp)
                {
                    cmd.CommandText = "ALTER TABLE Printers ADD COLUMN IpAddress TEXT NULL;";
                    cmd.ExecuteNonQuery();
                }
                // Ensure SpoolmanConfigs exists even on older DBs
                try
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SpoolmanConfigs';";
                    var hasCfg = cmd.ExecuteScalar()?.ToString() == "SpoolmanConfigs";
                    if (!hasCfg)
                    {
                        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS SpoolmanConfigs (
                            Id INTEGER NOT NULL PRIMARY KEY,
                            BaseUrl TEXT NOT NULL
                        );";
                        cmd.ExecuteNonQuery();
                    }
                    // Seed default Spoolman URL if no row exists
                    try
                    {
                        cmd.CommandText = @"INSERT INTO SpoolmanConfigs (Id, BaseUrl)
                                            SELECT 1, 'http://spoolman.local:7912'
                                            WHERE NOT EXISTS (SELECT 1 FROM SpoolmanConfigs WHERE Id = 1);";
                        cmd.ExecuteNonQuery();
                    }
                    catch { }
                }
                catch { }

                    // Ensure catalog tables exist for joins used by the API
                try
                {
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Manufacturers (
                        Id BLOB NOT NULL PRIMARY KEY,
                        Name TEXT COLLATE NOCASE NOT NULL UNIQUE
                    );";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Models (
                        Id BLOB NOT NULL PRIMARY KEY,
                        Name TEXT COLLATE NOCASE NOT NULL,
                        ManufacturerId BLOB NOT NULL,
                        MaxX REAL NULL,
                        MaxY REAL NULL,
                        MaxZ REAL NULL,
                        FOREIGN KEY (ManufacturerId) REFERENCES Manufacturers(Id) ON DELETE CASCADE
                    );";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Models_ManufacturerId_Name ON Models(ManufacturerId, Name);";
                    cmd.ExecuteNonQuery();

                    // Ensure dimension columns exist on Models for legacy DBs
                    try
                    {
                        cmd.CommandText = "PRAGMA table_info(Models);";
                        using var mcols = cmd.ExecuteReader();
                        bool hasMaxX = false, hasMaxY = false, hasMaxZ = false;
                        while (mcols.Read())
                        {
                            var col = mcols[1]?.ToString();
                            if (col == "MaxX") hasMaxX = true;
                            if (col == "MaxY") hasMaxY = true;
                            if (col == "MaxZ") hasMaxZ = true;
                        }
                        mcols.Close();
                        if (!hasMaxX) { cmd.CommandText = "ALTER TABLE Models ADD COLUMN MaxX REAL"; cmd.ExecuteNonQuery(); }
                        if (!hasMaxY) { cmd.CommandText = "ALTER TABLE Models ADD COLUMN MaxY REAL"; cmd.ExecuteNonQuery(); }
                        if (!hasMaxZ) { cmd.CommandText = "ALTER TABLE Models ADD COLUMN MaxZ REAL"; cmd.ExecuteNonQuery(); }
                    }
                    catch { }

                                        // Note: Seeding moved below using EF to ensure consistent GUID storage as TEXT
                }
                catch { }
            }
            else
            {
                // Create Printers table with the latest schema (idempotent for fresh DBs)
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Printers (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    ServerUrl TEXT,
                    OriginalServerUrl TEXT,
                    IpAddress TEXT NULL,
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

                // Create SpoolmanConfigs (single-row) table if missing
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS SpoolmanConfigs (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    BaseUrl TEXT NOT NULL
                );";
                cmd.ExecuteNonQuery();
                // Seed default Spoolman URL if no row exists
                try
                {
                    cmd.CommandText = @"INSERT INTO SpoolmanConfigs (Id, BaseUrl)
                                        SELECT 1, 'http://spoolman.local:7912'
                                        WHERE NOT EXISTS (SELECT 1 FROM SpoolmanConfigs WHERE Id = 1);";
                    cmd.ExecuteNonQuery();
                }
                catch { }

                // Create catalog tables for Manufacturers/Models
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Manufacturers (
                    Id BLOB NOT NULL PRIMARY KEY,
                    Name TEXT COLLATE NOCASE NOT NULL UNIQUE
                );";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Models (
                    Id BLOB NOT NULL PRIMARY KEY,
                    Name TEXT COLLATE NOCASE NOT NULL,
                    ManufacturerId BLOB NOT NULL,
                    MaxX REAL NULL,
                    MaxY REAL NULL,
                    MaxZ REAL NULL,
                    FOREIGN KEY (ManufacturerId) REFERENCES Manufacturers(Id) ON DELETE CASCADE
                );";
                cmd.ExecuteNonQuery();
                try { cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Models_ManufacturerId_Name ON Models(ManufacturerId, Name);"; cmd.ExecuteNonQuery(); } catch { }

                                // Note: Seeding moved below using EF
            }
        }
        conn.Close();
        // In containers or when requested, prefer EnsureCreated over Migrate
        if (disableEfMigrations || string.Equals(initMode, "EnsureCreated", StringComparison.OrdinalIgnoreCase))
        {
            db.Database.EnsureCreated();
        }
        else
        {
            db.Database.Migrate();
        }

        // EF-based seeding for catalog data (idempotent): ensures a set of manufacturers and models exist.
        try
        {
            // Desired manufacturers to ensure exist
            var manufacturerNames = new[]
            {
                "Prusa",
                "Elegoo",
                "Eryone",
                "Sovol",
                "RatRig",
                "VoronDesign",
                "PrintersForAnts"
            };

            var manufacturers = new Dictionary<string, Farm.Web.Server.Domain.Manufacturer>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in manufacturerNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name);
                if (existing == null)
                {
                    existing = new Farm.Web.Server.Domain.Manufacturer { Id = Guid.NewGuid(), Name = name };
                    db.Manufacturers.Add(existing);
                    await db.SaveChangesAsync();
                }
                manufacturers[name] = existing;
            }

            // Models to ensure exist (Name, ManufacturerName, MaxX/MaxY/MaxZ)
            var modelSeeds = new (string Name, string Mfg, double X, double Y, double Z)[]
            {
                ("AD5X", "Eryone", 220, 220, 220),
                ("SV08", "Sovol", 350, 350, 350),
                ("Thinker X400", "Eryone", 400, 400, 400),
                ("Centauri Carbon", "Elegoo", 256, 256, 256),
                ("Micron 120", "PrintersForAnts", 120, 120, 120),
                ("Micron 180", "PrintersForAnts", 180, 180, 165),
                ("Voron Trident 250", "VoronDesign", 250, 250, 250),
                ("Voron Trident 300", "VoronDesign", 300, 300, 250),
                ("Voron Trident 300 Cube", "VoronDesign", 300, 300, 300),
                ("Voron Trident 350", "VoronDesign", 350, 350, 250),
                ("Voron v0", "VoronDesign", 120, 120, 120),
                ("Voron v2.4 300", "VoronDesign", 300, 300, 300),
                ("Voron v2.4 350", "VoronDesign", 350, 350, 350),
                ("vCore4 400", "RatRig", 400, 400, 400),
                ("vCore4 500", "RatRig", 500, 500, 500),
                ("Original Prusa Mini+", "Prusa", 180, 180, 180),
                ("Original Prusa MK4S", "Prusa", 250, 210, 220),
                ("Original Prusa Core One", "Prusa", 250, 220, 270),
                ("Original Prusa i3 MK3S+", "Prusa", 250, 210, 210)
            };

            foreach (var (name, mfg, x, y, z) in modelSeeds)
            {
                if (!manufacturers.TryGetValue(mfg, out var m))
                {
                    // Skip if manufacturer wasn't ensured above for any reason
                    continue;
                }

                var exists = await db.Models.AnyAsync(pm => pm.ManufacturerId == m.Id && pm.Name == name);
                if (!exists)
                {
                    db.Models.Add(new Farm.Web.Server.Domain.PrinterModel
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        ManufacturerId = m.Id,
                        MaxX = x,
                        MaxY = y,
                        MaxZ = z
                    });
                }
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Catalog seeding error: {ex.Message}");
        }
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
