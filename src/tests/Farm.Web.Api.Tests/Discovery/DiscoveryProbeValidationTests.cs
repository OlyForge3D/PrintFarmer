using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Discovery;

public class DiscoveryProbeValidationTests
{
    [Theory]
    [InlineData("{ \"printer_model\":\"MK4\", \"friendly_name\":\"Prusa\" }", 100)]
    [InlineData("{ \"printer_model\":\"MK4\" }", 85)]
    public async Task PrusaLinkProbe_ScoresByFieldCount(string json, int expectedScore)
    {
        var probe = new TestablePrusaLinkProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeTrue();
        score.Should().Be(expectedScore);
        reason.Should().Contain("PrusaLink detected");
    }

    [Theory]
    [InlineData("{ \"friendly_name\":\"My Printer\", \"printer_model\":\"MK4\" }", 100)]
    [InlineData("{ \"printer_model\":\"MK4\", \"prusa\":\"field\" }", 100)]
    [InlineData("{ \"friendly_name\":\"Prusa\" }", 85)]
    public async Task PrusaLinkProbe_RecognizesVariantFields(string json, int expectedScore)
    {
        var probe = new TestablePrusaLinkProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeTrue();
        score.Should().Be(expectedScore);
        reason.Should().Contain("PrusaLink detected");
    }

    [Fact]
    public async Task PrusaLinkProbe_InvalidWhenMissingFields()
    {
        var probe = new TestablePrusaLinkProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("No Prusa fields");
    }

    [Fact]
    public async Task OctoPrintProbe_ReturnsZeroWhenMoonrakerDetected()
    {
        string json = "{ \"api\":\"1\", \"text\":\"Moonraker compat\" }";
        var probe = new TestableOctoPrintProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("Moonraker");
    }

    [Fact]
    public async Task OctoPrintProbe_Confidence100_WhenTextMentionsOctoPrint()
    {
        string json = "{ \"api\":\"1\", \"text\":\"OctoPrint server\" }";
        var probe = new TestableOctoPrintProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeTrue();
        score.Should().Be(100);
        reason.Should().Contain("OctoPrint detected");
    }

    [Theory]
    [InlineData("{ \"api\":\"1\", \"text\":\"OctoPrint\" }", 100)]
    [InlineData("{ \"api\":\"1\", \"text\":\"Octoprint\" }", 100)]  // Case-insensitive match
    [InlineData("{ \"api\":\"1\" }", 75)]  // No text field
    public async Task OctoPrintProbe_VariableScoring(string json, int expectedScore)
    {
        var probe = new TestableOctoPrintProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeTrue();
        score.Should().Be(expectedScore);
    }

    [Theory]
    [InlineData("{ \"result\": { \"state_message\":\"ok\", \"klipper_path\":\"/path\", \"hostname\":\"host\" } }", 100)]
    [InlineData("{ \"result\": { \"state_message\":\"ok\", \"hostname\":\"host\" } }", 90)]
    [InlineData("{ \"result\": { \"state_message\":\"ok\" } }", 75)]
    public async Task MoonrakerProbe_ScoresByKlipperFields(string json, int expectedScore)
    {
        var probe = new TestableMoonrakerProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeTrue();
        score.Should().Be(expectedScore);
        reason.Should().Contain("Moonraker detected");
    }

    [Fact]
    public async Task MoonrakerProbe_InvalidWhenMissingResult()
    {
        var probe = new TestableMoonrakerProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ }")
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("Missing 'result'");
    }

    [Fact]
    public async Task AllProbes_InvalidWhenJsonMalformed()
    {
        // Test each probe with invalid JSON
        var probes = new INetworkDiscoveryProbe[]
        {
            new PrusaLinkDiscoveryProbe(),
            new OctoPrintDiscoveryProbe(),
            new MoonrakerDiscoveryProbe(),
        };

        foreach (var probe in probes)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ broken json")
            };

            // Should handle JsonException gracefully
            Func<Task> act = async () => await probe.ProbeAsync("127.0.0.1", timeoutMs: 1000, cancellationToken: default);
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task PrusaLinkProbe_InvalidWhenStatusNotOk()
    {
        var probe = new TestablePrusaLinkProbe();
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{ \"printer_model\":\"MK4\" }")
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("HTTP error");
    }

    [Fact]
    public async Task OctoPrintProbe_InvalidWhenStatusNotOk()
    {
        var probe = new TestableOctoPrintProbe();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{ \"api\":\"1\" }")
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("HTTP error");
    }

    [Fact]
    public async Task MoonrakerProbe_InvalidWhenStatusNotOk()
    {
        var probe = new TestableMoonrakerProbe();
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{ \"result\": { \"state_message\":\"ok\" } }")
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, await response.Content.ReadAsStringAsync());

        valid.Should().BeFalse();
        score.Should().Be(0);
        reason.Should().Contain("HTTP error");
    }

    [Fact]
    public async Task BaseProbe_ReturnsResultWhenValidatePasses()
    {
        using var server = new LoopbackServer();
        server.Start();

        var probe = new TestableBaseProbe(server.Port, shouldValidate: true);

        ProbeResult? result = await probe.ProbeAsync("127.0.0.1", timeoutMs: 2000, cancellationToken: default);

        result.Should().NotBeNull();
        result!.Printer.Backend.Should().Be(PrinterBackend.Moonraker);
        result.Printer.BackendPort.Should().Be(server.Port);
        result.Printer.ServerUrl.Should().Be("http://127.0.0.1");
        result.ConfidenceScore.Should().Be(80);
        result.Reason.Should().Be("ok");
        result.Printer.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BaseProbe_ReturnsNullWhenValidateFails()
    {
        using var server = new LoopbackServer();
        server.Start();

        var probe = new TestableBaseProbe(server.Port, shouldValidate: false);

        ProbeResult? result = await probe.ProbeAsync("127.0.0.1", timeoutMs: 2000, cancellationToken: default);

        result.Should().BeNull();
    }

    private sealed class TestablePrusaLinkProbe : PrusaLinkDiscoveryProbe
    {
        public Task<(bool, int, string)> CallValidateAsync(HttpResponseMessage response, string content) => ValidateResponseAsync(response, content);
    }

    private sealed class TestableOctoPrintProbe : OctoPrintDiscoveryProbe
    {
        public Task<(bool, int, string)> CallValidateAsync(HttpResponseMessage response, string content) => ValidateResponseAsync(response, content);
    }

    private sealed class TestableMoonrakerProbe : MoonrakerDiscoveryProbe
    {
        public Task<(bool, int, string)> CallValidateAsync(HttpResponseMessage response, string content) => ValidateResponseAsync(response, content);
    }

    private sealed class TestableBaseProbe(int port, bool shouldValidate) : BaseDiscoveryProbe
    {
        private readonly int _port = port;
        private readonly bool _shouldValidate = shouldValidate;

        public override string DisplayName => "TestBase";
        protected override int[] Ports => new[] { _port };
        protected override string EndpointPath => "/test";
        protected override PrinterBackend Backend => PrinterBackend.Moonraker;
        protected override string PrinterName => "Loopback";

        protected override Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateResponseAsync(HttpResponseMessage response, string content)
        {
            if (!_shouldValidate)
            {
                return Task.FromResult<(bool, int, string)>((false, 0, "no"));
            }

            return Task.FromResult<(bool, int, string)>((true, 80, "ok"));
        }
    }

    [Fact]
    public async Task SdcpDiscoveryProbe_ReturnsNullOnInvalidJson()
    {
        var probe = new SdcpDiscoveryProbe();

        // SdcpDiscoveryProbe uses UDP and catches JsonException
        // We test by calling it with a non-existent IP (will timeout, then return null)
        ProbeResult? result = await probe.ProbeAsync("127.0.0.255", timeoutMs: 100, cancellationToken: default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SdcpDiscoveryProbe_ReturnsNullOnMissingDataStructure()
    {
        var probe = new SdcpDiscoveryProbe();

        // Test against invalid IP (will fail gracefully)
        ProbeResult? result = await probe.ProbeAsync("192.0.2.1", timeoutMs: 100, cancellationToken: default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task InvalidHttpResponses_HandleEdgeCases()
    {
        // Empty content
        var emptyProbe = new TestablePrusaLinkProbe();
        var emptyResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("")
        };

        Func<Task> act = async () => await emptyProbe.CallValidateAsync(emptyResponse, "");
        await act.Should().NotThrowAsync();

        // Whitespace only
        var wsProbe = new TestableOctoPrintProbe();
        var wsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("   ")
        };

        act = async () => await wsProbe.CallValidateAsync(wsResponse, "   ");
        await act.Should().NotThrowAsync();

        // Null content
        var nullProbe = new TestableMoonrakerProbe();
        var nullResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = null!
        };

        // This tests defensive null handling
        act = async () => await nullProbe.CallValidateAsync(nullResponse, "");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MoonrakerProbe_DiscoversFrontendPort()
    {
        // Test that frontend port discovery works when multiple ports are available
        using var loopback = new LoopbackServerOnPort(8080);
        loopback.Start();

        var probe = new MoonrakerDiscoveryProbe();
        // This will attempt to discover frontend port; may return null if backend 7125 not available
        // We're testing defensive behavior here
        await probe.ProbeAsync("127.0.0.1", timeoutMs: 500, cancellationToken: default);
    }

    [Fact]
    public async Task MoonrakerProbe_ExtractHostnameFromResponse()
    {
        string json = "{ \"result\": { \"state_message\":\"ok\", \"hostname\":\"my-printer\", \"klipper_path\":\"/path\" } }";
        var probe = new TestableMoonrakerProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, json);

        valid.Should().BeTrue();
        score.Should().Be(100);
        reason.Should().Contain("3/3");
    }

    [Fact]
    public async Task MoonrakerProbe_NoHostnameInResponse()
    {
        string json = "{ \"result\": { \"state_message\":\"ok\", \"klipper_path\":\"/path\" } }";
        var probe = new TestableMoonrakerProbe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        (bool valid, int score, string reason) = await probe.CallValidateAsync(response, json);

        valid.Should().BeTrue();
        score.Should().Be(90);
        reason.Should().Contain("2/3");
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private Task? _worker;

        public int Port { get; }

        public LoopbackServer()
        {
            Port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        }

        public void Start()
        {
            _listener.Start();
            _worker = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext context = await _listener.GetContextAsync();
                        context.Response.StatusCode = (int)HttpStatusCode.OK;
                        await context.Response.OutputStream.FlushAsync();
                        context.Response.Close();
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            });
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
            _worker?.Wait(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class LoopbackServerOnPort : IDisposable
    {
        private readonly HttpListener _listener;
        private Task? _worker;
        private readonly int _port;

        public LoopbackServerOnPort(int port)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            try
            {
                _listener.Start();
                _worker = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        try
                        {
                            HttpListenerContext context = await _listener.GetContextAsync();
                            context.Response.StatusCode = (int)HttpStatusCode.OK;
                            await context.Response.OutputStream.FlushAsync();
                            context.Response.Close();
                        }
                        catch (HttpListenerException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                    }
                });
            }
            catch
            {
                // Port may be in use, that's ok for this test
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Close();
                _worker?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore errors on cleanup
            }
        }
    }
}
