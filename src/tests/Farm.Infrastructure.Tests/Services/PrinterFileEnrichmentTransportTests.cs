using System.Net;
using System.Net.Http.Headers;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Services;

public class PrinterFileEnrichmentTransportTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(6, 4)]
    public async Task ReadRegionAsync_ExactRange_ReturnsOnlyRequestedBytes(long offset, int count)
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        byte[] source = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        RangeHeaderValue? requested = null;
        var result = await PrinterFileEnrichmentTransport.ReadRegionAsync((range, _) =>
        {
            budget.BeginRequest();
            requested = range;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(source[(int)offset..((int)offset + count)]),
            };
            response.Content.Headers.ContentRange = new(offset, offset + count - 1, source.Length);
            return Task.FromResult(response);
        }, offset, count, source.Length, budget);

        Assert.Equal($"bytes={offset}-{offset + count - 1}", requested?.ToString());
        Assert.Equal(source[(int)offset..((int)offset + count)], result.Bytes);
        Assert.Equal(offset, result.Offset);
        Assert.False(result.CompleteFile);
        Assert.Equal(count, budget.ResponseBytes);
        Assert.Equal(1, budget.Requests);
    }

    [Fact]
    public async Task ReadRegionAsync_IgnoredRange_DoesNotDrainFile()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var stream = new CountingStream(new byte[100_000]);
        var result = await PrinterFileEnrichmentTransport.ReadRegionAsync((_, _) =>
        {
            budget.BeginRequest();
            return Task.FromResult(Response(HttpStatusCode.OK, stream));
        }, 0, 1024, 100_000, budget);

        Assert.Equal(1024, result.Bytes.Length);
        Assert.False(result.CompleteFile);
        Assert.Equal(1024, stream.BytesRead);
        Assert.Equal(1024, budget.ResponseBytes);
    }

    [Fact]
    public async Task ReadRegionAsync_IgnoredTail_RefusesWithoutReadingBody()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var stream = new CountingStream(new byte[100_000]);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadRegionAsync((_, _) =>
                Task.FromResult(Response(HttpStatusCode.OK, stream)), 99_000, 1000, 100_000, budget));

        Assert.Equal("range_unsupported", error.Reason);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData("missing", "invalid_content_range")]
    [InlineData("offset", "invalid_content_range")]
    [InlineData("length", "invalid_content_range")]
    [InlineData("unit", "invalid_content_range")]
    [InlineData("truncated", "incomplete_response")]
    [InlineData("extra", "size_limit")]
    public async Task ReadRegionAsync_InvalidRangeResponse_FailsClosed(string corruption, string reason)
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadRegionAsync((_, _) =>
            {
                var response = Response(HttpStatusCode.PartialContent,
                    new CountingStream(new byte[corruption == "truncated" ? 3 : corruption == "extra" ? 5 : 4]));
                if (corruption != "missing")
                {
                    response.Content.Headers.ContentRange = new(
                        corruption == "offset" ? 1 : 0, 3, corruption == "length" ? 11 : 10)
                    {
                        Unit = corruption == "unit" ? "items" : "bytes",
                    };
                }

                return Task.FromResult(response);
            }, 0, 4, 10, budget));

        Assert.Equal(reason, error.Reason);
        Assert.InRange(budget.ResponseBytes, 0, 5);
    }

    [Fact]
    public async Task ReadRegionAsync_SmallFile_ReturnsCompleteFile()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        RangeHeaderValue? requested = null;
        var result = await PrinterFileEnrichmentTransport.ReadRegionAsync((range, _) =>
        {
            requested = range;
            return Task.FromResult(Response(HttpStatusCode.OK, new CountingStream([1, 2, 3])));
        }, 0, 1024, 3, budget);

        Assert.Equal("bytes=0-2", requested?.ToString());
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.True(result.CompleteFile);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "redirect_refused")]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_required")]
    [InlineData(HttpStatusCode.NotFound, "source_missing")]
    public async Task ReadAsync_InvalidStatus_DoesNotReadResponse(HttpStatusCode status, string reason)
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var stream = new CountingStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadAsync(_ => Task.FromResult(Response(status, stream)), 1024, budget));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_EncodedResponse_RefusesBeforeReadingBody()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var stream = new CountingStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadAsync(_ =>
            {
                var response = Response(HttpStatusCode.OK, stream);
                response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult(response);
            }, 1024, budget));

        Assert.Equal("encoded_response", error.Reason);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_MissingContentLength_StopsAfterLimitPlusProbe()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        var stream = new CountingStream(new byte[100_000]);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadAsync(_ =>
                Task.FromResult(Response(HttpStatusCode.OK, stream)), 1024, budget));

        Assert.Equal("size_limit", error.Reason);
        Assert.Equal(1025, stream.BytesRead);
        Assert.Equal(1025, budget.ResponseBytes);
    }

    [Fact]
    public async Task ReadRegionAsync_SharedByteBudget_CannotReadPastEightMiB()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        const int region = PrinterFileEnrichmentLimits.MaximumResponseBytes / 4;
        for (int i = 0; i < 4; i++)
        {
            var result = await PrinterFileEnrichmentTransport.ReadRegionAsync((_, _) =>
                Task.FromResult(Response(HttpStatusCode.OK, new CountingStream(new byte[region + 1]))),
                0, region, region + 1, budget);
            Assert.Equal(region, result.Bytes.Length);
        }

        var stream = new CountingStream([1]);
        var error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            PrinterFileEnrichmentTransport.ReadRegionAsync((_, _) =>
                Task.FromResult(Response(HttpStatusCode.OK, stream)), 0, 1, null, budget));
        Assert.Equal("response_limit", error.Reason);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(PrinterFileEnrichmentLimits.MaximumResponseBytes, budget.ResponseBytes);
    }

    [Fact]
    public void BeginRequest_SharedRequestBudget_RefusesNinthRequest()
    {
        using var budget = new PrinterFileEnrichmentBudget(CancellationToken.None);
        for (int i = 0; i < PrinterFileEnrichmentLimits.MaximumRequests; i++)
        {
            budget.BeginRequest();
        }

        Assert.Equal(8, budget.Requests);
        Assert.Equal("request_limit", Assert.Throws<PrinterFileCopyException>(budget.BeginRequest).Reason);
    }

    [Fact]
    public async Task ReadAsync_Cancellation_StopsReadWithoutSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new PrinterFileEnrichmentBudget(cancellation.Token);
        await cancellation.CancelAsync();
        var stream = new CountingStream([1, 2, 3]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrinterFileEnrichmentTransport.ReadAsync(_ =>
                Task.FromResult(Response(HttpStatusCode.OK, stream)), 1024, budget));
        Assert.Equal(0, stream.BytesRead);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, Stream stream) =>
        new(status) { Content = new StreamContent(stream) };

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
