using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Direct unit coverage for <see cref="ProcessOverrideSettingsValidation"/> (issue #2229), beyond
/// what <see cref="SliceJobControllerPrintSettingsValidationTests"/> exercises through the
/// controller. Focuses on the numeric-coercion edge cases raised in review: leading-decimal
/// strings (e.g. <c>".5"</c>), malformed <c>overrides</c> shapes, and the legacy typed
/// <see cref="ProcessProfileDto"/> overload used by the deprecated
/// <c>POST /api/slicer/jobs</c>/<c>slice(-model)</c> routes.
/// </summary>
public sealed class ProcessOverrideSettingsValidationTests
{
    [Theory]
    [InlineData(".5")]
    [InlineData("0.5")]
    [InlineData("+5")]
    public void TryValidate_LeadingDecimalOrSignedPositiveString_IsAccepted(string encodedValue)
    {
        string json = $"{{\"overrides\":{{\"sparse_infill_density\":\"{encodedValue}\"}}}}";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeTrue();
        _ = error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_LeadingDecimalNegativeString_IsRejected()
    {
        const string json = """{"overrides":{"sparse_infill_density":"-.5"}}""";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain("cannot be negative");
    }

    [Fact]
    public void TryValidate_NonNumericString_IsRejectedWithNonNegativeMessage()
    {
        const string json = """{"overrides":{"wall_loops":"not-a-number"}}""";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain("must be a non-negative number");
    }

    [Fact]
    public void TryValidate_PositiveInfinityString_IsRejected()
    {
        // "1e999" overflows double parsing to PositiveInfinity, which is not itself negative but
        // is just as invalid a print setting as NaN — both must be rejected, not just negatives.
        const string json = """{"overrides":{"wall_loops":"1e999"}}""";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain("must be a non-negative number");
    }

    [Fact]
    public void TryValidate_OverridesAsArray_IsRejected()
    {
        const string json = """{"overrides":[1,2,3]}""";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain("must be a JSON object");
    }

    [Fact]
    public void TryValidate_OverridesAsString_IsRejected()
    {
        const string json = """{"overrides":"not-an-object"}""";

        bool ok = ProcessOverrideSettingsValidation.TryValidate(json, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain("must be a JSON object");
    }

    [Fact]
    public void TryValidate_MalformedJson_IsTreatedAsValid()
    {
        bool ok = ProcessOverrideSettingsValidation.TryValidate("{not json", out string? error);

        _ = ok.Should().BeTrue();
        _ = error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_NullProcessProfile_IsAccepted()
    {
        bool ok = ProcessOverrideSettingsValidation.TryValidate((ProcessProfileDto?)null, out string? error);

        _ = ok.Should().BeTrue();
        _ = error.Should().BeNull();
    }

    [Theory]
    [InlineData(-1, 20, 4, 3, "wallCount")]
    [InlineData(3, -20, 4, 3, "infillPercentage")]
    [InlineData(3, 20, -4, 3, "topLayers")]
    [InlineData(3, 20, 4, -3, "bottomLayers")]
    public void TryValidate_ProcessProfileWithNegativeField_IsRejected(
        int wallCount, int infillPercentage, int topLayers, int bottomLayers, string expectedFieldMention)
    {
        var profile = new ProcessProfileDto
        {
            WallCount = wallCount,
            InfillPercentage = infillPercentage,
            TopLayers = topLayers,
            BottomLayers = bottomLayers,
        };

        bool ok = ProcessOverrideSettingsValidation.TryValidate(profile, out string? error);

        _ = ok.Should().BeFalse();
        _ = error.Should().Contain(expectedFieldMention);
    }

    [Fact]
    public void TryValidate_ProcessProfileWithZeroFields_IsAccepted()
    {
        var profile = new ProcessProfileDto
        {
            WallCount = 0,
            InfillPercentage = 0,
            TopLayers = 0,
            BottomLayers = 0,
        };

        bool ok = ProcessOverrideSettingsValidation.TryValidate(profile, out string? error);

        _ = ok.Should().BeTrue();
        _ = error.Should().BeNull();
    }
}
