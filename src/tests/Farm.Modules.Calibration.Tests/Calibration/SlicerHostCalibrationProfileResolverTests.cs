using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Calibration.Tests.Calibration;

/// <summary>
/// Behaviour of the split-deployment resolver adapter: it must forward the end user's own bearer
/// token, stay inside its bounds, never leak the token or the internal address, and turn every
/// dependency failure into the stable unavailable signal.
/// </summary>
public sealed class SlicerHostCalibrationProfileResolverTests
{
    private const string SlicerHostBaseUrl = "http://slicer-host.internal:5246/";
    private const string BearerToken = "test.bearer.token-value-that-must-never-be-logged";
    private const string EmptyProfilesPayload = """{"machine":null,"process":null,"filament":null}""";

    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProcessId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FilamentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task ResolveAsync_ForwardsEndUserBearerTokenToTheFixedRelativeRoute()
    {
        RecordingHandler handler = new(Responses.Json(EmptyProfilesPayload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        _ = await resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            new CalibrationProfileAccessScope(Guid.NewGuid(), BypassOwnership: true),
            CancellationToken.None);

        RecordedRequest request = handler.Requests.Should().ContainSingle().Subject;
        _ = request.Method.Should().Be(HttpMethod.Post);
        _ = request.Uri.Should().Be(
            new Uri(new Uri(SlicerHostBaseUrl), CalibrationProfileResolutionContract.ResolveRelativeRoute));
        _ = request.AuthorizationScheme.Should().Be("Bearer");
        _ = request.AuthorizationParameter.Should().Be(BearerToken);
    }

    [Fact]
    public async Task ResolveAsync_SendsExactlyTheThreeIdentifiersAndNoCallerScope()
    {
        RecordingHandler handler = new(Responses.Json(EmptyProfilesPayload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        _ = await resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,

            // A caller-supplied bypass must not travel over the wire; the slicer host re-derives it.
            new CalibrationProfileAccessScope(Guid.NewGuid(), BypassOwnership: true),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Requests.Single().Body);
        _ = body.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(CalibrationProfileResolutionContract.RequiredProperties);
        _ = body.RootElement.GetProperty("machineProfileId").GetGuid().Should().Be(MachineId);
        _ = body.RootElement.GetProperty("processProfileId").GetGuid().Should().Be(ProcessId);
        _ = body.RootElement.GetProperty("filamentProfileId").GetGuid().Should().Be(FilamentId);
        _ = CalibrationProfileResolutionContract.TryParseRequest(body.RootElement, out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NeverLogsTheForwardedTokenOrTheInternalAddress()
    {
        RecordingHandler handler = new(Responses.Status(HttpStatusCode.Unauthorized));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out CapturingLogger logger);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>();
        _ = logger.Messages.Should().NotBeEmpty();
        _ = logger.Messages.Should().NotContain(message =>
            message.Contains(BearerToken, StringComparison.Ordinal));
        _ = logger.Messages.Should().NotContain(message =>
            message.Contains("slicer-host.internal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_WithoutForwardableToken_FailsClosedWithoutCallingTheSlicerHost()
    {
        RecordingHandler handler = new(Responses.Json(EmptyProfilesPayload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(
            handler,
            out CapturingLogger logger,
            authorizationHeader: null);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        CalibrationProfileResolverUnavailableException exception =
            (await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>())
            .Which;
        _ = exception.ErrorCode.Should().Be("profile_service_authentication_failed");
        _ = handler.Requests.Should().BeEmpty();
        _ = logger.Messages.Should().NotContain(message =>
            message.Contains(BearerToken, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    public async Task ResolveAsync_WithNonBearerAuthorization_FailsClosed(string authorizationHeader)
    {
        RecordingHandler handler = new(Responses.Json(EmptyProfilesPayload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(
            handler,
            out _,
            authorizationHeader);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>();
        _ = handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WithMissingProfiles_ReturnsNullProfilesRatherThanFailing()
    {
        RecordingHandler handler = new(Responses.Json(EmptyProfilesPayload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        ResolvedCalibrationProfiles resolved = await resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = resolved.Machine.Should().BeNull();
        _ = resolved.Process.Should().BeNull();
        _ = resolved.Filament.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_WithResolvedProfiles_PreservesTheCredentialFreeContract()
    {
        string payload = JsonSerializer.Serialize(
            new ResolvedCalibrationProfiles(
                new ResolvedCalibrationProfile(
                    MachineId,
                    "machine",
                    "Test Machine",
                    "OrcaSlicer",
                    "upstream",
                    "2.4.0",
                    "orca-json",
                    new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    """{"gcode_flavor":"klipper"}""",
                    "abc123",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "Test",
                    null),
                null,
                null),
            CalibrationProfileResolutionContract.SerializerOptions);
        RecordingHandler handler = new(Responses.Json(payload));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        ResolvedCalibrationProfiles resolved = await resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = resolved.Machine.Should().NotBeNull();
        _ = resolved.Machine!.Id.Should().Be(MachineId);
        _ = resolved.Machine.Name.Should().Be("Test Machine");
        _ = resolved.Machine.RawJson.Should().Contain("klipper");
        _ = resolved.Process.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "profile_service_authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "profile_service_authorization_failed")]
    [InlineData(HttpStatusCode.NotFound, "profile_service_configuration_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "profile_service_unavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "profile_service_unavailable")]
    [InlineData(HttpStatusCode.BadRequest, "profile_service_configuration_error")]
    public async Task ResolveAsync_WithRefusedResponse_ReturnsTypedFailure(
        HttpStatusCode statusCode,
        string expectedErrorCode)
    {
        RecordingHandler handler = new(Responses.Status(statusCode));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        CalibrationProfileResolverUnavailableException exception =
            (await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>())
            .Which;
        _ = exception.ErrorCode.Should().Be(expectedErrorCode);
    }

    [Fact]
    public async Task ResolveAsync_WithMalformedDocument_ReportsResolverUnavailable()
    {
        RecordingHandler handler = new(Responses.Json("{\"machine\": "));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        CalibrationProfileResolverUnavailableException exception =
            (await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>())
            .Which;
        _ = exception.ErrorCode.Should().Be("profile_service_unavailable");
    }

    [Fact]
    public async Task ResolveAsync_WithUnexpectedMembers_ReportsResolverUnavailable()
    {
        RecordingHandler handler = new(Responses.Json(
            """{"machine":null,"process":null,"filament":null,"internalConnectionString":"secret"}"""));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        CalibrationProfileResolverUnavailableException exception =
            (await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>())
            .Which;
        _ = exception.ErrorCode.Should().Be("profile_service_unavailable");
    }

    [Fact]
    public async Task ResolveAsync_WithNonJsonMediaType_ReportsResolverUnavailable()
    {
        RecordingHandler handler = new(
            Responses.Status(HttpStatusCode.OK, "<html>login</html>", "text/html"));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_WithOversizedResponse_ReportsResolverUnavailable()
    {
        string oversized =
            $$"""{"machine":null,"process":null,"filament":null,"padding":"{{new string('a', 8192)}}"}""";
        RecordingHandler handler = new(Responses.Json(oversized));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(
            handler,
            out _,
            options: CreateOptions(maxResponseBytes: 2048));

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_WhenSlicerHostStalls_TimesOutAsResolverUnavailable()
    {
        RecordingHandler handler = new(Responses.Stalled());
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(
            handler,
            out _,
            options: CreateOptions(resolveTimeout: TimeSpan.FromMilliseconds(150)));

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        CalibrationProfileResolverUnavailableException exception =
            (await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>())
            .Which;
        _ = exception.ErrorCode.Should().Be("profile_service_timeout");
    }

    [Fact]
    public async Task ResolveAsync_WhenCallerCancels_PropagatesCancellation()
    {
        RecordingHandler handler = new(Responses.Stalled());
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            callerCancellation.Token);

        _ = await resolve.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IsAvailableAsync_WithHealthyProbe_ReturnsTrueAndSendsNoEndUserToken()
    {
        RecordingHandler handler = new(Responses.Status(HttpStatusCode.OK, "Healthy"));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        bool available = await resolver.IsAvailableAsync(CancellationToken.None);

        _ = available.Should().BeTrue();
        RecordedRequest request = handler.Requests.Should().ContainSingle().Subject;
        _ = request.Method.Should().Be(HttpMethod.Get);
        _ = request.Uri.Should().Be(
            new Uri(new Uri(SlicerHostBaseUrl), CalibrationProfileResolutionContract.HealthRelativeRoute));
        _ = request.AuthorizationScheme.Should().BeNull();
        _ = request.Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Degraded")]
    [InlineData(HttpStatusCode.OK, "Unhealthy")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task IsAvailableAsync_WithAnythingButHealthy_FailsClosed(
        HttpStatusCode statusCode,
        string body)
    {
        RecordingHandler handler = new(Responses.Status(statusCode, body));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        bool available = await resolver.IsAvailableAsync(CancellationToken.None);

        _ = available.Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_WhenTransportFails_FailsClosed()
    {
        RecordingHandler handler = new(Responses.Faulted(new HttpRequestException("connection refused")));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out CapturingLogger logger);

        bool available = await resolver.IsAvailableAsync(CancellationToken.None);

        _ = available.Should().BeFalse();
        _ = logger.Messages.Should().NotContain(message =>
            message.Contains("slicer-host.internal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IsAvailableAsync_WhenResponseBodyIsTruncated_FailsClosed()
    {
        // A reset or truncated body surfaces as HttpIOException, which is an IOException and not an
        // HttpRequestException. The public capability document must still degrade, not throw.
        RecordingHandler handler = new(Responses.Faulted(
            new HttpIOException(HttpRequestError.ResponseEnded, "response ended prematurely")));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out _);

        bool available = await resolver.IsAvailableAsync(CancellationToken.None);

        _ = available.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_WhenResponseBodyIsTruncated_ReportsResolverUnavailable()
    {
        RecordingHandler handler = new(Responses.Faulted(
            new HttpIOException(HttpRequestError.ResponseEnded, "response ended prematurely")));
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(handler, out CapturingLogger logger);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MachineId,
            ProcessId,
            FilamentId,
            AnonymousScope,
            CancellationToken.None);

        _ = await resolve.Should().ThrowAsync<CalibrationProfileResolverUnavailableException>();
        _ = logger.Messages.Should().NotContain(message =>
            message.Contains(BearerToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsAvailableAsync_WhenProbeStalls_FailsClosed()
    {
        RecordingHandler handler = new(Responses.Stalled());
        SlicerHostCalibrationProfileResolver resolver = CreateResolver(
            handler,
            out _,
            options: CreateOptions(healthTimeout: TimeSpan.FromMilliseconds(150)));

        bool available = await resolver.IsAvailableAsync(CancellationToken.None);

        _ = available.Should().BeFalse();
    }

    private static CalibrationProfileAccessScope AnonymousScope => new(null, false);

    private static SlicerHostCalibrationResolverOptions CreateOptions(
        TimeSpan? resolveTimeout = null,
        TimeSpan? healthTimeout = null,
        int maxResponseBytes = 8 * 1024 * 1024) =>
        new()
        {
            BaseUrl = new Uri(SlicerHostBaseUrl),
            ResolveTimeout = resolveTimeout ?? TimeSpan.FromSeconds(10),
            HealthTimeout = healthTimeout ?? TimeSpan.FromSeconds(5),
            MaxResponseBytes = maxResponseBytes,
        };

    private static SlicerHostCalibrationProfileResolver CreateResolver(
        RecordingHandler handler,
        out CapturingLogger logger,
        string? authorizationHeader = "Bearer " + BearerToken,
        SlicerHostCalibrationResolverOptions? options = null)
    {
        options ??= CreateOptions();
        HttpClient client = new(handler) { BaseAddress = options.BaseUrl };
        DefaultHttpContext httpContext = new();
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }

        logger = new CapturingLogger();
        return new SlicerHostCalibrationProfileResolver(
            client,
            new HttpContextAccessor { HttpContext = httpContext },
            options,
            logger);
    }

    private static class Responses
    {
        public static Func<CancellationToken, Task<HttpResponseMessage>> Json(string payload) =>
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });

        public static Func<CancellationToken, Task<HttpResponseMessage>> Status(
            HttpStatusCode statusCode,
            string? body = null,
            string mediaType = "text/plain") =>
            _ => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body ?? string.Empty, Encoding.UTF8, mediaType),
            });

        public static Func<CancellationToken, Task<HttpResponseMessage>> Stalled() =>
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        public static Func<CancellationToken, Task<HttpResponseMessage>> Faulted(Exception exception) =>
            _ => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);

    private sealed class RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthenticationHeaderValue? authorization = request.Headers.Authorization;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                authorization?.Scheme,
                authorization?.Parameter,
                body));
            return await responder(cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<SlicerHostCalibrationProfileResolver>
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception) + " " + exception);
        }
    }
}
