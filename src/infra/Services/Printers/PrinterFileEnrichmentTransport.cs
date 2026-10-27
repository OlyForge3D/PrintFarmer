using System.Net;
using System.Net.Http.Headers;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>One acquisition's shared request, response-byte and wall-clock allowance.</summary>
public sealed class PrinterFileEnrichmentBudget : IDisposable
{
    private readonly CancellationTokenSource deadline;
    private int requests;
    private long responseBytes;

    public PrinterFileEnrichmentBudget(CancellationToken ct)
    {
        deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PrinterFileEnrichmentLimits.FileDeadline);
    }

    public CancellationToken Token => deadline.Token;

    public long ResponseBytes => Interlocked.Read(ref responseBytes);

    public int Requests => Volatile.Read(ref requests);

    /// <summary>Call before every wire request, including authentication retries.</summary>
    public void BeginRequest()
    {
        Token.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref requests) > PrinterFileEnrichmentLimits.MaximumRequests)
        {
            throw new PrinterFileCopyException("request_limit");
        }
    }

    internal int ReadAllowance(int requested)
    {
        Token.ThrowIfCancellationRequested();
        long remaining = PrinterFileEnrichmentLimits.MaximumResponseBytes - ResponseBytes;
        if (remaining <= 0)
        {
            throw new PrinterFileCopyException("response_limit");
        }

        return (int)Math.Min(requested, remaining);
    }

    internal void RecordRead(int count)
    {
        if (Interlocked.Add(ref responseBytes, count) > PrinterFileEnrichmentLimits.MaximumResponseBytes)
        {
            throw new PrinterFileCopyException("response_limit");
        }
    }

    public void Dispose() => deadline.Dispose();
}

/// <summary>A bounded, verified byte region, not a byte-identity claim for the remote file.</summary>
public sealed record PrinterFileRegion(byte[] Bytes, long Offset, bool CompleteFile);

/// <summary>
/// Bounded streaming for background enrichment. Senders disable redirects and decompression,
/// use ResponseHeadersRead, and charge every request to the supplied budget.
/// </summary>
public static class PrinterFileEnrichmentTransport
{
    /// <summary>Reads a complete native metadata or image response within its own limit.</summary>
    public static async Task<byte[]> ReadAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        int maximumBytes, PrinterFileEnrichmentBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using HttpResponseMessage response = await send(budget.Token).ConfigureAwait(false);
        ValidateStatus(response, allowPartial: false);
        long? length = response.Content.Headers.ContentLength;
        if (length > maximumBytes)
        {
            throw new PrinterFileCopyException("size_limit");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        byte[] bytes = await ReadBytesAsync(stream, maximumBytes, requireEnd: true, budget).ConfigureAwait(false);
        if (bytes.Length == 0 || (length.HasValue && bytes.LongLength != length.Value))
        {
            throw new PrinterFileCopyException("incomplete_response");
        }

        return bytes;
    }

    /// <summary>
    /// Reads an exact Range response, or just a bounded prefix when a server ignores Range.
    /// An ignored tail request is refused without reading the whole file to reach its end.
    /// </summary>
    public static async Task<PrinterFileRegion> ReadRegionAsync(
        Func<RangeHeaderValue, CancellationToken, Task<HttpResponseMessage>> send,
        long offset, int maximumBytes, long? sourceSize, PrinterFileEnrichmentBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (sourceSize is <= 0 || (sourceSize.HasValue && offset >= sourceSize.Value) ||
            offset > long.MaxValue - maximumBytes)
        {
            throw new PrinterFileCopyException("invalid_source_size");
        }

        long end = offset + maximumBytes - 1;
        if (sourceSize.HasValue)
        {
            end = Math.Min(end, sourceSize.Value - 1);
        }

        using HttpResponseMessage response = await send(new RangeHeaderValue(offset, end), budget.Token).ConfigureAwait(false);
        ValidateStatus(response, allowPartial: true);
        long? length = response.Content.Headers.ContentLength;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != offset || !range.To.HasValue ||
                !range.Length.HasValue || range.Length <= range.To || range.To > end ||
                range.To != Math.Min(end, range.Length.Value - 1) ||
                (sourceSize.HasValue && range.Length != sourceSize))
            {
                throw new PrinterFileCopyException("invalid_content_range");
            }

            int count = checked((int)(range.To.Value - offset + 1));
            if (length.HasValue && length.Value != count)
            {
                throw new PrinterFileCopyException("incomplete_response");
            }

            await using Stream content = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            byte[] bytes = await ReadBytesAsync(content, count, requireEnd: true, budget).ConfigureAwait(false);
            if (bytes.Length != count)
            {
                throw new PrinterFileCopyException("incomplete_response");
            }

            return new(bytes, offset, offset == 0 && range.To == range.Length - 1);
        }

        if (offset != 0)
        {
            throw new PrinterFileCopyException("range_unsupported");
        }

        if (sourceSize.HasValue && length.HasValue && sourceSize != length)
        {
            throw new PrinterFileCopyException("source_changed");
        }

        long? total = length ?? sourceSize;
        int prefixSize = (int)Math.Min(maximumBytes, total ?? maximumBytes);
        bool complete = total.HasValue && total.Value <= maximumBytes;
        await using Stream prefix = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        byte[] result = await ReadBytesAsync(prefix, prefixSize, requireEnd: complete, budget).ConfigureAwait(false);
        if ((total.HasValue && result.Length != prefixSize) || result.Length == 0)
        {
            throw new PrinterFileCopyException("incomplete_response");
        }

        return new(result, 0, complete || result.Length < prefixSize);
    }

    private static void ValidateStatus(HttpResponseMessage response, bool allowPartial)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new PrinterFileCopyException("redirect_refused");
        }

        if (response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new PrinterFileCopyException("encoded_response");
        }

        if (response.StatusCode != HttpStatusCode.OK && !(allowPartial && response.StatusCode == HttpStatusCode.PartialContent))
        {
            throw new PrinterFileCopyException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_required",
                HttpStatusCode.NotFound => "source_missing",
                _ => "transfer_failed",
            });
        }
    }

    private static async Task<byte[]> ReadBytesAsync(
        Stream stream, int maximumBytes, bool requireEnd, PrinterFileEnrichmentBudget budget)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[Math.Min(maximumBytes, 81920)];
        while (output.Length < maximumBytes)
        {
            int allowance = budget.ReadAllowance((int)Math.Min(buffer.Length, maximumBytes - output.Length));
            int count = await stream.ReadAsync(buffer.AsMemory(0, allowance), budget.Token).ConfigureAwait(false);
            budget.RecordRead(count);
            if (count == 0)
            {
                return output.ToArray();
            }

            await output.WriteAsync(buffer.AsMemory(0, count), budget.Token).ConfigureAwait(false);
        }

        if (requireEnd)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, budget.ReadAllowance(1)), budget.Token).ConfigureAwait(false);
            budget.RecordRead(count);
            if (count != 0)
            {
                throw new PrinterFileCopyException("size_limit");
            }
        }

        budget.Token.ThrowIfCancellationRequested();
        return output.ToArray();
    }
}
