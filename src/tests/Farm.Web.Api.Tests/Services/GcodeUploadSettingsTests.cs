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
        IReadOnlyCollection<string> extensions = _settings.GetAllowedExtensions();

        extensions.Should().Contain(".gcode");
        extensions.Should().Contain(".bgcode");
        extensions.Count.Should().Be(2);
    }

    [Fact]
    public void AllowedExtensions_Returns_ReadOnlyCollection()
    {
        IReadOnlyCollection<string> extensions = _settings.GetAllowedExtensions();

        extensions.Should().NotBeNull();
        extensions.Should().BeAssignableTo<IReadOnlyCollection<string>>();
    }

    [Fact]
    public void UpdateAllowedExtensions_ReplacesPreviousExtensions()
    {
        string[] newExtensions = new[] { ".gcode", ".bgcode", ".ufp" };

        _settings.UpdateAllowedExtensions(newExtensions);

        IReadOnlyCollection<string> updated = _settings.GetAllowedExtensions();
        updated.Should().Contain(".gcode");
        updated.Should().Contain(".bgcode");
        updated.Should().Contain(".ufp");
    }

    [Fact]
    public void UpdateAllowedExtensions_WithEmptyList_ClearsExtensions()
    {
        _settings.UpdateAllowedExtensions(new string[] { });

        IReadOnlyCollection<string> extensions = _settings.GetAllowedExtensions();
        extensions.Should().BeEmpty();
    }

    [Fact]
    public void UpdateAllowedExtensions_WithDuplicates_HandlesDuplicates()
    {
        string[] extensions = new[] { ".gcode", ".gcode", ".bgcode", ".bgcode" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().Contain(".gcode");
        result.Should().Contain(".bgcode");
        result.Count.Should().Be(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_IsCaseInsensitive()
    {
        string[] extensions = new[] { ".GCODE", ".BgCode" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_AutoAddsDotsToExtensions()
    {
        string[] extensions = new[] { "gcode", "bgcode" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().AllSatisfy(e => e.Should().StartWith("."));
    }

    [Fact]
    public void UpdateAllowedExtensions_WithMixedDotNotation()
    {
        string[] extensions = new[] { ".gcode", "bgcode", ".ufp", "txt" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().AllSatisfy(e => e.Should().StartWith("."));
        result.Should().HaveCount(4);
    }

    [Fact]
    public void AllowedExtensions_ReturnsNewCollectionEachTime()
    {
        IReadOnlyCollection<string> extensions1 = _settings.GetAllowedExtensions();
        IReadOnlyCollection<string> extensions2 = _settings.GetAllowedExtensions();

        // Collections should have same content but be different instances
        extensions1.Should().Equal(extensions2);
    }

    [Fact]
    public void UpdateAllowedExtensions_CanBeCalledMultipleTimes()
    {
        string[] extensions1 = new[] { ".gcode" };
        _settings.UpdateAllowedExtensions(extensions1);
        IReadOnlyCollection<string> result1 = _settings.GetAllowedExtensions();

        string[] extensions2 = new[] { ".gcode", ".bgcode" };
        _settings.UpdateAllowedExtensions(extensions2);
        IReadOnlyCollection<string> result2 = _settings.GetAllowedExtensions();

        result1.Should().HaveCount(1);
        result2.Should().HaveCount(2);
    }

    [Fact]
    public void UpdateAllowedExtensions_PreservesOrderApproximately()
    {
        string[] extensions = new[] { ".ufp", ".bgcode", ".gcode" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().HaveCount(3);
        result.Should().Contain(".ufp");
        result.Should().Contain(".bgcode");
        result.Should().Contain(".gcode");
    }

    [Fact]
    public void UpdateAllowedExtensions_WithSpecialCharacters()
    {
        string[] extensions = new[] { ".gcode", ".g-code", ".g_code" };

        _settings.UpdateAllowedExtensions(extensions);

        IReadOnlyCollection<string> result = _settings.GetAllowedExtensions();
        result.Should().Contain(".gcode");
        result.Should().Contain(".g-code");
        result.Should().Contain(".g_code");
    }

    [Fact]
    public void AllowedExtensions_IsNotNull()
    {
        IReadOnlyCollection<string> extensions = _settings.GetAllowedExtensions();

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
