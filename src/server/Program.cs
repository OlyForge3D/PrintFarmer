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
