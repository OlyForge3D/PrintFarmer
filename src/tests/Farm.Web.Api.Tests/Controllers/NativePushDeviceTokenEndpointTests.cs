using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
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
                token = new string('a', 258),
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

    [Fact]
    public async Task RegisterThenUnregister_CanonicalMaximumBounds_PersistsAndRemovesExactValues()
    {
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);
        string installationId = "i" + new string('x', 127);
        string token = new string('a', 256);
        string bundleId = new string('b', 256);

        HttpResponseMessage registered = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId,
                token,
                platform = "ios",
                environment = "development",
                appBundleId = bundleId,
            });

        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        DeviceTokenRegistrationResponse? registrationBody =
            await registered.Content.ReadFromJsonAsync<DeviceTokenRegistrationResponse>();
        registrationBody.Should().NotBeNull();
        registrationBody!.ServerId.Should().NotBe(Guid.Empty);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.DeviceTokens.AsNoTracking().SingleAsync();
            row.InstallationId.Should().Be(installationId);
            row.Token.Should().Be(token);
            row.Platform.Should().Be("ios");
            row.Environment.Should().Be("development");
            row.AppBundleId.Should().Be(bundleId);
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/notifications/device-tokens")
        {
            Content = JsonContent.Create(new { installationId }),
        };
        HttpResponseMessage unregistered = await client.SendAsync(delete);

        unregistered.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.DeviceTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisterDeviceTokenAsync_MultipleRegistrations_ReturnStableServerId()
    {
        // Issue #1407: serverId is this server's own persisted, generated identity — it
        // must be stable across separate registration calls (i.e. the same underlying
        // ServerIdentityService instance/store), not re-derived per request.
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);

        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId = "installation-a",
                token = new string('a', 64),
                platform = "ios",
                environment = "production",
                appBundleId = "com.example.app",
            });
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId = "installation-b",
                token = new string('b', 64),
                platform = "ios",
                environment = "production",
                appBundleId = "com.example.app",
            });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        DeviceTokenRegistrationResponse? firstBody =
            await first.Content.ReadFromJsonAsync<DeviceTokenRegistrationResponse>();
        DeviceTokenRegistrationResponse? secondBody =
            await second.Content.ReadFromJsonAsync<DeviceTokenRegistrationResponse>();
        firstBody.Should().NotBeNull();
        secondBody.Should().NotBeNull();
        firstBody!.ServerId.Should().NotBe(Guid.Empty);
        secondBody!.ServerId.Should().Be(firstBody.ServerId);
    }

    [Theory]
    [InlineData("short-token")]
    [InlineData("odd-token")]
    [InlineData("uppercase-token")]
    [InlineData("bad-platform")]
    [InlineData("bad-environment")]
    [InlineData("bad-installation")]
    public async Task Register_NonCanonicalIdentifierOrToken_Returns400WithoutMutation(string scenario)
    {
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);
        string installationId = "installation-1";
        string token = new string('a', 64);
        string platform = "ios";
        string environment = "production";

        switch (scenario)
        {
            case "short-token":
                token = new string('a', 62);
                break;
            case "odd-token":
                token = new string('a', 65);
                break;
            case "uppercase-token":
                token = new string('A', 64);
                break;
            case "bad-platform":
                platform = "IOS";
                break;
            case "bad-environment":
                environment = "staging";
                break;
            case "bad-installation":
                installationId = " installation-1";
                break;
        }

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new { installationId, token, platform, environment });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DeviceTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unregister_OversizedInstallationId_Returns400WithoutDeletingRegistration()
    {
        await using var factory = new CustomWebApplicationFactory();
        HttpClient client = await CreateNativePushClientAsync(factory);
        HttpResponseMessage registered = await client.PostAsJsonAsync(
            "/api/notifications/device-tokens",
            new
            {
                installationId = "installation-1",
                token = new string('a', 64),
                platform = "ios",
                environment = "production",
            });
        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/notifications/device-tokens")
        {
            Content = JsonContent.Create(new { installationId = new string('x', 129) }),
        };

        HttpResponseMessage response = await client.SendAsync(delete);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DeviceTokens.CountAsync()).Should().Be(1);
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
