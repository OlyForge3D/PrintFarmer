using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Resilience;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Tests.TestUtils;
using FluentAssertions.Specialized;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

public class MoonrakerClientRetryTests
{
    private const string Base = "http://printer";

    private static (IMoonrakerClient client, Mock<HttpMessageHandler> handler, int attempts) CreateFlakyClient(Func<int, HttpResponseMessage> responder)
    {
        int attempt = 0;
        Mock<HttpMessageHandler> handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _ = handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                attempt++;
                return responder(attempt);
            });
#pragma warning disable CA2000 // Dispose objects before losing scope - HttpClient is owned by the test client for test lifetime
        HttpClient http = new HttpClient(handler.Object);
#pragma warning restore CA2000
        return (new MoonrakerClient(http, new TestLoggingService()), handler, attempt);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnThirdAttemptAsync()
    {
        (IMoonrakerClient? client, _, _) = CreateFlakyClient(i =>
        {
            if (i < 3)
            {
                throw new HttpRequestException("boom");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { result = new { state = "ready" } }), Encoding.UTF8, "application/json")
            };
        });

        int attempts = 0;
        PrinterStatus status = await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
        {
            attempts++;
            PrinterStatus s = await client.GetStatusAsync(Base);
            if (!s.IsOnline)
            {
                throw new HttpRequestException("offline");
            }

            return s;
        }, maxRetries: 2, initialDelayMs: 1, operationName: nameof(client.GetStatusAsync));

        _ = status.IsOnline.Should().BeTrue();
        _ = attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_FailsAfterMaxRetriesAsync()
    {
        (IMoonrakerClient? client, _, _) = CreateFlakyClient(i => throw new TaskCanceledException("timeout"));

        int attempts = 0;
        Func<Task> act = async () =>
        {
            await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
            {
                attempts++;
                PrinterStatus s = await client.GetStatusAsync(Base);
                if (!s.IsOnline)
                {
                    throw new HttpRequestException("offline");
                }
            }, maxRetries: 2, initialDelayMs: 1, operationName: "StatusCheck");
        };

        // Current RetryPolicyHelper wraps the final failure in InvalidOperationException when max retries are exceeded.
        // The underlying transient exception we simulate is TaskCanceledException (e.g. HTTP timeout). We assert on the
        // wrapper type and inner exception to prevent losing signal if RetryPolicyHelper implementation changes later.
        ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        _ = thrown.Which.InnerException.Should().BeOfType<TaskCanceledException>();
        _ = attempts.Should().Be(3); // initial try + 2 retries
    }

    [Fact]
    public async Task DirectoryCall_WithTransient5xx_RetriesAndSucceedsAsync()
    {
        (IMoonrakerClient? client, _, _) = CreateFlakyClient(i =>
        {
            if (i == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

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
        MoonrakerDirectoryInfo dir = await RetryPolicyHelper.ExecuteWithRetryAsync(async () =>
        {
            attempts++;
            MoonrakerDirectoryInfo? d = await client.GetDirectoryAsync(Base, "gcodes");
            if (d is null)
            {
                throw new HttpRequestException("dir null");
            }

            return d;
        }, maxRetries: 2, initialDelayMs: 1, operationName: nameof(client.GetDirectoryAsync));

        _ = dir.Should().NotBeNull();
        _ = attempts.Should().Be(2);
    }
}
