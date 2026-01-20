using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Models.SeedData;
using Farm.Web.Api.Services.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for reading and parsing YAML seed data files
/// </summary>
public class YamlSeedDataReader : IYamlSeedDataReader
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _seedDataPath;
    private readonly IDeserializer _yamlDeserializer;

    public YamlSeedDataReader(IUnifiedLoggingService logger, IConfiguration configuration)
    {
        _logger = logger;
        _seedDataPath = configuration["SeedData:Path"] ?? Path.Combine(AppContext.BaseDirectory, "data", "seed");

        // Configure YamlDotNet deserializer
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _logger.LogInformation("[SeedData] Configured seed data path: {SeedDataPath}", _seedDataPath);
    }

    public async Task<List<ManufacturerSeedDto>> ReadManufacturersAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "manufacturers.yaml");
        return await ReadYamlFileAsync<List<ManufacturerSeedDto>>(filePath, "manufacturers");
    }

    public async Task<List<PrinterModelSeedDto>> ReadPrinterModelsAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "printer-models.yaml");
        return await ReadYamlFileAsync<List<PrinterModelSeedDto>>(filePath, "printer models");
    }

    public async Task<List<FilamentTypeSeedDto>> ReadFilamentTypesAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "filament-types.yaml");
        return await ReadYamlFileAsync<List<FilamentTypeSeedDto>>(filePath, "filament types");
    }

    public async Task<List<HotendModelSeedDto>> ReadHotendsAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "components", "hotends.yaml");
        return await ReadYamlFileAsync<List<HotendModelSeedDto>>(filePath, "hotends");
    }

    public async Task<List<ExtruderModelSeedDto>> ReadExtrudersAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "components", "extruders.yaml");
        return await ReadYamlFileAsync<List<ExtruderModelSeedDto>>(filePath, "extruders");
    }

    public async Task<List<ToolheadModelSeedDto>> ReadToolheadsAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "components", "toolheads.yaml");
        return await ReadYamlFileAsync<List<ToolheadModelSeedDto>>(filePath, "toolheads");
    }

    public async Task<List<NozzleModelSeedDto>> ReadNozzlesAsync()
    {
        string filePath = Path.Combine(_seedDataPath, "components", "nozzles.yaml");
        return await ReadYamlFileAsync<List<NozzleModelSeedDto>>(filePath, "nozzles");
    }

    private async Task<T> ReadYamlFileAsync<T>(string filePath, string dataType)
        where T : new()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("[SeedData] YAML file not found: {FilePath}. Using empty dataset for {DataType}", filePath, dataType);
                return new T();
            }

            _logger.LogInformation("[SeedData] Reading {DataType} from {FilePath}", dataType, filePath);

            string yaml = await File.ReadAllTextAsync(filePath);

            if (string.IsNullOrWhiteSpace(yaml))
            {
                _logger.LogWarning("[SeedData] YAML file is empty: {FilePath}. Using empty dataset for {DataType}", filePath, dataType);
                return new T();
            }

            T? data = _yamlDeserializer.Deserialize<T>(yaml);

            if (object.Equals(data, default(T)))
            {
                _logger.LogWarning("[SeedData] Failed to deserialize YAML from {FilePath}. Using empty dataset for {DataType}", filePath, dataType);
                return new T();
            }

            _logger.LogInformation("[SeedData] Successfully loaded {DataType} from {FilePath}", dataType, filePath);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SeedData] Error reading YAML file for {DataType}: {Message}", dataType, ex.Message);
            throw new InvalidOperationException($"Failed to read {dataType} from YAML file: {filePath}", ex);
        }
    }
}
