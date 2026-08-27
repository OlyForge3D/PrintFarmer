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
        _listener.Dispose();
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
        // Deterministic: no real socket, no wall-clock wait. A blocking stream never yields a
        // response; a zero read timeout makes the internal linked-token CancelAfter fire
        // immediately, so the read observes cancellation and TestConnection returns false.
        KasaSmartPlugProvider provider = new(
            NullLogger<KasaSmartPlugProvider>.Instance,
            new BlockingConnector(),
            TimeSpan.Zero);

        bool result = await provider.TestConnectionAsync("device.local", CancellationToken.None);

        Assert.False(result);
    }

    /// <summary>Connector that hands back a stream which never completes a read.</summary>
    private sealed class BlockingConnector : KasaSmartPlugProvider.IKasaConnector
    {
        public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
            => Task.FromResult<Stream>(new BlockingStream());

        /// <summary>
        /// A stream whose writes are no-ops and whose reads block until the supplied token is
        /// canceled (then throw <see cref="OperationCanceledException"/>). Models a device that
        /// accepted the connection but never sent a response.
        /// </summary>
        private sealed class BlockingStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => 0;
                set => _ = value;
            }

            public override void Flush()
            {
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => ValueTask.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
                => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
