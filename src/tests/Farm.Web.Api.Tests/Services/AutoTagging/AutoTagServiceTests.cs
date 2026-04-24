using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoTagging;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.AutoTagging;

/// <summary>
/// Unit tests for AutoTagService static helper methods:
/// material extraction, color family mapping, and RGB-to-HSL conversion.
/// </summary>
public class AutoTagServiceTests
{
    // =========================================================================
    // ExtractMaterial tests
    // =========================================================================

    [Theory]
    [InlineData("PLA", "PLA")]
    [InlineData("pla", "PLA")]
    [InlineData("PETG", "PETG")]
    [InlineData("ABS", "ABS")]
    [InlineData("ASA", "ASA")]
    [InlineData("TPU", "TPU")]
    [InlineData("Nylon", "Nylon")]
    [InlineData("PC", "PC")]
    [InlineData("PVA", "PVA")]
    [InlineData("HIPS", "HIPS")]
    public void ExtractMaterial_RequiredMaterialType_ReturnsNormalized(string materialType, string expected)
    {
        var job = new PrintJob { RequiredMaterialType = materialType };

        string? result = AutoTagService.ExtractMaterial(job);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("PolyTerra PLA Charcoal Black", "PLA")]
    [InlineData("Prusament PETG Jet Black", "PETG")]
    [InlineData("Hatchbox ABS True White", "ABS")]
    [InlineData("Generic ASA Red", "ASA")]
    [InlineData("NinjaTek NinjaFlex TPU", "TPU")]
    [InlineData("Polymaker PA-CF", "PA-CF")]
    [InlineData("Bambu Lab PLA+ Matte Red", "PLA+")]
    public void ExtractMaterial_FilamentName_ParsesMaterial(string filamentName, string expected)
    {
        var job = new PrintJob { FilamentName = filamentName };

        string? result = AutoTagService.ExtractMaterial(job);

        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractMaterial_RequiredMaterialTypePreferred_OverFilamentName()
    {
        var job = new PrintJob
        {
            RequiredMaterialType = "PETG",
            FilamentName = "PolyTerra PLA Charcoal Black"
        };

        string? result = AutoTagService.ExtractMaterial(job);

        result.Should().Be("PETG");
    }

    [Fact]
    public void ExtractMaterial_NoData_ReturnsNull()
    {
        var job = new PrintJob();

        string? result = AutoTagService.ExtractMaterial(job);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Unknown Filament")]
    [InlineData("My Custom Material")]
    [InlineData("DISPLAY CABLE")]  // Should not match PLA inside "DISPLAY"
    public void ExtractMaterial_UnknownFilament_ReturnsNull(string filamentName)
    {
        var job = new PrintJob { FilamentName = filamentName };

        string? result = AutoTagService.ExtractMaterial(job);

        result.Should().BeNull();
    }

    // =========================================================================
    // HexToColorFamily tests
    // =========================================================================

    [Theory]
    [InlineData("#000000", "Black")]
    [InlineData("#1A1A1A", "Black")]
    [InlineData("#FFFFFF", "White")]
    [InlineData("#F5F5F5", "White")]
    [InlineData("#808080", "Gray")]
    [InlineData("#A0A0A0", "Gray")]
    public void HexToColorFamily_Grayscale_ReturnsCorrectFamily(string hex, string expected)
    {
        var result = AutoTagService.HexToColorFamily(hex);

        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("#FF0000", "Red")]
    [InlineData("#CC0000", "Red")]
    [InlineData("#FF8C00", "Orange")]
    [InlineData("#FFA500", "Orange")]
    [InlineData("#FFD700", "Yellow")]
    [InlineData("#FFFF00", "Yellow")]
    [InlineData("#00FF00", "Green")]
    [InlineData("#008000", "Green")]
    [InlineData("#0000FF", "Blue")]
    [InlineData("#000080", "Blue")]
    [InlineData("#800080", "Purple")]
    [InlineData("#9B30FF", "Purple")]
    [InlineData("#FF69B4", "Pink")]
    [InlineData("#FF1493", "Pink")]
    public void HexToColorFamily_ChromaticColors_ReturnsCorrectFamily(string hex, string expected)
    {
        var result = AutoTagService.HexToColorFamily(hex);

        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("#8B4513", "Brown")]
    [InlineData("#A0522D", "Brown")]
    public void HexToColorFamily_Brown_ReturnsCorrectFamily(string hex, string expected)
    {
        var result = AutoTagService.HexToColorFamily(hex);

        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ZZZZZZ")]
    public void HexToColorFamily_InvalidHex_ReturnsNull(string hex)
    {
        var result = AutoTagService.HexToColorFamily(hex);

        result.Should().BeNull();
    }

    [Fact]
    public void HexToColorFamily_WithHash_Works()
    {
        var result = AutoTagService.HexToColorFamily("#FF0000");

        result.Should().NotBeNull();
        result!.Value.Name.Should().Be("Red");
    }

    [Fact]
    public void HexToColorFamily_WithoutHash_Works()
    {
        var result = AutoTagService.HexToColorFamily("FF0000");

        result.Should().NotBeNull();
        result!.Value.Name.Should().Be("Red");
    }

    // =========================================================================
    // RgbToHsl tests
    // =========================================================================

    [Fact]
    public void RgbToHsl_Black_ReturnsZeroLightness()
    {
        (double h, double s, double l) = AutoTagService.RgbToHsl(0, 0, 0);

        l.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void RgbToHsl_White_ReturnsFullLightness()
    {
        (double h, double s, double l) = AutoTagService.RgbToHsl(255, 255, 255);

        l.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void RgbToHsl_PureRed_ReturnsZeroHue()
    {
        (double h, double s, double l) = AutoTagService.RgbToHsl(255, 0, 0);

        h.Should().BeApproximately(0, 1);
        s.Should().BeApproximately(1.0, 0.01);
        l.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void RgbToHsl_PureGreen_Returns120Hue()
    {
        (double h, double s, double l) = AutoTagService.RgbToHsl(0, 255, 0);

        h.Should().BeApproximately(120, 1);
    }

    [Fact]
    public void RgbToHsl_PureBlue_Returns240Hue()
    {
        (double h, double s, double l) = AutoTagService.RgbToHsl(0, 0, 255);

        h.Should().BeApproximately(240, 1);
    }
}
