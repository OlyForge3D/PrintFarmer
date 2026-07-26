using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class ArtifactUploadStreamingTests
{
    [Fact]
    public async Task OpenArtifactFileStream_ExistingFile_ReturnsAsyncReadableStream()
    {
        string tempFile = Path.GetTempFileName();
        byte[] expectedBytes = Enumerable.Range(0, 100_000)
            .Select(index => (byte)(index % 251))
            .ToArray();

        try
        {
            await File.WriteAllBytesAsync(tempFile, expectedBytes);

            await using FileStream stream = HttpJobPollerService.OpenArtifactFileStream(tempFile);

            stream.IsAsync.Should().BeTrue();
            stream.Length.Should().Be(expectedBytes.Length);

            using MemoryStream uploadedBytes = new MemoryStream();
            await stream.CopyToAsync(uploadedBytes);
            uploadedBytes.ToArray().Should().Equal(expectedBytes);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
