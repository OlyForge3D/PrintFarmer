using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Farm.Web.Api.Services.Calibration.Generation;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Deterministic builders for calibration generation tests.
/// </summary>
/// <remarks>
/// Every identifier, timestamp and profile document is fixed, so a golden hash produced by one test
/// run is reproducible by the next one on any machine.
/// </remarks>
internal static class CalibrationGenerationTestData
{
    public static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AttemptId = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OrchestrationId = new("33333333-3333-3333-3333-333333333333");
    public static readonly Guid PrinterId = new("44444444-4444-4444-4444-444444444444");
    public static readonly Guid SnapshotId = new("55555555-5555-5555-5555-555555555555");
    public static readonly Guid ToolheadId = new("66666666-6666-6666-6666-666666666666");
    public static readonly Guid MachineProfileId = new("77777777-7777-7777-7777-777777777777");
    public static readonly Guid ProcessProfileId = new("88888888-8888-8888-8888-888888888888");
    public static readonly Guid FilamentProfileId = new("99999999-9999-9999-9999-999999999999");
    public static readonly Guid FilamentProductId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid ModelId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid ObservationId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public static readonly DateTime CapturedAtUtc =
        new(2026, 7, 25, 8, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime NowUtc =
        new(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc);

    public const string ContainerDigest =
        "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c";

    public const string BinaryDigest =
        "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a";

    public static TimeProvider TimeProvider() => new FixedTimeProvider(NowUtc);

    public static CalibrationSpecificationCompiler Compiler() =>
        new(TimeProvider());

    /// <summary>Builds a complete, valid authoritative context.</summary>
    /// <param name="nozzleDiameter">Installed nozzle diameter, in millimetres.</param>
    /// <param name="toolheadIndex">Zero-based toolhead index.</param>
    /// <param name="directDrive">Whether the toolhead is direct drive.</param>
    /// <returns>A context that passes every fail-closed check.</returns>
    public static CalibrationGenerationContext Context(
        decimal nozzleDiameter = 0.4m,
        int toolheadIndex = 0,
        bool directDrive = true)
    {
        string machineJson = MachineProfileJson(nozzleDiameter);
        string processJson = ProcessProfileJson(nozzleDiameter);
        string filamentJson = FilamentProfileJson();

        return new CalibrationGenerationContext
        {
            ProjectId = ProjectId,
            AttemptId = AttemptId,
            OrchestrationId = OrchestrationId,
            PrinterId = PrinterId,
            PrinterConfigurationSnapshotId = SnapshotId,
            PrinterConfigurationRevision = 42,
            CurrentPrinterConfigurationRevision = 42,
            PrinterConfigurationSnapshotSha256 =
                "5a3f6b8c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a",
            SnapshotCapturedAtUtc = CapturedAtUtc,
            Compatibility = new CalibrationCompatibilityIdentity(
                "Klipper",
                "Klipper",
                "OrcaSlicer",
                "upstream",
                "2.3.1",
                ContainerDigest,
                BinaryDigest,
                "orca-json"),
            Firmware = new CalibrationFirmwareContext(
                "Klipper",
                "v0.12.0-321",
                "printer",
                "Klipper",
                true,
                CapturedAtUtc),
            Toolhead = new CalibrationToolheadContext(
                ToolheadId,
                toolheadIndex,
                nozzleDiameter,
                "brass",
                "brass",
                300,
                300,
                24m,
                directDrive),
            Bed = new CalibrationBedGeometry(
                235m,
                235m,
                250m,
                0m,
                0m,
                [],
                []),
            Limits = new CalibrationMachineLimits(120, false, null, 300, 500, 10000, 12000),
            Filament = new CalibrationFilamentContext(
                FilamentProductId,
                "PLA",
                "PF-PLA-001",
                "PrintFarmer",
                1.75m,
                220,
                60,
                null,
                1.0m,
                18m,
                null,
                null),
            Process = new CalibrationProcessContext(
                decimal.Round(nozzleDiameter / 2m, 3),
                decimal.Round(nozzleDiameter / 2m, 3),
                decimal.Round(nozzleDiameter * 1.125m, 3),
                120,
                40,
                300,
                8000,
                0.03m,
                0.8m,
                40),
            Profiles = new CalibrationProfileTriplet(
                Profile(MachineProfileId, "machine", "PF Machine", machineJson),
                Profile(ProcessProfileId, "process", "PF Process", processJson),
                Profile(FilamentProfileId, "filament", "PF Filament", filamentJson)),
            Generator = CalibrationGeneratorIdentity.Current,
            OperationId = "op-0000000000000001",
        };
    }

    public static CalibrationExactProfile Profile(
        Guid id,
        string kind,
        string name,
        string exactJson) =>
        new(
            id,
            kind,
            name,
            "1",
            exactJson,
            CalibrationCanonicalJson.ComputeTextSha256(exactJson));

    public static string MachineProfileJson(decimal nozzleDiameter) =>
        "{\"name\":\"PF Machine\",\"printer_technology\":\"FFF\",\"nozzle_diameter\":[\"" +
        nozzleDiameter.ToString("0.##", CultureInfo.InvariantCulture) +
        "\"],\"printable_area\":[\"0x0\",\"235x0\",\"235x235\",\"0x235\"],\"retraction_length\":[\"0.8\"]}";

    public static string ProcessProfileJson(decimal nozzleDiameter) =>
        "{\"name\":\"PF Process\",\"layer_height\":\"0.2\",\"line_width\":\"" +
        (nozzleDiameter * 1.125m).ToString("0.###", CultureInfo.InvariantCulture) +
        "\",\"wall_loops\":\"2\"}";

    public static string FilamentProfileJson() =>
        "{\"name\":\"PF Filament\",\"filament_type\":[\"PLA\"],\"filament_flow_ratio\":[\"1\"]," +
        "\"nozzle_temperature\":[\"220\"],\"filament_max_volumetric_speed\":[\"18\"]}";

    /// <summary>Builds a deterministic binary STL cuboid.</summary>
    /// <param name="sizeX">X extent, in millimetres.</param>
    /// <param name="sizeY">Y extent, in millimetres.</param>
    /// <param name="sizeZ">Z extent, in millimetres.</param>
    /// <returns>Binary STL bytes.</returns>
    public static byte[] BinaryStlCuboid(float sizeX, float sizeY, float sizeZ)
    {
        (float X, float Y, float Z)[][] triangles =
        [
            [(0, 0, 0), (sizeX, 0, 0), (sizeX, sizeY, 0)],
            [(0, 0, 0), (sizeX, sizeY, 0), (0, sizeY, 0)],
            [(0, 0, sizeZ), (sizeX, 0, sizeZ), (sizeX, sizeY, sizeZ)],
            [(0, 0, sizeZ), (sizeX, sizeY, sizeZ), (0, sizeY, sizeZ)],
        ];

        byte[] content = new byte[84 + (50 * triangles.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.AsSpan(80, 4),
            (uint)triangles.Length);

        for (int index = 0; index < triangles.Length; index++)
        {
            int offset = 84 + (index * 50) + 12;
            foreach ((float X, float Y, float Z) vertex in triangles[index])
            {
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset, 4), vertex.X);
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset + 4, 4), vertex.Y);
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset + 8, 4), vertex.Z);
                offset += 12;
            }
        }

        return content;
    }

    /// <summary>Builds a minimal, well-formed 3MF package.</summary>
    /// <param name="unit">The declared model unit.</param>
    /// <param name="size">The cube edge length.</param>
    /// <returns>3MF package bytes.</returns>
    public static byte[] ThreeMfCube(string unit = "millimeter", decimal size = 20m)
    {
        string edge = size.ToString("0.###", CultureInfo.InvariantCulture);
        string model =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<model unit=\"" + unit + "\" xml:lang=\"en-US\" " +
            "xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
            "<resources><object id=\"1\" type=\"model\"><mesh><vertices>" +
            "<vertex x=\"0\" y=\"0\" z=\"0\"/>" +
            "<vertex x=\"" + edge + "\" y=\"0\" z=\"0\"/>" +
            "<vertex x=\"" + edge + "\" y=\"" + edge + "\" z=\"0\"/>" +
            "<vertex x=\"0\" y=\"" + edge + "\" z=\"" + edge + "\"/>" +
            "</vertices><triangles>" +
            "<triangle v1=\"0\" v2=\"1\" v3=\"2\"/>" +
            "<triangle v1=\"0\" v2=\"2\" v3=\"3\"/>" +
            "</triangles></mesh></object></resources>" +
            "<build><item objectid=\"1\" transform=\"1 0 0 0 1 0 0 0 1 0 0 0\"/></build></model>";

        return ZipPackage([("3D/3dmodel.model", Encoding.UTF8.GetBytes(model))]);
    }

    /// <summary>Builds a ZIP package from raw entries, without any safety filtering.</summary>
    /// <param name="entries">The entry names and payloads.</param>
    /// <returns>The archive bytes.</returns>
    public static byte[] ZipPackage(IReadOnlyList<(string Name, byte[] Content)> entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }
}

/// <summary>A clock that always reports the same instant, so freshness checks are reproducible.</summary>
internal sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
}

/// <summary>An in-memory content source for model validator tests.</summary>
internal sealed class FakeModelContentSource(
    Guid model3DId,
    byte[] content,
    string format,
    string? sha256 = null,
    string? safeFileName = "calibration-cube.stl",
    string? provenance = "imported") : ICalibrationModelContentSource
{
    public Guid Model3DId { get; } = model3DId;

    public string? Sha256 { get; } =
        sha256 ?? CalibrationCanonicalJson.ComputeBytesSha256(content);

    public string? Format { get; } = format;

    public string? SafeFileName { get; } = safeFileName;

    public string? Provenance { get; } = provenance;

    public Task<Stream> OpenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(content, writable: false));
}
