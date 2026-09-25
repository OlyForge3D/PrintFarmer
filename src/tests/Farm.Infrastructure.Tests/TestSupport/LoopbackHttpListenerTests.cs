using System.Net;
using System.Net.Sockets;

namespace Farm.Infrastructure.Tests.TestSupport;

public sealed class LoopbackHttpListenerTests
{
    [Fact]
    public async Task Start_ReturnsRunningListenerThatServesOnReturnedLoopbackPort()
    {
        (HttpListener listener, int port) = LoopbackHttpListener.Start();
        using (listener)
        {
            listener.IsListening.Should().BeTrue("the started listener is handed over, not released (#3029)");
            listener.Prefixes.Should().ContainSingle().Which.Should().Be($"http://127.0.0.1:{port}/");

            using HttpClient http = new();
            Task<HttpResponseMessage> request = http.GetAsync(new Uri($"http://127.0.0.1:{port}/"));
            HttpListenerContext context = await listener.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Close();

            using HttpResponseMessage response = await request;
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public void Start_WhenCandidatePortIsHeldByAnotherSocket_RetriesWithNextCandidate()
    {
        using TcpListener occupant = new(IPAddress.Loopback, 0);
        occupant.Start();
        int occupiedPort = ((IPEndPoint)occupant.LocalEndpoint).Port;
        Queue<int> candidates = new([occupiedPort]);
        int calls = 0;

        (HttpListener listener, int port) = LoopbackHttpListener.Start(
            candidate => $"http://127.0.0.1:{candidate}/",
            () =>
            {
                calls++;
                return candidates.Count > 0 ? candidates.Dequeue() : LoopbackHttpListener.NextCandidatePort();
            });
        using (listener)
        {
            calls.Should().Be(2, "the bind on the occupied port must fail and be retried");
            port.Should().NotBe(occupiedPort);
            listener.IsListening.Should().BeTrue();
        }
    }

    [Fact]
    public void Start_WhenSamePrefixIsAlreadyRegisteredInProcess_RetriesWithNextCandidate()
    {
        (HttpListener first, int firstPort) = LoopbackHttpListener.Start();
        using (first)
        {
            Queue<int> candidates = new([firstPort]);

            (HttpListener second, int secondPort) = LoopbackHttpListener.Start(
                candidate => $"http://127.0.0.1:{candidate}/",
                () => candidates.Count > 0 ? candidates.Dequeue() : LoopbackHttpListener.NextCandidatePort());
            using (second)
            {
                secondPort.Should().NotBe(firstPort);
                second.IsListening.Should().BeTrue();
                first.IsListening.Should().BeTrue("a failed retry must not disturb the listener already serving");
            }
        }
    }

    [Fact]
    public void Start_WhenEveryCandidateIsTaken_ThrowsWithEachFailure()
    {
        using TcpListener occupant = new(IPAddress.Loopback, 0);
        occupant.Start();
        int occupiedPort = ((IPEndPoint)occupant.LocalEndpoint).Port;

        Action act = () => LoopbackHttpListener.Start(
            candidate => $"http://127.0.0.1:{candidate}/",
            () => occupiedPort,
            maxAttempts: 3);

        act.Should().Throw<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(3);
    }

    [Fact]
    public void Start_WhenPrefixIsInvalid_PropagatesWithoutRetrying()
    {
        int calls = 0;

        Action act = () => LoopbackHttpListener.Start(
            _ => "not-a-prefix",
            () =>
            {
                calls++;
                return LoopbackHttpListener.NextCandidatePort();
            });

        act.Should().Throw<ArgumentException>();
        calls.Should().Be(1, "only bind failures are retryable");
    }
}
