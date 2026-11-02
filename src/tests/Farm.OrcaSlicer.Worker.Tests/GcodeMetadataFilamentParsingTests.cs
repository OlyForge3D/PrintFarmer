using System.Text;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Filament usage must come from the slicer footer, not from a file-size heuristic: a 24 MB
/// G-code whose footer says 20.66 g was previously reported as 485 g.
/// </summary>
public sealed class GcodeMetadataFilamentParsingTests : IDisposable
{
    private const string OrcaFooter =
        "; filament used [mm] = 6926.99\n" +
        "; filament used [cm3] = 16.66\n" +
        "; filament used [g] = 20.66\n" +
        "; filament cost = 0.41\n" +
        "; total filament used [g] = 20.66\n";

    private readonly string _directory =
        Path.Join(Path.GetTempPath(), $"printfarmer-gcode-filament-{Guid.NewGuid():N}");

    public GcodeMetadataFilamentParsingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task OrcaFooter_ReportsGramsFromFooter_NotFileSize()
    {
        // ~5 MB of moves: the old fallback would have reported ~100 g.
        var builder = new StringBuilder();
        for (int i = 0; i < 200_000; i++)
        {
            builder.Append("G1 X10.123 Y20.456 E0.0321\n");
        }

        builder.Append(OrcaFooter);

        GcodeMetadataSnapshot result = await ExtractAsync(builder.ToString());

        result.FilamentUsageGrams.Should().BeApproximately(20.66, 1e-9);
    }

    [Fact]
    public async Task MultiExtruderFooter_SumsPerExtruderValues_WhenNoTotalLine()
    {
        GcodeMetadataSnapshot result = await ExtractAsync("; filament used [g] = 1.50, 2.25\r\n");

        result.FilamentUsageGrams.Should().BeApproximately(3.75, 1e-9);
    }

    [Fact]
    public async Task TotalLine_TakesPrecedenceOverPerExtruderLine()
    {
        GcodeMetadataSnapshot result = await ExtractAsync(
            "; total filament used [g] = 9.00\n; filament used [g] = 1.50, 2.25\n");

        result.FilamentUsageGrams.Should().BeApproximately(9.0, 1e-9);
    }

    [Theory]
    [InlineData("; filament used = 10.5g\n", 10.5)]
    [InlineData("; filament used [g] = 12.5g\n", 12.5)]
    public async Task LegacyGramForms_AreStillParsed(string content, double expected)
    {
        GcodeMetadataSnapshot result = await ExtractAsync(content);

        result.FilamentUsageGrams.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public async Task LegacyMillimetreOnlyFooter_UsesHistoricalApproximation()
    {
        GcodeMetadataSnapshot result = await ExtractAsync("; filament used = 8000mm\n");

        result.FilamentUsageGrams.Should().BeApproximately(8000 * OrcaSlicingPipelineService.LegacyFilamentGramsPerMm, 1e-9);
        result.FilamentUsageGrams.Should().BeApproximately(20.0, 1e-9);
    }

    [Theory]
    [InlineData("; filament used = 8000mm\n; filament used [g] = 12.5\n")]
    [InlineData("; filament used [g] = 12.5\n; filament used = 8000mm\n")]
    [InlineData("; filament used = 12.5g\n; filament used = 8000mm\n")]
    public async Task ExplicitGrams_TakePrecedenceOverLegacyMillimetres(string content)
    {
        GcodeMetadataSnapshot result = await ExtractAsync(content);

        result.FilamentUsageGrams.Should().BeApproximately(12.5, 1e-9);
    }

    [Fact]
    public async Task MillimetreAndVolumeLines_AreNotMisreadAsGrams()
    {
        GcodeMetadataSnapshot result = await ExtractAsync(
            "; filament used [mm] = 6926.99\n; filament used [cm3] = 16.66\n");

        // No gram figure available: fall back to the existing minimum estimate, not 6926.99 or 16.66.
        result.FilamentUsageGrams.Should().Be(5.0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<GcodeMetadataSnapshot> ExtractAsync(string content)
    {
        string path = Path.Join(_directory, $"{Guid.NewGuid():N}.gcode");
        await File.WriteAllTextAsync(path, content);
        OrcaSlicingPipelineService.GcodeMetadata metadata =
            await OrcaSlicingPipelineService.ExtractGcodeMetadataAsync(path, CancellationToken.None);
        return new GcodeMetadataSnapshot(metadata.FilamentUsageGrams);
    }

    private sealed record GcodeMetadataSnapshot(double FilamentUsageGrams);
}
