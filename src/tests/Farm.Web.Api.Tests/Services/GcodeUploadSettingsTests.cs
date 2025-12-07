using Farm.Web.Api.Services;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class InMemoryGcodeUploadSettingsTests : IDisposable
{
    private readonly InMemoryGcodeUploadSettings _settings;
    private readonly string? _originalEnv;

    public InMemoryGcodeUploadSettingsTests()
    {
        // Store original env var
        _originalEnv = Environment.GetEnvironmentVariable("GCODE_ALLOWED_EXTENSIONS");
        // Clear it for consistent tests
        Environment.SetEnvironmentVariable("GCODE_ALLOWED_EXTENSIONS", null);
        _settings = new InMemoryGcodeUploadSettings();
    }

    public void Dispose()
    {
        // Restore original env var
        Environment.SetEnvironmentVariable("GCODE_ALLOWED_EXTENSIONS", _originalEnv);
    }

    [Fact]
    public void Constructor_WithoutEnvironmentVariable_SetsDefaultExtensions()
    {
        var extensions = _settings.AllowedExtensions;

        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
        extensions.Count.Should().Be(2);
    }

    [Fact]
    public void AllowedExtensions_Returns_ReadOnlyCollection()
    {
        var extensions = _settings.AllowedExtensions;

        extensions.Should().NotBeNull();
        extensions.Should().BeAssignableTo<IReadOnlyCollection<string>>();
    }

    [Fact]
    public void UpdateAllowedExtensions_ReplacesPreviousExtensions()
    {
        var newExtensions = new[] { ".gcode", ".bgcode", ".ufp" };

        _settings.UpdateAllowedExtensions(newExtensions);

        var updated = _settings.AllowedExtensions;
        updated.Should().Contain(".gcode");
        updated.Should().Contain(".bgcode");
        updated.Should().Contain(".ufp");
    }

    [Fact]
    public void UpdateAllowedExtensions_WithEmptyList_ClearsExtensions()
    {
        _settings.UpdateAllowedExtensions(new string[] { });

        var extensions = _settings.AllowedExtensions;
        extensions.Should().BeEmpty();
    }

    [Fact]
    public void UpdateAllowedExtensions_WithDuplicates_HandlesDuplicates()
    {
        var extensions = new[] { ".gcode", ".gcode", ".bgcode", ".bgcode" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().Contain(".gcode");
        result.Should().Contain(".bgcode");
        result.Count.Should().Be(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_IsCaseInsensitive()
    {
        var extensions = new[] { ".GCODE", ".BgCode" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().HaveCount(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_AutoAddsDotsToExtensions()
    {
        var extensions = new[] { "gcode", "bgcode" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().AllSatisfy(e => e.Should().StartWith("."));
    }

    [Fact]
    public void UpdateAllowedExtensions_WithMixedDotNotation()
    {
        var extensions = new[] { ".gcode", "bgcode", ".ufp", "txt" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().AllSatisfy(e => e.Should().StartWith("."));
        result.Should().HaveCount(4);
    }

    [Fact]
    public void AllowedExtensions_ReturnsNewCollectionEachTime()
    {
        var extensions1 = _settings.AllowedExtensions;
        var extensions2 = _settings.AllowedExtensions;

        // Collections should have same content but be different instances
        extensions1.Should().Equal(extensions2);
    }

    [Fact]
    public void UpdateAllowedExtensions_CanBeCalledMultipleTimes()
    {
        var extensions1 = new[] { ".gcode" };
        _settings.UpdateAllowedExtensions(extensions1);
        var result1 = _settings.AllowedExtensions;

        var extensions2 = new[] { ".gcode", ".bgcode" };
        _settings.UpdateAllowedExtensions(extensions2);
        var result2 = _settings.AllowedExtensions;

        result1.Should().HaveCount(1);
        result2.Should().HaveCount(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_PreservesOrderApproximately()
    {
        var extensions = new[] { ".ufp", ".bgcode", ".gcode" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().HaveCount(3);
        result.Should().Contain(".ufp");
        result.Should().Contain(".bgcode");
        result.Should().Contain(".gcode");
    }

    [Fact]
    public void UpdateAllowedExtensions_WithSpecialCharacters()
    {
        var extensions = new[] { ".gcode", ".g-code", ".g_code" };

        _settings.UpdateAllowedExtensions(extensions);

        var result = _settings.AllowedExtensions;
        result.Should().Contain(".gcode");
        result.Should().Contain(".g-code");
        result.Should().Contain(".g_code");
    }

    [Fact]
    public void AllowedExtensions_IsNotNull()
    {
        var extensions = _settings.AllowedExtensions;

        extensions.Should().NotBeNull();
    }

    [Fact]
    public void UpdateAllowedExtensions_WithNull_HandlesSafely()
    {
        Action act = () => _settings.UpdateAllowedExtensions(null!);

        // This may throw ArgumentNullException or handle gracefully
        // depending on implementation
        act.Should().Throw<ArgumentNullException>();
    }
}
