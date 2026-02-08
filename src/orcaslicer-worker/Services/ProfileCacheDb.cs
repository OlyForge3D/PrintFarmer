using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// SQLite-backed cache for OrcaSlicer profiles.
/// Provides fast indexed queries - no in-memory caching needed.
/// </summary>
public class ProfileCacheDb : IDisposable
{
    private readonly string _dbPath;
    private readonly IUnifiedLoggingService _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public ProfileCacheDb(IUnifiedLoggingService logger, string? dbPath = null)
    {
        _logger = logger;
        _dbPath = dbPath ?? Path.Combine(Path.GetTempPath(), "orcaslicer-profiles.db");
    }

    /// <summary>
    /// Gets the path to the SQLite database file.
    /// </summary>
    public string DatabasePath => _dbPath;

    /// <summary>
    /// Opens the database connection and creates schema if needed.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared");
        await _connection.OpenAsync(cancellationToken);

        // Enable WAL mode for better concurrent read performance
        using SqliteCommand walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=10000;";
        await walCmd.ExecuteNonQueryAsync(cancellationToken);

        await CreateSchemaAsync(cancellationToken);
    }

    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Database not initialized");
        }

        string schema = """
            CREATE TABLE IF NOT EXISTS cache_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS machine_profiles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                manufacturer TEXT NOT NULL,
                printer_model TEXT,
                nozzle_diameter REAL,
                inherits TEXT,
                json_data TEXT NOT NULL,
                UNIQUE(name, manufacturer)
            );
            
            CREATE TABLE IF NOT EXISTS filament_profiles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                manufacturer TEXT NOT NULL,
                material TEXT NOT NULL,
                nozzle_temperature INTEGER,
                bed_temperature INTEGER,
                compatible_printers TEXT,
                json_data TEXT NOT NULL,
                UNIQUE(name, manufacturer)
            );
            
            CREATE TABLE IF NOT EXISTS process_profiles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                manufacturer TEXT NOT NULL,
                compatible_printers TEXT,
                json_data TEXT NOT NULL,
                UNIQUE(name, manufacturer)
            );
            
            CREATE INDEX IF NOT EXISTS idx_machine_manufacturer ON machine_profiles(manufacturer);
            CREATE INDEX IF NOT EXISTS idx_machine_model ON machine_profiles(printer_model);
            CREATE INDEX IF NOT EXISTS idx_filament_manufacturer ON filament_profiles(manufacturer);
            CREATE INDEX IF NOT EXISTS idx_filament_material ON filament_profiles(material);
            CREATE INDEX IF NOT EXISTS idx_process_manufacturer ON process_profiles(manufacturer);
            """;

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Checks if the cache is populated and valid.
    /// </summary>
    public async Task<bool> IsCacheValidAsync(string profilesHash, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return false;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM cache_metadata WHERE key = 'profiles_hash'";
        object? result = await cmd.ExecuteScalarAsync(cancellationToken);

        if (result == null || result.ToString() != profilesHash)
        {
            return false;
        }

        // Also check if we have data
        cmd.CommandText = "SELECT COUNT(*) FROM machine_profiles";
        long count = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return count > 0;
    }

    /// <summary>
    /// Clears all cached profiles.
    /// </summary>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM machine_profiles;
            DELETE FROM filament_profiles;
            DELETE FROM process_profiles;
            DELETE FROM cache_metadata;
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Stores a batch of machine profiles.
    /// </summary>
    public async Task StoreMachineProfilesAsync(IEnumerable<MachineProfileDto> profiles, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Database not initialized");
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO machine_profiles (name, manufacturer, printer_model, nozzle_diameter, inherits, json_data)
            VALUES ($name, $manufacturer, $model, $nozzle, $inherits, $json)
            """;

        SqliteParameter nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter manufacturerParam = cmd.Parameters.Add("$manufacturer", SqliteType.Text);
        SqliteParameter modelParam = cmd.Parameters.Add("$model", SqliteType.Text);
        SqliteParameter nozzleParam = cmd.Parameters.Add("$nozzle", SqliteType.Real);
        SqliteParameter inheritsParam = cmd.Parameters.Add("$inherits", SqliteType.Text);
        SqliteParameter jsonParam = cmd.Parameters.Add("$json", SqliteType.Text);

        foreach (MachineProfileDto profile in profiles)
        {
            nameParam.Value = profile.Name;
            manufacturerParam.Value = profile.Manufacturer ?? "Unknown";
            modelParam.Value = profile.PrinterModel ?? (object)DBNull.Value;
            nozzleParam.Value = profile.NozzleDiameter ?? (object)DBNull.Value;
            inheritsParam.Value = profile.Inherits ?? (object)DBNull.Value;
            jsonParam.Value = JsonSerializer.Serialize(profile);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Stores a batch of filament profiles.
    /// </summary>
    public async Task StoreFilamentProfilesAsync(IEnumerable<FilamentProfileDto> profiles, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Database not initialized");
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO filament_profiles (name, manufacturer, material, nozzle_temperature, bed_temperature, compatible_printers, json_data)
            VALUES ($name, $manufacturer, $material, $nozzleTemp, $bedTemp, $compatible, $json)
            """;

        SqliteParameter nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter manufacturerParam = cmd.Parameters.Add("$manufacturer", SqliteType.Text);
        SqliteParameter materialParam = cmd.Parameters.Add("$material", SqliteType.Text);
        SqliteParameter nozzleTempParam = cmd.Parameters.Add("$nozzleTemp", SqliteType.Integer);
        SqliteParameter bedTempParam = cmd.Parameters.Add("$bedTemp", SqliteType.Integer);
        SqliteParameter compatibleParam = cmd.Parameters.Add("$compatible", SqliteType.Text);
        SqliteParameter jsonParam = cmd.Parameters.Add("$json", SqliteType.Text);

        foreach (FilamentProfileDto profile in profiles)
        {
            nameParam.Value = profile.Name;
            manufacturerParam.Value = profile.Manufacturer ?? "Unknown";
            materialParam.Value = profile.Material ?? "Other";
            nozzleTempParam.Value = profile.NozzleTemperature;
            bedTempParam.Value = profile.BedTemperature;
            compatibleParam.Value = profile.CompatiblePrinters != null && profile.CompatiblePrinters.Count > 0 ? string.Join(",", profile.CompatiblePrinters) : (object)DBNull.Value;
            jsonParam.Value = JsonSerializer.Serialize(profile);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Stores a batch of process profiles.
    /// </summary>
    public async Task StoreProcessProfilesAsync(IEnumerable<ProcessProfileDto> profiles, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Database not initialized");
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO process_profiles (name, manufacturer, compatible_printers, json_data)
            VALUES ($name, $manufacturer, $compatible, $json)
            """;

        SqliteParameter nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter manufacturerParam = cmd.Parameters.Add("$manufacturer", SqliteType.Text);
        SqliteParameter compatibleParam = cmd.Parameters.Add("$compatible", SqliteType.Text);
        SqliteParameter jsonParam = cmd.Parameters.Add("$json", SqliteType.Text);

        foreach (ProcessProfileDto profile in profiles)
        {
            nameParam.Value = profile.Name;

            // ProcessProfileDto doesn't have Manufacturer - extract from name pattern or use Unknown
            manufacturerParam.Value = ExtractManufacturerFromName(profile.Name);
            compatibleParam.Value = profile.CompatiblePrinters != null && profile.CompatiblePrinters.Count > 0 ? string.Join(",", profile.CompatiblePrinters) : (object)DBNull.Value;
            jsonParam.Value = JsonSerializer.Serialize(profile);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Sets a metadata value for cache validation.
    /// </summary>
    public async Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Database not initialized");
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO cache_metadata (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Gets all machine profiles from SQLite (use sparingly - prefer filtered queries).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<MachineProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM machine_profiles";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets all filament profiles from SQLite (use sparingly - prefer filtered queries).
    /// </summary>
    public async Task<List<FilamentProfileDto>> GetFilamentProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<FilamentProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM filament_profiles";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            FilamentProfileDto? profile = JsonSerializer.Deserialize<FilamentProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets all process profiles from SQLite (use sparingly - prefer filtered queries).
    /// </summary>
    public async Task<List<ProcessProfileDto>> GetProcessProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<ProcessProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM process_profiles";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            ProcessProfileDto? profile = JsonSerializer.Deserialize<ProcessProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets machine profiles by manufacturer.
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByManufacturerAsync(string manufacturer, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<MachineProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM machine_profiles WHERE manufacturer = $manufacturer";
        cmd.Parameters.AddWithValue("$manufacturer", manufacturer);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets machine profiles by printer model only (uses indexed query).
    /// This is the simplest query - just match the printer_model field directly.
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByPrinterModelAsync(
        string printerModel,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<MachineProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT json_data FROM machine_profiles 
            WHERE printer_model = $printerModel COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$printerModel", printerModel);

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets machine profiles by manufacturer AND printer model (uses indexed query).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByModelAsync(
        string manufacturer,
        string printerModel,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<MachineProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT json_data FROM machine_profiles 
            WHERE manufacturer = $manufacturer 
            AND printer_model = $printerModel COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$manufacturer", manufacturer);
        cmd.Parameters.AddWithValue("$printerModel", printerModel);

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets distinct printer models for a manufacturer.
    /// </summary>
    public async Task<List<string>> GetPrinterModelsAsync(string manufacturer, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<string> models = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT printer_model FROM machine_profiles 
            WHERE manufacturer = $manufacturer 
            AND printer_model IS NOT NULL
            ORDER BY printer_model
            """;
        cmd.Parameters.AddWithValue("$manufacturer", manufacturer);

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            models.Add(reader.GetString(0));
        }

        return models;
    }

    /// <summary>
    /// Gets filament profiles by manufacturer.
    /// </summary>
    public async Task<List<FilamentProfileDto>> GetFilamentProfilesByManufacturerAsync(string manufacturer, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<FilamentProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM filament_profiles WHERE manufacturer = $manufacturer";
        cmd.Parameters.AddWithValue("$manufacturer", manufacturer);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            FilamentProfileDto? profile = JsonSerializer.Deserialize<FilamentProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets process profiles by manufacturer.
    /// </summary>
    public async Task<List<ProcessProfileDto>> GetProcessProfilesByManufacturerAsync(string manufacturer, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<ProcessProfileDto> profiles = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM process_profiles WHERE manufacturer = $manufacturer";
        cmd.Parameters.AddWithValue("$manufacturer", manufacturer);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string json = reader.GetString(0);
            ProcessProfileDto? profile = JsonSerializer.Deserialize<ProcessProfileDto>(json);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Gets distinct manufacturers.
    /// </summary>
    public async Task<List<string>> GetManufacturersAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<string> manufacturers = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT manufacturer FROM machine_profiles ORDER BY manufacturer";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            manufacturers.Add(reader.GetString(0));
        }

        return manufacturers;
    }

    /// <summary>
    /// Gets distinct materials for filaments.
    /// </summary>
    public async Task<List<string>> GetMaterialsAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return [];
        }

        List<string> materials = [];

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT material FROM filament_profiles ORDER BY material";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            materials.Add(reader.GetString(0));
        }

        return materials;
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public async Task<(int machineCount, int filamentCount, int processCount)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return (0, 0, 0);
        }

        using SqliteCommand cmd = _connection.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM machine_profiles";
        int machines = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

        cmd.CommandText = "SELECT COUNT(*) FROM filament_profiles";
        int filaments = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

        cmd.CommandText = "SELECT COUNT(*) FROM process_profiles";
        int processes = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

        return (machines, filaments, processes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Extracts manufacturer name from a profile name like "0.20mm Standard @Prusa" -> "Prusa".
    /// </summary>
    private static string ExtractManufacturerFromName(string name)
    {
        // OrcaSlicer profiles often use @ to indicate the printer/manufacturer
        // e.g., "0.20mm Standard @Prusa MK4", "0.16mm Optimal @Voron"
        int atIndex = name.IndexOf('@');
        if (atIndex > 0 && atIndex < name.Length - 1)
        {
            string afterAt = name[(atIndex + 1)..].Trim();

            // Take first word as manufacturer
            int spaceIndex = afterAt.IndexOf(' ');
            return spaceIndex > 0 ? afterAt[..spaceIndex] : afterAt;
        }

        return "Unknown";
    }
}
