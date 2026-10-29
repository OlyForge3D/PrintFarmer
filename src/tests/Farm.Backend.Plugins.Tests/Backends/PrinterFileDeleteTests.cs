using System.Net;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterFileDeleteTests
{
    [Theory]
    [InlineData("moonraker", "gcodes", "/server/files/gcodes/Folder/Part%20one.gcode")]
    [InlineData("octoprint", "local", "/api/files/local/Folder/Part%20one.gcode")]
    public async Task ExactDelete_UsesRegisteredCredentialAndCasePreservingRoot_NoRetryAsync(
        string backend, string root, string expectedPath)
    {
        using Handler handler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using HttpClient http = new(handler);
        ISupportsSafePrinterFileDelete client = Client(backend, http);
        Assert.Equal(PrinterFileDeleteGuarantee.CurrentExactPath,
            client.GetDeleteGuarantee(root, "Folder/Part one.gcode"));
        PrinterFileDeleteResult result = await client.DeleteExactFileAsync(
            "http://printer", root, "Folder/Part one.gcode", "not-an-atomic-version",
            PrinterCredential.FromApiKey("secret"), CancellationToken.None);
        Assert.Equal(PrinterFileDeleteOutcome.Acknowledged, result.Outcome);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("moonraker", "gcodes", HttpStatusCode.InternalServerError, PrinterFileDeleteOutcome.OutcomeUnknown)]
    [InlineData("octoprint", "local", HttpStatusCode.ServiceUnavailable, PrinterFileDeleteOutcome.OutcomeUnknown)]
    [InlineData("octoprint", "local", HttpStatusCode.Accepted, PrinterFileDeleteOutcome.OutcomeUnknown)]
    [InlineData("moonraker", "gcodes", HttpStatusCode.TemporaryRedirect, PrinterFileDeleteOutcome.OutcomeUnknown)]
    [InlineData("octoprint", "local", HttpStatusCode.NotFound, PrinterFileDeleteOutcome.ObservedAbsent)]
    [InlineData("moonraker", "gcodes", HttpStatusCode.Forbidden, PrinterFileDeleteOutcome.Rejected)]
    [InlineData("octoprint", "local", HttpStatusCode.Conflict, PrinterFileDeleteOutcome.Rejected)]
    public async Task Status_IsTypedWithoutBodyOrAutomaticRetryAsync(
        string backend, string root, HttpStatusCode status, PrinterFileDeleteOutcome expected)
    {
        using Handler handler = new((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("private provider body") }));
        using HttpClient http = new(handler);
        PrinterFileDeleteResult result = await Client(backend, http).DeleteExactFileAsync(
            "http://printer", root, "part.gcode", null, null, CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain("private", result.ToString());
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("moonraker", "gcodes")]
    [InlineData("octoprint", "local")]
    public async Task LostResponseAndCancellation_AreUnknown_NotRetriedAsync(string backend, string root)
    {
        using Handler handler = new((_, _) => throw new HttpRequestException("private endpoint"));
        using HttpClient http = new(handler);
        PrinterFileDeleteResult result = await Client(backend, http).DeleteExactFileAsync(
            "http://printer", root, "part.gcode", null, null, CancellationToken.None);
        Assert.Equal(PrinterFileDeleteOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, handler.Sends);

        using Handler cancelled = new(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        using HttpClient cancelledHttp = new(cancelled);
        using CancellationTokenSource lifetime = new(TimeSpan.FromMilliseconds(25));
        result = await Client(backend, cancelledHttp).DeleteExactFileAsync(
            "http://printer", root, "part.gcode", null, null, lifetime.Token);
        Assert.Equal(PrinterFileDeleteOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, cancelled.Sends);
    }

    [Theory]
    [InlineData("moonraker", "config", "part.gcode")]
    [InlineData("moonraker", "Gcodes", "part.gcode")]
    [InlineData("octoprint", "sdcard", "part.gcode")]
    [InlineData("octoprint", "local", "folder/")]
    [InlineData("octoprint", "local", "../part.gcode")]
    [InlineData("moonraker", "gcodes", "https://evil/file.gcode")]
    [InlineData("moonraker", "gcodes", "folder/%2e%2e/part.gcode")]
    [InlineData("octoprint", "local", "a\\part.gcode")]
    public async Task UnsafeIdentity_HasNoCapabilityOrSendAsync(string backend, string root, string path)
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No request allowed"));
        using HttpClient http = new(handler);
        ISupportsSafePrinterFileDelete client = Client(backend, http);
        Assert.Equal(PrinterFileDeleteGuarantee.Unsupported, client.GetDeleteGuarantee(root, path));
        await Assert.ThrowsAsync<PrinterFileCopyException>(() => client.DeleteExactFileAsync(
            "http://printer", root, path, null, null, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public void UncertifiedPrusaLink_DoesNotGuessLocalRootOrClaimSafeCapability()
    {
        using HttpClient http = new();
        Assert.False((object)new PrusaLinkClient(http) is ISupportsSafePrinterFileDelete);
    }

    private static ISupportsSafePrinterFileDelete Client(string backend, HttpClient http) => backend switch
    {
        "moonraker" => new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()),
        "octoprint" => new OctoPrintClient(http, NullLogger<OctoPrintClient>.Instance, new BackendTimeoutSettings()),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return send(request, cancellationToken);
        }
    }
}
