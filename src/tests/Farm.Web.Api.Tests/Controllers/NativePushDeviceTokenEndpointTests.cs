using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class NativePushDeviceTokenEndpointTests
{
    [Fact]
    public async Task RegisterDeviceTokenAsync_OversizedToken_ReturnsProblemDetailsWithoutPersistingRow()
    {
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId = "installation-1",
                token = new string('a', 4097),
                platform = "ios",
                environment = "production",
                appBundleId = "com.example.app",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DeviceTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisterDeviceTokenAsync_NonCanonicalBundleId_ReturnsProblemDetailsWithoutPersistingRow()
    {
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId = "installation-1",
                token = new string('a', 64),
                platform = "ios",
                environment = "production",
                appBundleId = "Com.Example.App",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DeviceTokens.CountAsync()).Should().Be(0);
    }

    private static async Task<HttpClient> CreateNativePushClientAsync(CustomWebApplicationFactory factory)
    {
        HttpClient client = await factory.CreateAuthenticatedClientAsync(
            username: $"native-push-{Guid.NewGuid():N}",
            email: $"native-push-{Guid.NewGuid():N}@example.com");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IAppSettingsRepository settings = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        string json = JsonSerializer.Serialize(new OperatorFeatureSettings { NativePushEnabled = true });
        await settings.SetAsync(OperatorFeatureSettings.SectionName, json);
        await settings.SaveChangesAsync();
        return client;
    }
}
