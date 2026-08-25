using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Seeds a calibration-ready printer plus its machine/process/filament triple, mirroring the
/// production shape the candidate and context evaluators require.
/// </summary>
internal static class CalibrationPrinterSeeder
{
    /// <summary>Identifiers of a seeded calibration-ready printer.</summary>
    internal sealed record SeededPrinter(
        Guid PrinterId,
        Guid MachineProfileId,
        Guid ProcessProfileId,
        Guid FilamentProfileId);

    /// <summary>
    /// Writes a calibration-ready printer into the core database and its profiles into the slicer store.
    /// </summary>
    /// <param name="services">The API host's service provider.</param>
    /// <param name="profilesPublic">Whether the seeded profiles are visible to everyone.</param>
    /// <param name="profileOwnerUserId">Owner recorded on the profiles.</param>
    /// <param name="deriveHardwareFromMachineProfile">
    /// When <see langword="true"/>, leaves every #1614 AC-2 derivable <c>Calibration*</c>/
    /// <c>Toolhead</c> column null and seeds a fully-specified machine profile
    /// <c>RawJson</c> so calibration context generation must source those fields from the resolved
    /// machine profile — the split-deployment counterpart of the in-process derivation unit
    /// test, proving parity across both deployment topologies (test plan item 6).
    /// </param>
    /// <returns>The seeded identifiers.</returns>
    public static async Task<SeededPrinter> SeedAsync(
        IServiceProvider services,
        bool profilesPublic = true,
        Guid? profileOwnerUserId = null,
        bool deriveHardwareFromMachineProfile = false)
    {
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid machineProfileId = Guid.NewGuid();
        Guid processProfileId = Guid.NewGuid();
        Guid filamentProfileId = Guid.NewGuid();
        DateTime nowUtc = DateTime.UtcNow;

        using (IServiceScope scope = services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = db.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = $"Calibration manufacturer {manufacturerId}",
            });
            _ = db.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = $"Calibration model {modelId}",
            });
            _ = db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Split calibration printer",
                ServerUrl = "http://10.0.0.42",
                BackendPort = 7125,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                IsEnabled = true,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                FirmwareDetectionSource = FirmwareDetectionSource.Printer,
                FirmwareVersion = "v0.12.0",
                FirmwareDetectionVersion = "printer-info-v1",
                FirmwareDetectionConfidence = 1m,
                FirmwareDetectedAtUtc = nowUtc,
                FirmwareIdentityVerified = true,
                BackendVersion = "v0.9.3",
                BackendApiVersion = "v1",
                MaxBuildVolumeX = deriveHardwareFromMachineProfile ? null : 250,
                MaxBuildVolumeY = deriveHardwareFromMachineProfile ? null : 250,
                MaxBuildVolumeZ = deriveHardwareFromMachineProfile ? null : 250,
                BedOriginX = deriveHardwareFromMachineProfile ? null : 0,
                BedOriginY = deriveHardwareFromMachineProfile ? null : 0,
                PrintablePolygonJson = deriveHardwareFromMachineProfile
                    ? null
                    : """[{"x":0,"y":0},{"x":250,"y":0},{"x":250,"y":250},{"x":0,"y":250}]""",
                ExcludedRegionsJson = "[]",
                MaxPrintSpeed = 300,
                MaxTravelSpeed = deriveHardwareFromMachineProfile ? null : 500,
                MaxAcceleration = deriveHardwareFromMachineProfile ? null : 10000,
                MaxTravelAcceleration = 12000,
                CalibrationHasHeatedBed = deriveHardwareFromMachineProfile ? null : true,
                MaxBedTemp = 120,
                CalibrationHasEnclosure = false,
                HasHeatedChamber = deriveHardwareFromMachineProfile ? null : false,
                ActiveToolheadIndex = 0,
                SupportsPressureAdvance = true,
                SupportsFirmwareRetraction = true,
                CalibrationSlicerEngine = CalibrationContractConstants.SlicerEngine,
                CalibrationSlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                CalibrationSlicerVersion = CalibrationContractConstants.SlicerVersion,
                ApiKey = "printer-api-key",
                Username = "printer-user",
                Password = "printer-password",
                Toolheads =
                [
                    new Toolhead
                    {
                        Id = Guid.NewGuid(),
                        PrinterId = printerId,
                        Name = "T0",
                        Index = 0,
                        IsPrimary = true,
                        ToolheadType = ToolheadType.Physical,
                        OffsetX = 0,
                        OffsetY = 0,
                        OffsetZ = 0,
                        NozzleDiameter = deriveHardwareFromMachineProfile ? null : 0.4,
                        NozzleType = deriveHardwareFromMachineProfile ? null : NozzleType.Brass,
                        NozzleMaterial = "brass",
                        NozzleMaxTemperature = deriveHardwareFromMachineProfile ? null : 300,
                        NozzleIsHardened = false,
                        HotendMaxTemperature = deriveHardwareFromMachineProfile ? null : 300,
                        MaxVolumetricFlow = 15,
                        DriveType = "direct",
                        IsDirectDrive = true,
                        ExtruderGearRatio = "50:10",
                        SupportedMaterials = ["PLA", "PETG"],
                    },
                ],
            });
            _ = db.Spools.Add(new Spool
            {
                Id = Guid.NewGuid(),
                Material = "PLA",
                ColorHex = "#ff0000",
                WeightGrams = 750,
                InUse = true,
                AssignedPrinterId = printerId,
            });
            _ = await db.SaveChangesAsync();
        }

        string machineJson = deriveHardwareFromMachineProfile
            ? $$"""
                {
                    "gcode_flavor": "klipper",
                    "printer_variant": "{{machineProfileId}}",
                    "printable_area": ["0x0", "250x0", "250x250", "0x250"],
                    "printable_height": 250,
                    "machine_max_acceleration_x": [10000],
                    "machine_max_speed_x": [500],
                    "has_heated_bed": true,
                    "has_heated_chamber": false,
                    "nozzle_diameter": [0.4],
                    "nozzle_type": "brass",
                    "max_hotend_temp": [300],
                    "printer_type": "corexy"
                }
                """
            : $$"""{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"printer_variant":"{{machineProfileId}}"}""";
        string processJson =
            $$"""{"layer_height":0.2,"infill_density":20,"process_variant":"{{processProfileId}}"}""";
        string filamentJson =
            $$"""{"filament_max_volumetric_speed":12,"filament_variant":"{{filamentProfileId}}"}""";
        using (IServiceScope scope = services.CreateScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            _ = db.MachineProfiles.Add(new MachineProfile
            {
                Id = machineProfileId,
                Name = "Split Machine",
                Manufacturer = "Test",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = machineJson,
                Hash = Sha256(machineJson),
                IsSystem = true,
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = db.ProcessProfiles.Add(new ProcessProfile
            {
                Id = processProfileId,
                Name = "Split Process",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                SpecificPrinterId = printerId,
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = processJson,
                Hash = Sha256(processJson),
                CompatiblePrinters = "Split Machine",
                IsSystem = true,
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = db.FilamentProfiles.Add(new FilamentProfile
            {
                Id = filamentProfileId,
                Name = "Split PLA",
                Material = "PLA",
                Manufacturer = "Test",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                NozzleTemperature = 210,
                BedTemperature = 60,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = filamentJson,
                Hash = Sha256(filamentJson),
                CompatiblePrinters = "Split Machine",
                IsSystem = true,
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = await db.SaveChangesAsync();
        }

        IPrinterStatusCacheWriter statusWriter =
            services.GetRequiredService<IPrinterStatusCacheWriter>();
        statusWriter.UpdateStatus(new PrinterStatusDto(
            printerId,
            IsOnline: true,
            State: "idle",
            HotendTemp: 205,
            BedTemp: 60,
            HotendTarget: 210,
            BedTarget: 60));

        return new SeededPrinter(printerId, machineProfileId, processProfileId, filamentProfileId);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
