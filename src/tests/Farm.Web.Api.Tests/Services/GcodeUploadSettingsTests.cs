using Farm.Infrastructure.Services;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.Gcode;
using FluentAssertions;
using Moq;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Tests for PersistedGcodeUploadSettingsAdapter which bridges IGcodeUploadSettings
/// to the persisted GcodeUploadSettings via ISettingsService.
/// </summary>
public class PersistedGcodeUploadSettingsAdapterTests
{
    private readonly Mock<ISettingsService> _mockSettingsService;
    private readonly PersistedGcodeUploadSettingsAdapter _adapter;

    public PersistedGcodeUploadSettingsAdapterTests()
    {
        _mockSettingsService = new Mock<ISettingsService>();
        _adapter = new PersistedGcodeUploadSettingsAdapter(_mockSettingsService.Object);
    }

    [Fact]
    public void GetAllowedExtensions_ReturnsExtensionsFromSettingsService()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode", ".bgcode", ".ufp"]
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(settings);

        // Act
        IReadOnlyCollection<string> extensions = _adapter.GetAllowedExtensions();

        // Assert
        extensions.Should().HaveCount(3);
        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
        extensions.Should().Contain(".ufp");
    }

    [Fact]
    public void GetAllowedExtensions_WhenSettingsServiceReturnsNull_ReturnsDefaultExtensions()
    {
        // Arrange - when settings service returns null, adapter uses defaults
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns((GcodeUploadSettings?)null!);

        // Act
        IReadOnlyCollection<string> extensions = _adapter.GetAllowedExtensions();

        // Assert - defaults to [".gcode"]
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".gcode");
    }

    [Fact]
    public void GetAllowedExtensions_WhenExtensionsNull_ReturnsDefaultExtensions()
    {
        // Arrange - when AllowedExtensions is null, the GcodeUploadSettings normalizes to defaults
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = null!
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(settings);

        // Act
        IReadOnlyCollection<string> extensions = _adapter.GetAllowedExtensions();

        // Assert - defaults to [".gcode"]
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".gcode");
    }

    [Fact]
    public void GetAllowedExtensions_ReturnsReadOnlyCollection()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode"]
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(settings);

        // Act
        IReadOnlyCollection<string> extensions = _adapter.GetAllowedExtensions();

        // Assert
        extensions.Should().BeAssignableTo<IReadOnlyCollection<string>>();
    }

    [Fact]
    public void UpdateAllowedExtensions_SavesViaSettingsService()
    {
        // Arrange
        string[] newExtensions = [".gcode", ".bgcode"];
        var existingSettings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode"]
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(existingSettings);
        _mockSettingsService
            .Setup(s => s.Save(It.IsAny<GcodeUploadSettings>()))
            .Verifiable();

        // Act
        _adapter.UpdateAllowedExtensions(newExtensions);

        // Assert
        _mockSettingsService.Verify(s => s.Save(It.Is<GcodeUploadSettings>(
            settings => settings.AllowedExtensions.Contains(".gcode") &&
                        settings.AllowedExtensions.Contains(".bgcode"))), Times.Once);
    }
}

/// <summary>
/// Tests for the GcodeUploadSettings normalization logic that ensures
/// extensions are properly formatted and deduplicated.
/// </summary>
public class GcodeUploadSettingsNormalizationTests
{
    [Fact]
    public void AllowedExtensions_NormalizesExtensions_ToLowercase()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".GCODE", ".BgCode", ".UFP"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().AllSatisfy(e => e.Should().Be(e.ToLowerInvariant()));
    }

    [Fact]
    public void AllowedExtensions_AutoAddsLeadingDot()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = ["gcode", "bgcode", "ufp"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().AllSatisfy(e => e.Should().StartWith("."));
        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
        extensions.Should().Contain(".ufp");
    }

    [Fact]
    public void AllowedExtensions_RemovesDuplicates()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode", ".GCODE", ".GCode", ".bgcode", ".BGCODE"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().HaveCount(2);
        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
    }

    [Fact]
    public void AllowedExtensions_TrimsWhitespace()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = ["  .gcode  ", " bgcode ", "  .ufp"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().AllSatisfy(e => e.Should().NotStartWith(" ").And.NotEndWith(" "));
        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
        extensions.Should().Contain(".ufp");
    }

    [Fact]
    public void AllowedExtensions_WithEmptyList_ReturnsDefaultExtensions()
    {
        // Arrange - when setting empty list, the normalization returns defaults
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = []
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert - defaults to [".gcode"]
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".gcode");
    }

    [Fact]
    public void AllowedExtensions_WithMixedDotNotation_NormalizesAll()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode", "bgcode", ".UFP", "txt"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().AllSatisfy(e => e.Should().StartWith("."));
        extensions.Should().HaveCount(4);
    }

    [Fact]
    public void AllowedExtensions_WithNull_ReturnsDefaultExtensions()
    {
        // Arrange - when setting null, the normalization returns defaults
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = null!
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert - defaults to [".gcode"]
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".gcode");
    }

    [Fact]
    public void AllowedExtensions_FiltersOutEmptyStrings()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            AllowedExtensions = [".gcode", "", "  ", ".bgcode"]
        };

        // Act
        IList<string> extensions = settings.AllowedExtensions;

        // Assert
        extensions.Should().HaveCount(2);
        extensions.Should().NotContain("");
        extensions.Should().NotContain(".");
    }
}
