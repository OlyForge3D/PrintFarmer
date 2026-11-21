using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class OrcaBedGcodeVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsMatchingCommandsAndFinalShutdown()
    {
        await VerifyAsync("""
            M140 S60
            PRINT_START BED=60
            ;LAYER_CHANGE
            G1 X1 E1
            ;LAYER_CHANGE
            M140 S55
            G1 X2 E2
            M140 S0
            """);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsRelativeRetractionAfterShutdown()
    {
        await VerifyAsync("M83\nM140 S60\nG1 E1\nM140 S0\nG1 E-3");
    }

    [Theory]
    [InlineData("PRINT_START")]
    [InlineData("PRINT_START\nG1 E1\nM140 S0")]
    public async Task VerifyAsync_AcceptsOpaqueFirmwareMacroWithMatchingMetadata(string commands)
    {
        await VerifyAsync(commands);
    }

    [Theory]
    [InlineData("M140 S45")]
    [InlineData("M140 S60\nPRINT_START BED=45")]
    [InlineData("M140 S60\nPRINT_START BED=unknown")]
    [InlineData("M140 S60\nPRINT_START BED")]
    [InlineData("M140 S60\nM190 Sbad")]
    [InlineData("M140 S60\nM140 S0\nG1 X1 E1")]
    [InlineData("M140 S60\nM140S0\nG1E1")]
    [InlineData("M140 S60\nM140 S0\nG2 X1 E1")]
    [InlineData("M83\nM140 S60\nM140 S0\nG1 E1")]
    [InlineData("M82\nM140 S60\nM140 S0\nG1 E-3")]
    [InlineData("M140 S60\n;LAYER_CHANGE\n;LAYER_CHANGE\nM140 S45")]
    public async Task VerifyAsync_RejectsMissingInvalidOrMismatchedCommands(string commands)
    {
        Func<Task> action = () => VerifyAsync(commands);
        _ = await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongBedMetadata()
    {
        Func<Task> action = () => VerifyAsync("M140 S60", bed: "Cool Plate");
        _ = await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task VerifyAsync_RejectsAmbiguousLaterLayerTemperatures()
    {
        Func<Task> action = () => VerifyAsync("M140 S60", normal: "55,65", filaments: "1,2", formula: "by_first_filament");
        _ = await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task VerifyAsync_RejectsDifferentFirstLayerVectorEntry()
    {
        Func<Task> action = () => VerifyAsync("M140 S60\nM190 S65", first: "60,65", filaments: "1,2");
        _ = await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task VerifyAsync_RejectsAmbiguousFirstLayerEvenWhenCommandsMatchExtruderZero()
    {
        Func<Task> action = () => VerifyAsync("M140 S60", first: "60,65", filaments: "1,2");
        _ = await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task VerifyAsync_AcceptsEqualTemperaturesAcrossFilaments()
    {
        await VerifyAsync("M140 S60\n;LAYER_CHANGE\n;LAYER_CHANGE\nM140 S55", first: "60,60", normal: "55,55", filaments: "1,2");
    }

    [Fact]
    public async Task VerifyAsync_UsesSelectedFilamentInsteadOfExtruderZeroMetadata()
    {
        await VerifyAsync("M140 S65\n;LAYER_CHANGE\n;LAYER_CHANGE\nM140 S70", first: "60,65", normal: "55,70", filaments: "2");
    }

    [Fact]
    public async Task VerifyAsync_UsesHighestNormalTemperatureOfUsedFilaments()
    {
        await VerifyAsync("M140 S60\n;LAYER_CHANGE\n;LAYER_CHANGE\nM140 S70", first: "60,60,80", normal: "55,70,90", filaments: "1,2");
    }

    [Fact]
    public async Task VerifyProjectBedOutputsAsync_ChecksEveryPlateAndRequiresSelectedPlate()
    {
        string directory = Path.Join(Path.GetTempPath(), $"bed-plates-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        try
        {
            string plate1 = Path.Join(directory, "plate_1.gcode");
            string plate2 = Path.Join(directory, "plate_2.gcode");
            await File.WriteAllTextAsync(plate1, Gcode("M140 S60", "High Temp Plate", "hot_plate", "60", "55"));
            await File.WriteAllTextAsync(plate2, Gcode("M140 S45", "Cool Plate", "cool_plate", "45", "45"));
            var settings = new OrcaProjectBedSettings("High Temp Plate", new Dictionary<int, string> { [2] = "Cool Plate" });

            string selected = await OrcaSlicingPipelineService.VerifyProjectBedOutputsAsync(
                directory, 0, settings, null, CancellationToken.None);
            _ = selected.Should().Be(plate1);

            await File.WriteAllTextAsync(plate2, Gcode("M140 S60", "Cool Plate", "cool_plate", "45", "45"));
            Func<Task> mismatch = () => OrcaSlicingPipelineService.VerifyProjectBedOutputsAsync(
                directory, 0, settings, null, CancellationToken.None);
            _ = await mismatch.Should().ThrowAsync<InvalidDataException>();

            File.Delete(plate2);
            Func<Task> missing = () => OrcaSlicingPipelineService.VerifyProjectBedOutputsAsync(
                directory, 1, settings, null, CancellationToken.None);
            _ = await missing.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyAsync(
        string commands,
        string bed = "High Temp Plate",
        string first = "60",
        string normal = "55",
        string filaments = "1",
        string formula = "by_highest_temp")
    {
        string path = Path.Join(Path.GetTempPath(), $"bed-gcode-{Guid.NewGuid():N}.gcode");
        try
        {
            await File.WriteAllTextAsync(path, Gcode(commands, bed, "hot_plate", first, normal, filaments, formula));
            await OrcaBedGcodeVerifier.VerifyAsync(path, "High Temp Plate", CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Gcode(string commands, string bed, string prefix, string first, string normal, string filaments = "1", string formula = "by_highest_temp") => $"""
        ; HEADER_BLOCK_START
        ; filament: {filaments}
        ; HEADER_BLOCK_END
        {commands}
        ; CONFIG_BLOCK_START
        ; curr_bed_type = {bed}
        ; bed_temperature_formula = {formula}
        ; first_layer_bed_temperature = {first.Split(',')[0]}
        ; {prefix}_temp_initial_layer = {first}
        ; {prefix}_temp = {normal}
        ; CONFIG_BLOCK_END
        """;
}
