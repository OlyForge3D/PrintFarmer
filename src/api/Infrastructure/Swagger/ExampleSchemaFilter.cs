using Farm.Web.Shared;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Farm.Web.Api.Infrastructure.Swagger;

/// <summary>
/// Adds example objects to OpenAPI schemas for improved Swagger UI clarity.
/// </summary>
public sealed class ExampleSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var t = context.Type;

        if (t == typeof(CreatePrinterDto))
        {
            schema.Example = new OpenApiObject
            {
                ["name"] = new OpenApiString("Voron Trident #1"),
                ["serverUrl"] = new OpenApiString("http://voron1.local"),
                ["notes"] = new OpenApiString("Primary ABS printer"),
                ["manufacturerId"] = new OpenApiString("00000000-0000-0000-0000-000000000000"),
                ["modelId"] = new OpenApiString("00000000-0000-0000-0000-000000000000"),
                ["backend"] = new OpenApiString("Moonraker"),
                ["apiKey"] = new OpenApiString("(optional)")
            };
        }
        else if (t == typeof(StartGcodeHarvestDto))
        {
            schema.Example = new OpenApiObject
            {
                ["printerId"] = new OpenApiString("11111111-2222-3333-4444-555555555555"),
                ["includeSubdirectories"] = new OpenApiBoolean(true),
                ["maxFileSizeBytes"] = new OpenApiLong(50 * 1024 * 1024),
                ["modifiedAfter"] = new OpenApiString(DateTime.UtcNow.AddDays(-7).ToString("o")),
                ["fileExtensions"] = new OpenApiArray { new OpenApiString("gcode"), new OpenApiString("gco") },
                ["minFileSizeBytes"] = new OpenApiLong(5 * 1024),
                ["duplicateHandling"] = new OpenApiString("rename")
            };
        }
        else if (t == typeof(ImportSelectedGcodeFilesDto))
        {
            schema.Example = new OpenApiObject
            {
                ["harvestOperationId"] = new OpenApiString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                ["selectedFileIds"] = new OpenApiArray
                {
                    new OpenApiString("11111111-1111-1111-1111-111111111111"),
                    new OpenApiString("22222222-2222-2222-2222-222222222222")
                },
                ["addToLibraryOnly"] = new OpenApiBoolean(true),
                ["autoDetectCapabilities"] = new OpenApiBoolean(true),
                ["defaultTags"] = new OpenApiArray { new OpenApiString("harvested"), new OpenApiString("quality-profile-A") }
            };
        }
        else if (t == typeof(CreatePrintJobDto))
        {
            schema.Example = new OpenApiObject
            {
                ["name"] = new OpenApiString("Calibration Cube"),
                ["priority"] = new OpenApiInteger(1),
                ["gcodeFileId"] = new OpenApiString("99999999-8888-7777-6666-555555555555"),
                ["hotendTemperature"] = new OpenApiDouble(215),
                ["bedTemperature"] = new OpenApiDouble(60),
                ["requiredCapabilities"] = new OpenApiArray { new OpenApiString("0.4mm-nozzle") },
                ["autoAssign"] = new OpenApiBoolean(true)
            };
        }
        else if (t == typeof(UpdatePrintJobStatusDto))
        {
            schema.Example = new OpenApiObject
            {
                ["status"] = new OpenApiString("Printing"),
                ["priority"] = new OpenApiString("High"),
                ["assignedPrinterId"] = new OpenApiString("22222222-3333-4444-5555-666666666666"),
                ["actualFilamentUsage"] = new OpenApiDouble(12.4)
            };
        }
        else if (t == typeof(SlicerProfileDto))
        {
            schema.Example = new OpenApiObject
            {
                ["layerHeight"] = new OpenApiDouble(0.2),
                ["infillPercentage"] = new OpenApiInteger(20),
                ["printSpeed"] = new OpenApiInteger(50),
                ["nozzleTemperature"] = new OpenApiInteger(210),
                ["bedTemperature"] = new OpenApiInteger(60),
                ["supports"] = new OpenApiBoolean(false),
                ["material"] = new OpenApiString("PLA"),
                ["quality"] = new OpenApiString("standard")
            };
        }
        else if (t == typeof(LoginRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["username"] = new OpenApiString("admin"),
                ["password"] = new OpenApiString("P@ssw0rd!")
            };
        }
        else if (t == typeof(RegisterRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["username"] = new OpenApiString("jdoe"),
                ["email"] = new OpenApiString("jdoe@example.com"),
                ["password"] = new OpenApiString("StrongPass!123"),
                ["firstName"] = new OpenApiString("John"),
                ["lastName"] = new OpenApiString("Doe")
            };
        }
        // ---- Response DTO examples (select high-value read models) ----
        else if (t == typeof(PrinterDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["name"] = new OpenApiString("Voron Trident #1"),
                ["serverUrl"] = new OpenApiString("http://voron1.local"),
                ["notes"] = new OpenApiString("Primary ABS printer"),
                ["isOnline"] = new OpenApiBoolean(true),
                ["state"] = new OpenApiString("printing"),
                ["manufacturerName"] = new OpenApiString("Voron"),
                ["modelName"] = new OpenApiString("Trident"),
                ["progress"] = new OpenApiDouble(42.3),
                ["jobName"] = new OpenApiString("calibration_cube.gcode"),
                ["cameraStreamUrl"] = new OpenApiString("http://cam.local/stream"),
                ["x"] = new OpenApiDouble(125.4),
                ["y"] = new OpenApiDouble(110.2),
                ["z"] = new OpenApiDouble(5.6),
                ["hotendTemp"] = new OpenApiDouble(245),
                ["bedTemp"] = new OpenApiDouble(100),
                ["backend"] = new OpenApiString("Moonraker"),
                ["ipAddress"] = new OpenApiString("192.168.1.50")
            };
        }
        else if (t == typeof(GcodeFileDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                ["originalFileName"] = new OpenApiString("benchy.gcode"),
                ["displayName"] = new OpenApiString("Benchy"),
                ["fileSizeBytes"] = new OpenApiLong(3_456_789),
                ["uploadedAt"] = new OpenApiString(DateTime.UtcNow.AddDays(-1).ToString("o")),
                ["source"] = new OpenApiString("Upload"),
                ["tags"] = new OpenApiArray { new OpenApiString("test"), new OpenApiString("calibration") },
                ["estimatedPrintTimeMinutes"] = new OpenApiDouble(95.2),
                ["estimatedFilamentLengthMm"] = new OpenApiDouble(13450),
                ["hasThumbnail"] = new OpenApiBoolean(true)
            };
        }
        else if (t == typeof(GcodeHarvestOperationDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("22222222-3333-4444-5555-666666666666"),
                ["printerId"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["printerName"] = new OpenApiString("Voron Trident #1"),
                ["startedAt"] = new OpenApiString(DateTime.UtcNow.AddMinutes(-3).ToString("o")),
                ["completedAt"] = new OpenApiString(DateTime.UtcNow.ToString("o")),
                ["status"] = new OpenApiString("Completed"),
                ["filesFound"] = new OpenApiInteger(15),
                ["filesAdded"] = new OpenApiInteger(10),
                ["filesSkipped"] = new OpenApiInteger(3),
                ["filesErrored"] = new OpenApiInteger(2),
                ["totalBytesProcessed"] = new OpenApiLong(24_567_123),
                ["includeSubdirectories"] = new OpenApiBoolean(true),
                ["maxFileSizeBytes"] = new OpenApiLong(104857600)
            };
        }
        else if (t == typeof(DiscoveredGcodeFileDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("33333333-4444-5555-6666-777777777777"),
                ["harvestOperationId"] = new OpenApiString("22222222-3333-4444-5555-666666666666"),
                ["printerPath"] = new OpenApiString("gcodes/benchy.gcode"),
                ["fileName"] = new OpenApiString("benchy.gcode"),
                ["fileSizeBytes"] = new OpenApiLong(3_456_789),
                ["modifiedAt"] = new OpenApiString(DateTime.UtcNow.AddDays(-2).ToString("o")),
                ["isSelected"] = new OpenApiBoolean(false),
                ["alreadyInLibrary"] = new OpenApiBoolean(false)
            };
        }
        else if (t == typeof(PrinterCapabilitiesDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("44444444-5555-6666-7777-888888888888"),
                ["printerId"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["printerName"] = new OpenApiString("Voron Trident #1"),
                ["nozzleDiameter"] = new OpenApiDouble(0.4),
                ["supportedMaterials"] = new OpenApiArray { new OpenApiString("ABS"), new OpenApiString("ASA") },
                ["maxBuildVolumeX"] = new OpenApiDouble(300),
                ["maxBuildVolumeY"] = new OpenApiDouble(300),
                ["maxBuildVolumeZ"] = new OpenApiDouble(250),
                ["hasHeatedBed"] = new OpenApiBoolean(true),
                ["hasEnclosure"] = new OpenApiBoolean(true),
                ["multiMaterial"] = new OpenApiBoolean(false),
                ["numberOfExtruders"] = new OpenApiInteger(1),
                ["isAvailable"] = new OpenApiBoolean(true),
                ["lastUpdated"] = new OpenApiString(DateTime.UtcNow.ToString("o"))
            };
        }
        else if (t == typeof(PrintJobDto))
        {
            schema.Example = new OpenApiObject
            {
                ["id"] = new OpenApiString("55555555-6666-7777-8888-999999999999"),
                ["name"] = new OpenApiString("Calibration Cube"),
                ["priority"] = new OpenApiInteger(1),
                ["status"] = new OpenApiString("Printing"),
                ["queuedAt"] = new OpenApiString(DateTime.UtcNow.AddMinutes(-10).ToString("o")),
                ["gcodeFileId"] = new OpenApiString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                ["gcodeFileName"] = new OpenApiString("benchy.gcode"),
                ["assignedPrinterId"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["assignedPrinterName"] = new OpenApiString("Voron Trident #1"),
                ["hotendTemperature"] = new OpenApiDouble(215),
                ["bedTemperature"] = new OpenApiDouble(60),
                ["progressPercentage"] = new OpenApiDouble(42.3),
                ["autoAssign"] = new OpenApiBoolean(true)
            };
        }
        else if (t == typeof(GcodeMetadataDto))
        {
            schema.Example = new OpenApiObject
            {
                ["slicerName"] = new OpenApiString("PrusaSlicer"),
                ["slicerVersion"] = new OpenApiString("2.7.0"),
                ["printTimeMinutes"] = new OpenApiDouble(95.2),
                ["filamentLengthMm"] = new OpenApiDouble(13450),
                ["nozzleDiameter"] = new OpenApiDouble(0.4),
                ["material"] = new OpenApiString("PLA"),
                ["layerHeight"] = new OpenApiDouble(0.2),
                ["infillPercentage"] = new OpenApiString("20%"),
                ["buildPlateX"] = new OpenApiDouble(220),
                ["buildPlateY"] = new OpenApiDouble(220),
                ["buildPlateZ"] = new OpenApiDouble(250)
            };
        }
    }
}