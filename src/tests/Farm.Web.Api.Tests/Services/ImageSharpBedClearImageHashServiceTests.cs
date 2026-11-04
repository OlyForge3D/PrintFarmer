using Farm.Web.Api.Services.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Tests.Services;

public sealed class ImageSharpBedClearImageHashServiceTests
{
    private readonly ImageSharpBedClearImageHashService _service = new();

    [Theory]
    [InlineData(0, "dark_or_saturated_image")]
    [InlineData(255, "dark_or_saturated_image")]
    [InlineData(100, "obstructed_or_low_detail_image")]
    public void TryCreateHash_WhenImageHasNoUsableDetail_ReturnsUnknownReason(byte intensity, string reason)
    {
        using var image = new Image<Rgb24>(100, 100, new Rgb24(intensity, intensity, intensity));

        bool usable = _service.TryCreateHash(Encode(image), out _, out string? reasonCode);

        usable.Should().BeFalse();
        reasonCode.Should().Be(reason);
    }

    [Fact]
    public void TryCreateHash_WhenImageIsInvalid_ReturnsFailureWithoutThrowing()
    {
        bool usable = _service.TryCreateHash(new byte[] { 1, 2, 3 }, out _, out string? reasonCode);

        usable.Should().BeFalse();
        reasonCode.Should().Be("unsupported_image_format");
    }

    [Fact]
    public void TryCreateHash_WhenImageIsTooSmall_RejectsDimensions()
    {
        using var image = new Image<Rgb24>(32, 32);

        _service.TryCreateHash(Encode(image), out _, out string? reasonCode).Should().BeFalse();
        reasonCode.Should().Be("invalid_image_dimensions");
    }

    [Fact]
    public void TryCreateHash_WhenOnlyMaskedBorderChanges_HashRemainsUnchanged()
    {
        using var image = new Image<Rgb24>(100, 100);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                byte intensity = (byte)(30 + (x * 2));
                image[x, y] = new Rgb24(intensity, intensity, intensity);
            }
        }

        _service.TryCreateHash(Encode(image), out ulong originalHash, out _).Should().BeTrue();
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (x < 10 || x >= 90 || y < 10 || y >= 90)
                {
                    image[x, y] = new Rgb24(255, 0, 0);
                }
            }
        }

        _service.TryCreateHash(Encode(image), out ulong changedHash, out _).Should().BeTrue();
        changedHash.Should().Be(originalHash);
    }

    private static byte[] Encode(Image<Rgb24> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
