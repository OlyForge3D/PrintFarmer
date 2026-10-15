using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FailureDetection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public partial class ObicoFailureDetectionServiceTests
{
    private const string TlsSnapshotHostname = "camera.invalid";

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenHttpsSnapshotIsPinned_PreservesTlsHostnameAndUploads()
    {
        await using var server = new TlsSnapshotServer(TlsSnapshotHostname);

        TlsAnalysis analysis = await AnalyzeTlsSnapshotAsync(server);

        using var scope = new AssertionScope();
        analysis.Result.ErrorMessage.Should().BeNull("the local TLS fixture must authenticate ({0})", server.ServerError);
        analysis.Result.Confidence.Should().Be(0.82m);
        analysis.DnsCalls.Should().Be(1, "the guarded .invalid hostname must not be resolved again by the transport");
        server.ConnectionCount.Should().Be(1);
        server.ServerName.Should().Be(TlsSnapshotHostname, "the actual ClientHello must use the original hostname, not the pinned IP");
        server.HostHeader.Should().Be($"{TlsSnapshotHostname}:{server.Port}");
        analysis.CertificateErrors.Should().Be(SslPolicyErrors.RemoteCertificateChainErrors,
            "the hostname-only self-signed certificate must match; only its test-only trust anchor needs configuration");
        analysis.ObicoRequests.Should().HaveCount(2);
        analysis.ObicoRequests.Should().Contain(request =>
            request.Method == HttpMethod.Post && request.Body.Contains("camera-image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenHttpsCertificateHostnameDoesNotMatch_RejectsWithoutUpload()
    {
        await using var server = new TlsSnapshotServer("different.invalid");

        TlsAnalysis analysis = await AnalyzeTlsSnapshotAsync(server);

        analysis.Result.ErrorMessage.Should().StartWith("PrintFarmer could not download the camera snapshot:");
        analysis.CertificateErrors.Should().NotBeNull();
        analysis.CertificateErrors!.Value.Should().HaveFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
        server.ServerName.Should().Be(TlsSnapshotHostname);
        server.HostHeader.Should().BeNull("certificate validation must fail before sending the HTTP request");
        analysis.ObicoRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task AnalyzeImageFromUrlAsync_WhenPinnedHttpsSnapshotRedirects_NeverConnectsToRedirectTarget(
        HttpStatusCode redirectStatus)
    {
        await using var redirectTarget = new TlsSnapshotServer(TlsSnapshotHostname);
        await using var server = new TlsSnapshotServer(
            TlsSnapshotHostname,
            redirectStatus,
            new Uri($"https://127.0.0.1:{redirectTarget.Port}/unvetted.jpg"));

        TlsAnalysis analysis = await AnalyzeTlsSnapshotAsync(server);

        analysis.Result.ErrorMessage.Should().Be($"Failed to fetch snapshot: HTTP {(int)redirectStatus}");
        server.ServerName.Should().Be(TlsSnapshotHostname);
        server.ConnectionCount.Should().Be(1);
        redirectTarget.ConnectionCount.Should().Be(0);
        analysis.DnsCalls.Should().Be(1);
        analysis.ObicoRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    private static async Task<TlsAnalysis> AnalyzeTlsSnapshotAsync(TlsSnapshotServer server)
    {
        List<CapturedRequest> obicoRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return request.Method == HttpMethod.Get
                ? CreateFallbackResponse(HttpStatusCode.MethodNotAllowed)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":{\"p\":0.82}}", Encoding.UTF8, "application/json")
                };
        });
        SslPolicyErrors? certificateErrors = null;
        using HttpClientHandler snapshotHandler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                certificateErrors = errors;
                // Trust only this fixture's self-signed certificate, without installing it in
                // the machine store or bypassing the transport's hostname validation.
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(server.Certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return certificate is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                    chain.Build(certificate);
            }
        };
        Mock<IHttpClientFactory> factory = CreateFallbackClientFactory(obicoHandler, snapshotHandler);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ALLOWED_NETWORK_RANGES"] = "127.0.0.1/32" })
            .Build();
        int dnsCalls = 0;
        var guard = new EgressGuard(configuration, NullLogger<EgressGuard>.Instance, (_, _) =>
        {
            dnsCalls++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });
        ObicoFailureDetectionService service = CreateService(factory, guard);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(
            $"https://{TlsSnapshotHostname}:{server.Port}/snapshot.jpg?camera=1",
            "http://obico.local",
            ct: timeout.Token);

        factory.Verify(clientFactory => clientFactory.CreateClient("VettedEgress"), Times.Once);
        return new TlsAnalysis(result, obicoRequests, dnsCalls, certificateErrors);
    }

    private sealed record TlsAnalysis(
        FailureDetectionResult Result,
        List<CapturedRequest> ObicoRequests,
        int DnsCalls,
        SslPolicyErrors? CertificateErrors);

    private sealed class TlsSnapshotServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly RSA _key = RSA.Create(2048);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serverTask;
        private readonly HttpStatusCode _status;
        private readonly Uri? _redirect;
        private int _connectionCount;

        public TlsSnapshotServer(string certificateHostname, HttpStatusCode status = HttpStatusCode.OK, Uri? redirect = null)
        {
            var certificateRequest = new CertificateRequest(
                $"CN={certificateHostname}", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(certificateHostname);
            certificateRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());
            using X509Certificate2 generatedCertificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel cannot authenticate with CreateSelfSigned's ephemeral private key.
            // Reimport without EphemeralKeySet; disposal releases the imported key.
            Certificate = X509CertificateLoader.LoadPkcs12(generatedCertificate.Export(X509ContentType.Pkcs12), password: null);
            _status = status;
            _redirect = redirect;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = ServeAsync();
        }

        public X509Certificate2 Certificate { get; }

        public int Port { get; }

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public string? ServerName { get; private set; }

        public string? HostHeader { get; private set; }

        public Exception? ServerError { get; private set; }

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                Interlocked.Increment(ref _connectionCount);
                await using var stream = new SslStream(connection.GetStream());
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        ServerName = name;
                        return Certificate;
                    }
                }, _shutdown.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (true)
                {
                    string? line = await reader.ReadLineAsync(_shutdown.Token);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }

                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        HostHeader = line["Host:".Length..].Trim();
                    }
                }

                string body = _status == HttpStatusCode.OK ? "camera-image" : string.Empty;
                string location = _redirect is null ? string.Empty : $"Location: {_redirect.AbsoluteUri}\r\n";
                byte[] response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(int)_status} {_status}\r\nContent-Length: {body.Length}\r\n{location}Connection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, _shutdown.Token);
                await stream.FlushAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (AuthenticationException ex)
            {
                // A client rejecting the fixture's deliberately mismatched certificate is expected.
                ServerError = ex;
            }
            catch (IOException ex)
            {
                // Certificate rejection may also close the connection before an HTTP request.
                ServerError = ex;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
#pragma warning disable VSTHRD003 // This fixture owns and cancels the listener task; it has no foreign/UI-thread dependency.
            await _serverTask;
#pragma warning restore VSTHRD003
            Certificate.Dispose();
            _key.Dispose();
            _shutdown.Dispose();
        }
    }
}
