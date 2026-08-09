using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class MailjetEmailServiceTests
{
    [Fact]
    public async Task SendAsync_MissingApiKeys_FallsBackToLoggingWithoutCallingHttpClient()
    {
        var options = new EmailOptions { Mailjet = new MailjetOptions() };
        var handler = new QueueHttpMessageHandler();
        var service = CreateService(options, handler);

        EmailDispatchResult result = await service.SendAsync(new EmailMessage("user@example.com", "Subject"));

        result.Success.Should().BeTrue();
        result.ProviderMessage.Should().Be("Missing API keys - logged only");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_WithSandboxEnabled_IncludesSandboxModeInPayload()
    {
        var options = new EmailOptions
        {
            FromAddress = "noreply@example.com",
            FromName = "PrintFarmer Test",
            Mailjet = new MailjetOptions
            {
                ApiKey = "key",
                ApiSecret = "secret",
                Sandbox = true
            }
        };
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(options, handler);

        EmailDispatchResult result = await service.SendAsync(new EmailMessage("user@example.com", "Subject"));

        result.Success.Should().BeTrue();
        CapturedRequest request = handler.Requests.Single();
        using JsonDocument json = JsonDocument.Parse(request.Body);
        json.RootElement.GetProperty("SandboxMode").GetBoolean().Should().BeTrue();
        JsonElement fromElement = json.RootElement.GetProperty("Messages")[0].GetProperty("From");
        fromElement.GetProperty("Email").GetString().Should().Be("noreply@example.com");
        fromElement.GetProperty("Name").GetString().Should().Be("PrintFarmer Test");
    }

    [Fact]
    public async Task SendAsync_WithoutTopLevelFromAddress_FallsBackToMailjetFromEmail()
    {
        var options = new EmailOptions
        {
            Mailjet = new MailjetOptions
            {
                ApiKey = "key",
                ApiSecret = "secret",
                FromEmail = "legacy@example.com",
                FromName = "Legacy Sender"
            }
        };
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(options, handler);

        await service.SendAsync(new EmailMessage("user@example.com", "Subject"));

        CapturedRequest request = handler.Requests.Single();
        using JsonDocument json = JsonDocument.Parse(request.Body);
        JsonElement fromElement = json.RootElement.GetProperty("Messages")[0].GetProperty("From");
        fromElement.GetProperty("Email").GetString().Should().Be("legacy@example.com");
        fromElement.GetProperty("Name").GetString().Should().Be("Legacy Sender");
        json.RootElement.GetProperty("SandboxMode").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenMailjetReturnsError_ReturnsFailureResult()
    {
        var options = new EmailOptions
        {
            Mailjet = new MailjetOptions { ApiKey = "key", ApiSecret = "secret" }
        };
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized")
        });
        var service = CreateService(options, handler);

        EmailDispatchResult result = await service.SendAsync(new EmailMessage("user@example.com", "Subject"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unauthorized");
    }

    private static MailjetEmailService CreateService(EmailOptions options, HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("Mailjet")).Returns(() => new HttpClient(handler));
        var renderer = new EmailTemplateRenderer();
        return new MailjetEmailService(
            new CapturingLogger<MailjetEmailService>(),
            options,
            renderer,
            factory.Object);
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(body));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(string Body);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
