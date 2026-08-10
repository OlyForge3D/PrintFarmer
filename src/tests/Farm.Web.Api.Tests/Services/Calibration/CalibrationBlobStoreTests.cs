using System.Text;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Tests.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Tests.Services.Calibration;

public sealed class CalibrationBlobStoreTests
{
    [Fact]
    public async Task PutAsync_WithSpoofedContent_ReturnsMagicValidationFailure()
    {
        CalibrationBlobStore store = CreateStore();
        CalibrationBlobWriteRequest request = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "photo.png",
            "image/png");
        await using MemoryStream content = new(Encoding.UTF8.GetBytes("not an image"));

        Func<Task> operation = () => store.PutAsync(request, content, CancellationToken.None);

        CalibrationBlobValidationException exception =
            (await operation.Should().ThrowAsync<CalibrationBlobValidationException>()).Which;
        _ = exception.Code.Should().Be("photo_magic_invalid");
    }

    [Fact]
    public async Task ExistsAsync_WithTraversalKey_RejectsStorageEscape()
    {
        CalibrationBlobStore store = CreateStore();

        Func<Task> operation = async () =>
            _ = await store.ExistsAsync("../outside.png", CancellationToken.None);

        _ = await operation.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static CalibrationBlobStore CreateStore() =>
        new(
            Options.Create(new CalibrationBlobStorageOptions
            {
                RootPath = Path.Join(Path.GetTempPath(), $"calibration-blobs-{Guid.NewGuid():N}"),
                MaxBytes = 1024,
                MaxWidth = 128,
                MaxHeight = 128,
                MaxPixels = 16_384,
            }),
            new TestFileSystem());
}
