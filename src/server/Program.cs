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
builder.Services.AddHostedService<MoonrakerSubscriptionService>();
builder.Services.AddSingleton<PresetService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // --- Migration safety check for missing columns ---
    var conn = db.Database.GetDbConnection();
    conn.Open();
    using (var cmd = conn.CreateCommand())
    {
        // Add Backend column if missing
        cmd.CommandText = "PRAGMA table_info(Printers);";
        using var reader = cmd.ExecuteReader();
        bool hasBackend = false, hasApiKey = false;
        while (reader.Read())
        {
            var col = reader[1]?.ToString();
            if (col == "Backend") hasBackend = true;
            if (col == "ApiKey") hasApiKey = true;
        }
        reader.Close();
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
    conn.Close();
    db.Database.Migrate();
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
app.MapFallbackToFile("index.html");

app.Run();
