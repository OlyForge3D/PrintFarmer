using System.Buffers.Binary;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Services;

public class PrinterFileImageValidatorTests
{
    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void TryValidate_Png_ReturnsActualDimensionsAndMime()
    {
        Assert.True(PrinterFileImageValidator.TryValidate(Png, out string? type, out int width, out int height));
        Assert.Equal("image/png", type);
        Assert.Equal(1, width);
        Assert.Equal(1, height);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(4096, 1024, true)]
    [InlineData(4097, 1, false)]
    [InlineData(2048, 2048, true)]
    [InlineData(2049, 2048, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    public void TryValidate_PngDimensions_EnforcesEdgeAndPixelBounds(int width, int height, bool valid)
    {
        byte[] image = Png;
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(20, 4), height);

        Assert.Equal(valid, PrinterFileImageValidator.TryValidate(image, out _, out int actualWidth, out int actualHeight));
        Assert.Equal(valid ? width : 0, actualWidth);
        Assert.Equal(valid ? height : 0, actualHeight);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(33)]
    [InlineData(60)]
    public void TryValidate_TruncatedPng_RejectsEvenValidSignature(int length) =>
        Assert.False(PrinterFileImageValidator.TryValidate(Png[..length], out _, out _, out _));

    [Fact]
    public void TryValidate_TooManyEncodedBytes_RejectsBeforeParsing()
    {
        byte[] image = new byte[PrinterFileEnrichmentLimits.MaximumImageBytes + 1];
        Png.CopyTo(image, 0);
        Assert.False(PrinterFileImageValidator.TryValidate(image, out _, out _, out _));
    }

    [Fact]
    public void TryValidate_SvgOrSignatureOnlyJpeg_RejectsUnsupportedPayloads()
    {
        Assert.False(PrinterFileImageValidator.TryValidate("<svg />"u8.ToArray(), out _, out _, out _));
        Assert.False(PrinterFileImageValidator.TryValidate([0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9], out _, out _, out _));
    }

    [Theory]
    [InlineData(640, 480, true)]
    [InlineData(4097, 1, false)]
    [InlineData(4096, 4096, false)]
    public void TryValidate_JpegDimensions_EnforcesRasterBounds(int width, int height, bool valid)
    {
        byte[] image =
        [
            0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0, 0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0, 0, 0xff, 0xd9,
        ];
        Assert.Equal(valid, PrinterFileImageValidator.TryValidate(image, out string? type, out int actualWidth, out int actualHeight));
        Assert.Equal(valid ? "image/jpeg" : null, type);
        Assert.Equal(valid ? width : 0, actualWidth);
        Assert.Equal(valid ? height : 0, actualHeight);
    }

    [Theory]
    [InlineData(16, 16, true)]
    [InlineData(4097, 1, false)]
    [InlineData(4096, 4096, false)]
    public void TryValidate_WebPDimensions_EnforcesRasterBounds(int width, int height, bool valid)
    {
        byte[] image = [.. "RIFF"u8, 22, 0, 0, 0, .. "WEBPVP8 "u8, 10, 0, 0, 0,
            0, 0, 0, 0x9d, 0x01, 0x2a, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)];
        Assert.Equal(valid, PrinterFileImageValidator.TryValidate(image, out string? type, out int actualWidth, out int actualHeight));
        Assert.Equal(valid ? "image/webp" : null, type);
        Assert.Equal(valid ? width : 0, actualWidth);
        Assert.Equal(valid ? height : 0, actualHeight);
    }
}
