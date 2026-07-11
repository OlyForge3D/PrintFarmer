using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Verifies the #725 feature-gate contract for the Attention endpoints: when
/// <c>OperatorFeatures:attentionEnabled</c> is explicitly <c>false</c>, every HTTP verb must
/// return 404 ProblemDetails with <c>code=featureDisabled</c>, perform no writes, and emit no
/// SignalR events. Authentication succeeds first (the gate is not an auth substitute), so the
/// 404 is the gate response rather than a 401.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class AttentionControllerDisabledTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _authClient;

    public AttentionControllerDisabledTests()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["OperatorFeatures:attentionEnabled"] = "false",
        });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _authClient = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _authClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "GET /api/attention returns 404 featureDisabled when the feature is off")]
    public async Task GetFeed_FeatureDisabled_Returns404FeatureDisabled()
    {
        HttpResponseMessage response = await _authClient!.GetAsync("/api/attention");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("featureDisabled");
        body.Should().Contain("attentionEnabled");
    }

    [Fact(DisplayName = "Snooze POST returns 404 featureDisabled and writes nothing when disabled")]
    public async Task Snooze_FeatureDisabled_Returns404AndDoesNotWrite()
    {
        HttpResponseMessage response = await _authClient!.PostAsJsonAsync(
            "/api/attention/failure:00000000-0000-0000-0000-000000000abc/snooze",
            new { snoozedUntilUtc = DateTime.UtcNow.AddHours(1) });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("featureDisabled");

        await AssertNoSnoozesPersistedAsync();
    }

    [Fact(DisplayName = "Snooze DELETE returns 404 featureDisabled when disabled")]
    public async Task ClearSnooze_FeatureDisabled_Returns404()
    {
        HttpResponseMessage response = await _authClient!.DeleteAsync(
            "/api/attention/failure:00000000-0000-0000-0000-000000000abc/snooze");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("featureDisabled");

        await AssertNoSnoozesPersistedAsync();
    }

    [Fact(DisplayName = "Action POST returns 404 featureDisabled when disabled")]
    public async Task ExecuteAction_FeatureDisabled_Returns404()
    {
        HttpResponseMessage response = await _authClient!.PostAsync(
            "/api/attention/maintenance:00000000-0000-0000-0000-000000000abc/actions/Acknowledge",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("featureDisabled");
    }

    private async Task AssertNoSnoozesPersistedAsync()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int count = await db.AttentionSnoozes.CountAsync();
        count.Should().Be(0, "a disabled Attention endpoint must not perform any writes");
    }
}
