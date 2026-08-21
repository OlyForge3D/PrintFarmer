using System;
using System.Reflection;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Direct unit tests for <c>SliceJobController.ParseLayoutDegradationReason</c>, the helper that
/// parses the persisted <c>SliceJob.LayoutDegradationReason</c> string column back into the typed
/// <see cref="LayoutDegradationReason"/> contract value (issue #1800 review finding).
/// </summary>
/// <remarks>
/// This guards against a bare numeric string (e.g. <c>"999"</c>) round-tripping as a
/// look-alike-valid enum value: <c>Enum.TryParse</c> alone accepts any parseable integer for the
/// underlying type regardless of whether it names a real member, so the helper must also check
/// <c>Enum.IsDefined</c>. The write-side guard in <c>CompleteAsync</c> should prevent an
/// out-of-domain value from ever reaching this column, but this covers the read path
/// independently in case of legacy or otherwise malformed data already stored.
/// </remarks>
public class SliceJobControllerLayoutDegradationParsingTests
{
    private static readonly MethodInfo ParseMethod = typeof(SliceJobController).GetMethod(
        "ParseLayoutDegradationReason",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseLayoutDegradationReason method not found via reflection.");

    private static LayoutDegradationReason? Parse(string? value) =>
        (LayoutDegradationReason?)ParseMethod.Invoke(null, [value]);

    [Theory(DisplayName = "Parses each recognized enum name")]
    [InlineData("LayoutNotEmbedded", LayoutDegradationReason.LayoutNotEmbedded)]
    [InlineData("SourcePlacementFallback", LayoutDegradationReason.SourcePlacementFallback)]
    [InlineData("layoutnotembedded", LayoutDegradationReason.LayoutNotEmbedded)]
    public void Parse_RecognizedName_ReturnsTypedValue(string value, LayoutDegradationReason expected)
    {
        _ = Parse(value).Should().Be(expected);
    }

    [Theory(DisplayName = "Returns null for unset, blank, or unrecognized values")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotARealReason")]
    public void Parse_UnsetOrUnrecognizedName_ReturnsNull(string? value)
    {
        _ = Parse(value).Should().BeNull();
    }

    [Theory(DisplayName = "Rejects a bare numeric string that names no real enum member")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("2")] // one past the last defined member; must not silently resolve
    public void Parse_OutOfDomainNumericString_ReturnsNull(string value)
    {
        _ = Parse(value).Should().BeNull();
    }
}
