using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.ProfileParsing.Tests;

/// <summary>
/// Unit tests for <see cref="OrcaRawValueParser"/>, lifted verbatim from
/// <c>orcaslicer-worker</c>'s <c>OrcaProfilesService</c> (#1615 PR-2). Verifies the generic
/// scalar/array/string normalization behavior is preserved exactly &#8212; these are the same
/// semantics the worker already depends on, now shared rather than duplicated.
/// </summary>
public sealed class OrcaRawValueParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("260", 260)]
    [InlineData("\"260\"", 260)]
    [InlineData("[\"260\"]", 260)]
    [InlineData("[260]", 260)]
    public void ParseIntValue_HandlesNumberStringAndSingleElementArrayForms(string json, int expected)
    {
        int? result = OrcaRawValueParser.ParseIntValue(Parse(json));

        _ = result.Should().Be(expected);
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    public void ParseIntValue_WithUnparsableInput_ReturnsNull(string json)
    {
        int? result = OrcaRawValueParser.ParseIntValue(Parse(json));

        _ = result.Should().BeNull();
    }

    [Theory]
    [InlineData("0.2", 0.2)]
    [InlineData("\"0.2\"", 0.2)]
    [InlineData("[\"0.2\"]", 0.2)]
    [InlineData("[0.2]", 0.2)]
    public void ParseDoubleValue_HandlesNumberStringAndSingleElementArrayForms(string json, double expected)
    {
        double? result = OrcaRawValueParser.ParseDoubleValue(Parse(json));

        _ = result.Should().Be(expected);
    }

    [Fact]
    public void ParseDoubleValue_WithEmptyArray_ReturnsNull()
    {
        double? result = OrcaRawValueParser.ParseDoubleValue(Parse("[]"));

        _ = result.Should().BeNull();
    }

    [Theory]
    [InlineData("\"PLA\"", "PLA")]
    [InlineData("[\"PLA\"]", "PLA")]
    public void ParseStringValue_HandlesStringAndSingleElementArrayForms(string json, string expected)
    {
        string? result = OrcaRawValueParser.ParseStringValue(Parse(json));

        _ = result.Should().Be(expected);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ParseStringValue_WithNonStringInput_ReturnsNull(string json)
    {
        string? result = OrcaRawValueParser.ParseStringValue(Parse(json));

        _ = result.Should().BeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("\"0\"", false)]
    [InlineData("null", false)]
    [InlineData("42", false)]
    public void ParseBoolValue_MatchesWorkerSemanticsExactly(string json, bool expected)
    {
        // Deliberately non-nullable and defaults to false for anything unrecognized, matching the
        // worker's existing (not the deriver's more permissive nullable) semantics (#1615).
        bool result = OrcaRawValueParser.ParseBoolValue(Parse(json));

        _ = result.Should().Be(expected);
    }

    [Fact]
    public void ParseOptionalInt_WithMissingKey_ReturnsNull()
    {
        JsonElement root = Parse("""{"other": 1}""");

        int? result = OrcaRawValueParser.ParseOptionalInt(root, "missing");

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParseOptionalInt_WithPresentKey_ParsesValue()
    {
        JsonElement root = Parse("""{"value": ["260"]}""");

        int? result = OrcaRawValueParser.ParseOptionalInt(root, "value");

        _ = result.Should().Be(260);
    }

    [Fact]
    public void ParseOptionalDouble_WithPresentKey_ParsesValue()
    {
        JsonElement root = Parse("""{"value": ["0.4"]}""");

        double? result = OrcaRawValueParser.ParseOptionalDouble(root, "value");

        _ = result.Should().Be(0.4);
    }

    [Fact]
    public void ParseOptionalBool_WithPresentKey_ParsesValue()
    {
        JsonElement root = Parse("""{"value": "true"}""");

        bool? result = OrcaRawValueParser.ParseOptionalBool(root, "value");

        _ = result.Should().BeTrue();
    }

    [Fact]
    public void ParseOptionalString_WithPresentKey_ParsesValue()
    {
        JsonElement root = Parse("""{"value": ["PLA"]}""");

        string? result = OrcaRawValueParser.ParseOptionalString(root, "value");

        _ = result.Should().Be("PLA");
    }
}
