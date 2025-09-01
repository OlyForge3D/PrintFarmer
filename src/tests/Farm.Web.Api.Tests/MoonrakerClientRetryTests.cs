using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Tests;

public class MoonrakerClientRetryTests
{
    private const string Base = "http://printer";

    private static (IMoonrakerClient client, Mock<HttpMessageHandler> handler, int attempts) CreateFlakyClient(Func<int, HttpResponseMessage> responder)
    {
        int attempt = 0;
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                attempt++;
                return responder(attempt);
            });
        var http = new HttpClient(handler.Object);
        return (new MoonrakerClient(http), handler, attempt);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnThirdAttempt()
    {
        var (client, _, _) = CreateFlakyClient(i =>
        {
            if (i < 3) throw new HttpRequestException("boom");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { result = new { state = "ready" } }), Encoding.UTF8, "application/json")
            };
        });

        int attempts = 0;
        var status = await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
        {
            attempts++;
            var s = await client.GetStatusAsync(Base);
            if (!s.IsOnline) throw new HttpRequestException("offline");
            return s;
        }, maxRetries: 2, initialDelayMs: 1, operationName: nameof(client.GetStatusAsync));

        status.IsOnline.Should().BeTrue();
    attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_FailsAfterMaxRetries()
    {
        var (client, _, _) = CreateFlakyClient(i => throw new TaskCanceledException("timeout"));

        int attempts = 0;
        Func<Task> act = async () =>
        {
            await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
            {
                attempts++;
                var s = await client.GetStatusAsync(Base);
                if (!s.IsOnline) throw new Exception("offline");
            }, maxRetries: 2, initialDelayMs: 1, operationName: "StatusCheck");
        };

        await act.Should().ThrowAsync<Exception>();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task DirectoryCall_WithTransient5xx_RetriesAndSucceeds()
    {
        var (client, _, _) = CreateFlakyClient(i =>
        {
            if (i == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (i == 2)
            {
                // Simulate REST failure followed by JSON-RPC fallback success
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"rest parsing error\"}", Encoding.UTF8, "application/json")
                };
            }
            // Success on retry (assume REST now succeeds)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { result = new { path = "gcodes", dirs = Array.Empty<object>(), files = Array.Empty<object>(), size = 0, modified = 0 } }), Encoding.UTF8, "application/json")
            };
        });

        int attempts = 0;
        var dir = await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
        {
            attempts++;
            var d = await client.GetDirectoryAsync(Base, "gcodes");
            if (d is null) throw new HttpRequestException("dir null");
            return d;
        }, maxRetries: 2, initialDelayMs: 1, operationName: nameof(client.GetDirectoryAsync));

    dir.Should().NotBeNull();
    attempts.Should().Be(2);
    }
}
