using System.Net;
using System.Net.Sockets;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="KasaSmartPlugProvider"/> covering DoS protections.
/// Uses a real loopback TCP listener to exercise the binary protocol path.
/// </summary>
public class KasaSmartPlugProviderTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly KasaSmartPlugProvider _provider;

    public KasaSmartPlugProviderTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _provider = new KasaSmartPlugProvider(NullLogger<KasaSmartPlugProvider>.Instance);
    }

    public void Dispose()
    {
        _listener.Stop();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TestConnection_OversizedResponseLength_ReturnsNull()
    {
        // Arrange: server sends a 4-byte length prefix > 64KB to trigger the guard.
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();

            // Read incoming request (skip it)
            byte[] requestHeader = new byte[4];
            await stream.ReadExactlyAsync(requestHeader);
            int reqLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(requestHeader, 0));
            byte[] reqBody = new byte[reqLen];
            await stream.ReadExactlyAsync(reqBody);

            // Respond with a malicious length (1 MB)
            int maliciousLen = 1_048_576;
            byte[] lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(maliciousLen));
            await stream.WriteAsync(lenBytes);

            // Don't send actual payload — the provider should reject before reading.
            await Task.Delay(500);
        });

        // Act
        bool result = await _provider.TestConnectionAsync($"127.0.0.1:{_port}", CancellationToken.None);

        // Assert: the provider returns false (caught the InvalidOperationException internally).
        Assert.False(result);
        await serverTask;
    }

    [Fact]
    public async Task TestConnection_NegativeResponseLength_ReturnsNull()
    {
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();

            byte[] requestHeader = new byte[4];
            await stream.ReadExactlyAsync(requestHeader);
            int reqLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(requestHeader, 0));
            byte[] reqBody = new byte[reqLen];
            await stream.ReadExactlyAsync(reqBody);

            // Respond with a negative length
            int negativeLen = -1;
            byte[] lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(negativeLen));
            await stream.WriteAsync(lenBytes);
            await Task.Delay(500);
        });

        bool result = await _provider.TestConnectionAsync($"127.0.0.1:{_port}", CancellationToken.None);
        Assert.False(result);
        await serverTask;
    }

    [Fact]
    public async Task TestConnection_ReadTimeout_ReturnsNull()
    {
        // Arrange: server accepts connection but never sends a response.
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();

            byte[] requestHeader = new byte[4];
            await stream.ReadExactlyAsync(requestHeader);
            int reqLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(requestHeader, 0));
            byte[] reqBody = new byte[reqLen];
            await stream.ReadExactlyAsync(reqBody);

            // Never respond — let the read timeout kick in.
            await Task.Delay(TimeSpan.FromSeconds(10));
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));
        bool result = await _provider.TestConnectionAsync($"127.0.0.1:{_port}", cts.Token);
        Assert.False(result);
    }
}
