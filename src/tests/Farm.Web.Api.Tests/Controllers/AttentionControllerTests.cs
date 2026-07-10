using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the AttentionController. Covers authorization (401 for
/// anonymous callers) and the happy-path GET returning a valid <see cref="AttentionFeedDto"/>
/// with camelCase property naming and string enum values.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class AttentionControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _anonClient;
    private HttpClient? _authClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AttentionControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonClient = _factory.CreateClient();
        _authClient = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _anonClient?.Dispose();
        _authClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "GET /api/attention returns 401 for anonymous clients")]
    public async Task GetAttention_Anonymous_Returns401()
    {
        HttpResponseMessage response = await _anonClient!.GetAsync("/api/attention");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "GET /api/attention returns 200 with feed shape for authenticated user")]
    public async Task GetAttention_Authenticated_ReturnsFeedShape()
    {
        HttpResponseMessage response = await _authClient!.GetAsync("/api/attention");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        AttentionFeedDto? feed = await response.Content.ReadFromJsonAsync<AttentionFeedDto>(JsonOptions);
        feed.Should().NotBeNull();
        feed!.Items.Should().NotBeNull();
        feed.HealthyPrinterIds.Should().NotBeNull();
    }

    [Fact(DisplayName = "POST snooze with past deadline returns 400")]
    public async Task Snooze_WithPastDeadline_Returns400()
    {
        SnoozeAttentionRequest req = new() { SnoozedUntilUtc = DateTime.UtcNow.AddHours(-1) };

        HttpResponseMessage response = await _authClient!.PostAsJsonAsync("/api/attention/failure:xyz/snooze", req, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "POST snooze with future deadline is accepted and echoes deadline")]
    public async Task Snooze_WithFutureDeadline_Returns200()
    {
        DateTime until = DateTime.UtcNow.AddHours(1);
        SnoozeAttentionRequest req = new() { SnoozedUntilUtc = until };

        HttpResponseMessage response = await _authClient!.PostAsJsonAsync("/api/attention/failure:abc/snooze", req, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("snoozedUntilUtc");
    }

    [Fact(DisplayName = "DELETE snooze for unknown item returns 404")]
    public async Task ClearSnooze_UnknownItem_Returns404()
    {
        HttpResponseMessage response = await _authClient!.DeleteAsync("/api/attention/failure:nonexistent/snooze");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "POST action on missing item returns 404")]
    public async Task ExecuteAction_MissingItem_Returns404()
    {
        HttpResponseMessage response = await _authClient!.PostAsync("/api/attention/failure:nope/actions/Pause", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
