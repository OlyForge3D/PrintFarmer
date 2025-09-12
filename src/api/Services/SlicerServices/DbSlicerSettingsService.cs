using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices;

public class DbSlicerSettingsService : ISlicerSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DbSlicerSettingsService> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DbSlicerSettingsService(IServiceScopeFactory scopeFactory, ILogger<DbSlicerSettingsService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SlicerSettingsDto GetSettings()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = db.SlicerSettings.FirstOrDefault(s => s.Id == 1);
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

        return new SlicerSettingsDto(entity.Enabled, perEngine ?? new());
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
        entity.UpdatedAt = DateTime.UtcNow;

        db.SaveChanges();
    }
}
