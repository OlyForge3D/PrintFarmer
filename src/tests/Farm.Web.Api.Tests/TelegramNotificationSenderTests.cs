using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class TelegramNotificationSenderTests
{
    [Fact]
    public async Task SendMessageAsync_WhenTelegramAcceptsRequest_PostsSendMessagePayload()
    {
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var factory = CreateFactory(handler);
        var logger = new CapturingLogger<TelegramNotificationSender>();
        var sender = new TelegramNotificationSender(factory.Object, logger, []);

        TelegramDispatchResult result = await sender.SendMessageAsync(
            "123456:ABC",
            "987654",
            "Print completed",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        CapturedRequest request = handler.Requests[0];
        request.Uri.Should().Be("https://api.telegram.org/bot123456:ABC/sendMessage");
        using JsonDocument json = JsonDocument.Parse(request.Body);
        json.RootElement.GetProperty("chat_id").GetString().Should().Be("987654");
        json.RootElement.GetProperty("text").GetString().Should().Be("Print completed");
    }

    [Fact]
    public async Task SendPhotoAsync_WhenPhotoProvided_PostsSendPhotoMultipartPayload()
    {
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var factory = CreateFactory(handler);
        var sender = new TelegramNotificationSender(factory.Object, new CapturingLogger<TelegramNotificationSender>(), []);

        TelegramDispatchResult result = await sender.SendPhotoAsync(
            "123456:ABC",
            "987654",
            "Print completed",
            [1, 2, 3],
            "image/jpeg",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        CapturedRequest request = handler.Requests.Single();
        request.Uri.Should().Be("https://api.telegram.org/bot123456:ABC/sendPhoto");
        request.ContentType.Should().StartWith("multipart/form-data");
        request.Body.Should().Contain("chat_id");
        request.Body.Should().Contain("987654");
        request.Body.Should().Contain("photo");
    }

    [Fact]
    public async Task SendMessageAsync_WhenTransientFailureOccurs_RetriesAndSucceeds()
    {
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.OK));
        var factory = CreateFactory(handler);
        var sender = new TelegramNotificationSender(
            factory.Object,
            new CapturingLogger<TelegramNotificationSender>(),
            [TimeSpan.Zero]);

        TelegramDispatchResult result = await sender.SendMessageAsync(
            "123456:ABC",
            "987654",
            "Print completed",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendMessageAsync_WhenDeliveryFails_DoesNotLogBotToken()
    {
        const string token = "123456:SECRET-TOKEN";
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var factory = CreateFactory(handler);
        var logger = new CapturingLogger<TelegramNotificationSender>();
        var sender = new TelegramNotificationSender(factory.Object, logger, [TimeSpan.Zero]);

        TelegramDispatchResult result = await sender.SendMessageAsync(
            token,
            "987654",
            "Print completed",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        logger.Messages.Any(message => message.Contains(token, StringComparison.Ordinal))
            .Should()
            .BeFalse();
        logger.Messages.Any(message => message.Contains("SECRET-TOKEN", StringComparison.Ordinal))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task SendMessageAsync_WhenExceptionMentionsRequestUri_DoesNotReturnOrLogBotToken()
    {
        const string token = "123456:SECRET-TOKEN";
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException($"Request failed for https://api.telegram.org/bot{token}/sendMessage"));
        var factory = CreateFactory(handler);
        var logger = new CapturingLogger<TelegramNotificationSender>();
        var sender = new TelegramNotificationSender(factory.Object, logger, []);

        TelegramDispatchResult result = await sender.SendMessageAsync(
            token,
            "987654",
            "Print completed",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain(token);
        result.Error.Should().NotContain("SECRET-TOKEN");
        logger.Messages.Any(message => message.Contains(token, StringComparison.Ordinal))
            .Should()
            .BeFalse();
        logger.Messages.Any(message => message.Contains("SECRET-TOKEN", StringComparison.Ordinal))
            .Should()
            .BeFalse();
    }

    private static Mock<IHttpClientFactory> CreateFactory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("TelegramDelivery"))
            .Returns(() => new HttpClient(handler));
        return factory;
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
            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                body));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(string Uri, string ContentType, string Body);

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.Message);
            }
        }
    }
}
