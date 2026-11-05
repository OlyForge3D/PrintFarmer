namespace Farm.Infrastructure.Services.FailureDetection;

internal static class BoundedHttpContentReader
{
    public static async Task<byte[]> ReadAsByteArrayAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException($"HTTP content exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream contentStream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int totalBytes = 0;

        while (true)
        {
            int requestedBytes = Math.Min(chunk.Length, maximumBytes - totalBytes + 1);
            int bytesRead = await contentStream.ReadAsync(chunk.AsMemory(0, requestedBytes), ct);
            if (bytesRead == 0)
            {
                return buffer.ToArray();
            }

            totalBytes += bytesRead;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException($"HTTP content exceeds the {maximumBytes}-byte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), ct);
        }
    }
}
