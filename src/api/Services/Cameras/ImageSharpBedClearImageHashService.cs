using Farm.Infrastructure.Services.Cameras;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services.Cameras;

/// <summary>Decodes bounded camera frames and hashes the central bed region.</summary>
public sealed class ImageSharpBedClearImageHashService : IBedClearImageHashService
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;
    private const int MaximumImageBytes = 10 * 1024 * 1024;

    public bool TryCreateHash(
        ReadOnlyMemory<byte> imageBytes,
        out ulong hash,
        out string? reasonCode)
    {
        hash = 0;
        reasonCode = "invalid_image";
        if (imageBytes.IsEmpty || imageBytes.Length > MaximumImageBytes)
        {
            reasonCode = "invalid_image_size";
            return false;
        }

        try
        {
            ImageInfo info = Image.Identify(imageBytes.Span);
            if (info.Width < 64 || info.Height < 64 ||
                info.Width > 4096 || info.Height > 4096)
            {
                reasonCode = "invalid_image_dimensions";
                return false;
            }

            using Image<Rgb24> image = Image.Load<Rgb24>(
                new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 },
                imageBytes.Span);
            int left = image.Width / 10;
            int top = image.Height / 10;
            int width = image.Width - (2 * left);
            int height = image.Height - (2 * top);
            using Image<Rgb24> roi = image.Clone(context =>
                context.Crop(new Rectangle(left, top, width, height))
                    .Resize(HashWidth, HashHeight));

            Span<byte> grayscale = stackalloc byte[HashWidth * HashHeight];
            long totalLuminance = 0;
            int minimum = byte.MaxValue;
            int maximum = byte.MinValue;
            for (int y = 0; y < HashHeight; y++)
            {
                for (int x = 0; x < HashWidth; x++)
                {
                    Rgb24 pixel = roi[x, y];
                    byte luminance = (byte)(((pixel.R * 299) + (pixel.G * 587) + (pixel.B * 114)) / 1000);
                    grayscale[(y * HashWidth) + x] = luminance;
                    totalLuminance += luminance;
                    minimum = Math.Min(minimum, luminance);
                    maximum = Math.Max(maximum, luminance);
                }
            }

            double meanLuminance = totalLuminance / (double)grayscale.Length;
            if (meanLuminance < 20 || meanLuminance > 245)
            {
                reasonCode = "dark_or_saturated_image";
                return false;
            }

            if (maximum - minimum < 8)
            {
                reasonCode = "obstructed_or_low_detail_image";
                return false;
            }

            for (int y = 0; y < HashHeight; y++)
            {
                for (int x = 0; x < HashWidth - 1; x++)
                {
                    hash <<= 1;
                    if (grayscale[(y * HashWidth) + x] >
                        grayscale[(y * HashWidth) + x + 1])
                    {
                        hash |= 1;
                    }
                }
            }

            reasonCode = null;
            return true;
        }
        catch (InvalidImageContentException)
        {
            reasonCode = "invalid_image";
            return false;
        }
        catch (UnknownImageFormatException)
        {
            reasonCode = "unsupported_image_format";
            return false;
        }
    }
}
