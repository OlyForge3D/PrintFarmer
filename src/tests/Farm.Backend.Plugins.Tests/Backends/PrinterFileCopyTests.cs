using System.Net;
using System.Security.Cryptography;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterFileCopyTests
{
    [Theory]
    [InlineData("moonraker", "gcodes", "/server/files/gcodes/folder/part%20one.gcode")]
    [InlineData("octoprint", "local", "/downloads/files/local/folder/part%20one.gcode")]
    public async Task Copy_UsesNativeAuthenticatedPath_AndActualDigestAsync(string backend, string root, string expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("G28\nG1 X1\n");
        using Handler handler = new((request, _) =>
        {
            Assert.Equal(expected, request.RequestUri!.AbsolutePath);
            Assert.Equal("credential", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        using HttpClient http = new(handler);
        await using MemoryStream target = new();

        PrinterFileCopyBytes result = await Client(backend, http).CopyFileToAsync(
            "http://printer", root, "folder/part one.gcode", PrinterCredential.FromApiKey("credential"),
            target, 1000, CancellationToken.None);

        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(bytes, target.ToArray());
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("moonraker", "gcodes")]
    [InlineData("octoprint", "local")]
    public async Task Copy_UnknownSize_StreamsWithActualByteCapAsync(string backend, string root)
    {
        using Handler handler = new((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(33)) }));
        using HttpClient http = new(handler);
        await using MemoryStream target = new();
        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Client(backend, http).CopyFileToAsync("http://printer", root, "part.gcode", null,
                target, 32, CancellationToken.None));
        Assert.Equal("size_limit", error.Reason);
        Assert.True(target.Length <= 32);
    }

    [Theory]
    [InlineData("moonraker", "gcodes", HttpStatusCode.Redirect, "redirect_refused")]
    [InlineData("octoprint", "local", HttpStatusCode.TemporaryRedirect, "redirect_refused")]
    [InlineData("moonraker", "gcodes", HttpStatusCode.NotFound, "source_missing")]
    [InlineData("octoprint", "local", HttpStatusCode.Forbidden, "authentication_required")]
    public async Task Copy_StatusFailure_IsSafeAndDoesNotReadBodyAsync(
        string backend, string root, HttpStatusCode status, string reason)
    {
        using Handler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(status) { Content = new StringContent("private upstream body") };
            response.Headers.Location = new Uri("http://private.invalid/?api_key=secret");
            return Task.FromResult(response);
        });
        using HttpClient http = new(handler);
        await using MemoryStream target = new();
        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Client(backend, http).CopyFileToAsync("http://printer", root, "part.gcode", null,
                target, 1000, CancellationToken.None));
        Assert.Equal(reason, error.Reason);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(0, target.Length);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("moonraker", "gcodes", "../part.gcode")]
    [InlineData("moonraker", "config", "part.gcode")]
    [InlineData("octoprint", "sdcard", "part.gcode")]
    [InlineData("octoprint", "local", "a/%2e%2e/part.gcode")]
    [InlineData("octoprint", "local", "a\\part.gcode")]
    [InlineData("moonraker", "gcodes", "/part.gcode")]
    [InlineData("moonraker", "gcodes", "http://other/part.gcode")]
    public async Task Copy_UnsafeSource_IsRejectedBeforeHttpAsync(string backend, string root, string path)
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No I/O expected"));
        using HttpClient http = new(handler);
        await using MemoryStream target = new();
        await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Client(backend, http).CopyFileToAsync("http://printer", root, path, null,
                target, 1000, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("moonraker", "gcodes")]
    [InlineData("octoprint", "local")]
    public async Task Copy_TruncatedContentLength_FailsAsync(string backend, string root)
    {
        using Handler handler = new((_, _) =>
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentLength = 10;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using HttpClient http = new(handler);
        await using MemoryStream target = new();
        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Client(backend, http).CopyFileToAsync("http://printer", root, "part.gcode", null,
                target, 1000, CancellationToken.None));
        Assert.Equal("incomplete_response", error.Reason);
    }

    [Theory]
    [InlineData("moonraker", "gcodes")]
    [InlineData("octoprint", "local")]
    public async Task Copy_Cancellation_ReachesHttpAsync(string backend, string root)
    {
        using Handler handler = new(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation expected");
        });
        using HttpClient http = new(handler);
        using CancellationTokenSource lifetime = new(TimeSpan.FromMilliseconds(50));
        await using MemoryStream target = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(backend, http).CopyFileToAsync("http://printer", root, "part.gcode", null,
                target, 1000, lifetime.Token));
    }

    [Fact]
    public void PrusaLink_DoesNotAdvertiseUncertifiedCopy()
    {
        using HttpClient http = new();
        Assert.False((object)new PrusaLinkClient(http) is ISupportsPrinterFileCopy);
    }

    private static ISupportsPrinterFileCopy Client(string backend, HttpClient http) => backend switch
    {
        "moonraker" => new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()),
        "octoprint" => new OctoPrintClient(http),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return send(request, ct);
        }
    }

    private sealed class UnknownLengthStream(int bytes) : Stream
    {
        private int _remaining = bytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(_remaining, count);
            Array.Fill(buffer, (byte)'G', offset, read);
            _remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int read = Math.Min(_remaining, buffer.Length);
            buffer.Span[..read].Fill((byte)'G');
            _remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
