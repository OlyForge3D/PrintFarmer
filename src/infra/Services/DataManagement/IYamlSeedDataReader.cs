using Farm.Infrastructure.Dtos.DataManagement;

namespace Farm.Infrastructure.Services.DataManagement;

/// <summary>
/// Service for reading and parsing YAML seed data files
/// </summary>
public interface IYamlSeedDataReader
{
    /// <summary>
    /// Read manufacturers from YAML file
    /// </summary>
    Task<List<ManufacturerSeedDto>> ReadManufacturersAsync();

    /// <summary>
    /// Read printer models from YAML file
    /// </summary>
    Task<List<PrinterModelSeedDto>> ReadPrinterModelsAsync();

    /// <summary>
    /// Read filament types from YAML file
    /// </summary>
    Task<List<FilamentTypeSeedDto>> ReadFilamentTypesAsync();

    /// <summary>
    /// Read hotend models from YAML file
    /// </summary>
    Task<List<HotendModelSeedDto>> ReadHotendsAsync();

    /// <summary>
    /// Read extruder models from YAML file
    /// </summary>
    Task<List<ExtruderModelSeedDto>> ReadExtrudersAsync();

    /// <summary>
    /// Read toolhead models from YAML file
    /// </summary>
    Task<List<ToolheadModelSeedDto>> ReadToolheadsAsync();

    /// <summary>
    /// Read nozzle models from YAML file
    /// </summary>
    Task<List<NozzleModelSeedDto>> ReadNozzlesAsync();

    /// <summary>
    /// Read global maintenance task catalog from YAML file
    /// </summary>
    Task<List<MaintenanceTaskSeedDto>> ReadMaintenanceTasksAsync();

    /// <summary>
    /// Read maintenance components (spare parts) from YAML file
    /// </summary>
    Task<List<MaintenanceComponentSeedDto>> ReadMaintenanceComponentsAsync();

    /// <summary>
    /// Read default maintenance plans from YAML file
    /// </summary>
    Task<List<MaintenancePlanSeedDto>> ReadMaintenancePlansAsync();
}
