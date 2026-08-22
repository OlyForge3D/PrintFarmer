using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Regression coverage for issue #1826's <see cref="NozzleInterfaceExportJsonConverter"/> fix.
/// <see cref="System.Text.Json.JsonSerializer.Deserialize{T}(string, JsonSerializerOptions?)"/>
/// always reads from a contiguous in-memory string, so <c>Utf8JsonReader.HasValueSequence</c>
/// is guaranteed false there and it cannot exercise the multi-segment path that a large
/// ASP.NET Core <c>[FromBody]</c> payload read off a <c>PipeReader</c> can produce. This test
/// forces that scenario directly against a manually constructed multi-segment
/// <see cref="ReadOnlySequence{T}"/> to prove the converter falls back to
/// <c>ValueSequence</c> instead of throwing when the numeric token itself straddles a
/// buffer-segment boundary.
/// </summary>
public class NozzleInterfaceExportJsonConverterTests
{
    [Fact]
    public void Read_OverflowingNumericTokenSplitAcrossBufferSegments_ReturnsRawTextInsteadOfThrowing()
    {
        const string NumberText = "99999999999999999999"; // overflows Int32
        string json = $"{{\"NozzleInterface\":{NumberText}}}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        // Split the buffer in the middle of the numeric token so the reader must materialize
        // the token's bytes from two segments (HasValueSequence == true for that token).
        int tokenStart = json.IndexOf(NumberText, StringComparison.Ordinal);
        int splitIndex = tokenStart + (NumberText.Length / 2);

        var firstSegment = new BufferSegment(bytes.AsMemory(0, splitIndex));
        BufferSegment secondSegment = firstSegment.Append(bytes.AsMemory(splitIndex));
        var sequence = new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, secondSegment.Memory.Length);

        var reader = new Utf8JsonReader(sequence);
        reader.Read(); // StartObject
        reader.Read(); // PropertyName
        reader.Read(); // Number value under test
        reader.TokenType.Should().Be(JsonTokenType.Number);
        reader.HasValueSequence.Should().BeTrue("the split forces the number token itself to straddle segments");

        var converter = new NozzleInterfaceExportJsonConverter();
        string? result = converter.Read(ref reader, typeof(string), new JsonSerializerOptions());

        result.Should().Be(NumberText, "the raw numeric text must be preserved (not thrown, not null) so import validation can reject the row");
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
