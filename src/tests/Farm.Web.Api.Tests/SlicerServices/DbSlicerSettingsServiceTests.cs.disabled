using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.SlicerServices;

public class DbSlicerSettingsServiceTests
{
    private ServiceProvider BuildServiceProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.AddSingleton<ISlicerSettingsService, DbSlicerSettingsService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetSettings_WhenNoneExist_CreatesDefault()
    {
        var sp = BuildServiceProvider("dbsettings_test1");
        var svc = sp.GetRequiredService<ISlicerSettingsService>();

        var settings = svc.GetSettings();
        settings.Should().NotBeNull();
        settings.Enabled.Should().BeTrue();
        settings.PerEngine.Should().NotBeNull();
        settings.PerEngine.Should().BeEmpty();

        // Verify DB contains a row
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.SlicerSettings.FirstOrDefault(s => s.Id == 1);
        row.Should().NotBeNull();
        row!.PerEngineJson.Should().NotBeNull();
    }

    [Fact]
    public void SaveSettings_PersistsAndCanBeReadBack()
    {
        var sp = BuildServiceProvider("dbsettings_test2");
        var svc = sp.GetRequiredService<ISlicerSettingsService>();

        var newSettings = new SlicerSettingsDto(true, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>
        {
            { SlicerEngineType.OrcaSlicer, new PerEngineSlicerSetting("/usr/bin/orca", "--export-gcode -o {output} {input}") }
        }, 12.5);

        svc.SaveSettings(newSettings);

        // Read raw DB and assert JSON persisted
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.SlicerSettings.FirstOrDefault(s => s.Id == 1);
        row.Should().NotBeNull();
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var map = JsonSerializer.Deserialize<Dictionary<SlicerEngineType, PerEngineSlicerSetting>>(row!.PerEngineJson ?? "{}", opts);
        map.Should().ContainKey(SlicerEngineType.OrcaSlicer);
        map![SlicerEngineType.OrcaSlicer].Path.Should().Be("/usr/bin/orca");
        row.JitterPercent.Should().BeApproximately(12.5, 0.0001);

        // GetSettings should return the saved value
        var fetched = svc.GetSettings();
        fetched.PerEngine.Should().ContainKey(SlicerEngineType.OrcaSlicer);
        fetched.JitterPercent.Should().BeApproximately(12.5, 0.0001);
    }
}
