using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;

namespace Farm.Web.Api.Services.SlicerServices;

public partial class DbSlicerSettingsService : ISlicerSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DbSlicerSettingsService> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static int _loggedMissingTableFlag; // 0 = not logged, 1 = logged (Interlocked for thread-safety)
    private readonly IHostEnvironment _env;

    public DbSlicerSettingsService(IServiceScopeFactory scopeFactory, ILogger<DbSlicerSettingsService> logger, IServiceProvider rootProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(rootProvider);
        _env = (IHostEnvironment?)rootProvider.GetService(typeof(IHostEnvironment)) ?? new HostingEnvironmentFallback();
    }

    public SlicerSettingsDto GetSettings()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("[SlicerSettings] Retrieving settings record");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        SlicerSettings? entity = null;
        var tableReady = true;
        try
        {
            entity = db.SlicerSettings.FirstOrDefault(s => s.Id == 1);
        }
        catch (Exception ex)
        {
            if (IsMissingTable(ex))
            {
                tableReady = false;
                if (System.Threading.Interlocked.Exchange(ref _loggedMissingTableFlag, 1) == 0)
                {
                    _logger.LogDebug("[SlicerSettings] Table not yet available (startup not ready). Suppressing creation until ready.");
                }
            }
            else
            {
                // Non-missing-table errors still logged (demoted to debug in test env to reduce noise)
                if (_env.IsEnvironment("Testing"))
                {
                    _logger.LogDebug(ex, "[SlicerSettings] Query failed (elapsed {Elapsed} ms) - env=Testing", sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogWarning(ex, "[SlicerSettings] Query failed (elapsed {Elapsed} ms)", sw.ElapsedMilliseconds);
                }
            }
        }

        if (!tableReady)
        {
            // Return in-memory defaults without attempting to create the row (avoids repeated failing writes during early startup/tests)
            sw.Stop();
            return FallbackDto();
        }

        if (entity == null)
        {
            // Create default record and persist so DB reflects runtime defaults
            entity = new SlicerSettings
            {
                Id = 1,
                Enabled = true,
                PerEngineJson = JsonSerializer.Serialize(new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(), _serializerOptions),
                UpdatedAt = DateTime.UtcNow
            };
            db.SlicerSettings.Add(entity);
            db.SaveChanges();
            _logger.LogInformation("[SlicerSettings] Created default settings row (elapsed {Elapsed} ms)", sw.ElapsedMilliseconds);
        }

        Dictionary<SlicerEngineType, PerEngineSlicerSetting>? perEngine = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(entity.PerEngineJson))
            {
                perEngine = JsonSerializer.Deserialize<Dictionary<SlicerEngineType, PerEngineSlicerSetting>>(entity.PerEngineJson!, _serializerOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize per-engine slicer settings from DB; falling back to empty map");
        }

        sw.Stop();
        _logger.LogDebug("[SlicerSettings] Settings retrieval complete in {Elapsed} ms (Enabled={Enabled})", sw.ElapsedMilliseconds, entity.Enabled);
        return new SlicerSettingsDto(entity.Enabled, perEngine ?? new(), entity.JitterPercent);
    }

    public void SaveSettings(SlicerSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = db.SlicerSettings.FirstOrDefault(s => s.Id == 1);
        if (entity == null)
        {
            entity = new SlicerSettings { Id = 1 };
            db.SlicerSettings.Add(entity);
        }

        entity.Enabled = settings.Enabled;
        entity.PerEngineJson = JsonSerializer.Serialize(settings.PerEngine, _serializerOptions);
        entity.JitterPercent = settings.JitterPercent;
        entity.UpdatedAt = DateTime.UtcNow;

        db.SaveChanges();
    }
    private static bool IsMissingTable(Exception ex)
        => ex is SqliteException sqlEx && sqlEx.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    // Cached default DTO (immutable) to avoid repeated allocations & serialization during early startup race conditions.
    private static readonly SlicerSettingsDto CachedDefault = new(
        Enabled: true,
        PerEngine: new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(),
        JitterPercent: 0);

    private static SlicerSettingsDto FallbackDto()
    {
        return CachedDefault;
    }
}

file sealed class HostingEnvironmentFallback : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = typeof(HostingEnvironmentFallback).Assembly.GetName().Name!;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

// (helper members moved into primary class)
