using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class NotificationPreferencesHttpBoundaryTests
{
    [Theory]
    [InlineData(33, 12)]
    [InlineData(1, 65)]
    public async Task AttentionPreferences_InvalidRequestBounds_Return400WithoutMutation(
        int keyCount,
        int keyLength)
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateNativePushClientAsync(factory, "invalid-bound");
        Dictionary<string, bool> categories = Enumerable.Range(0, keyCount)
            .ToDictionary(index => $"k{index:D2}".PadRight(keyLength, 'x'), index => index % 2 == 0);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/notifications/attention-push-preferences",
            new { categories });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.NotificationPreferences.CountAsync(p => p.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task AttentionPreferences_ExactRequestBounds_Return200AndPersist()
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateNativePushClientAsync(factory, "exact-bound");
        Dictionary<string, bool> categories = Enumerable.Range(0, 32)
            .ToDictionary(
                index => $"k{index:D2}-".PadRight(64, 'x'),
                index => index % 2 == 0);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/notifications/attention-push-preferences",
            new { categories });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        NotificationPreferences persisted = await ReadPreferencesAsync(factory, userId);
        AttentionPushCategoryPreferences.FromJson(persisted.AttentionPushCategoryPreferencesJson)
            .Categories.Should().BeEquivalentTo(categories);
    }

    [Fact]
    public async Task AttentionPreferences_OneHundredTwentyNinthUniqueKey_Return400WithoutMutation()
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateNativePushClientAsync(factory, "cumulative-bound");

        for (int index = 0; index < 128; index++)
        {
            HttpResponseMessage accepted = await client.PutAsJsonAsync(
                "/api/notifications/attention-push-preferences",
                new { categories = new Dictionary<string, bool> { [$"key-{index:D3}"] = true } });
            accepted.StatusCode.Should().Be(HttpStatusCode.OK, $"key {index + 1} is within the cumulative bound");
        }

        string before = (await ReadPreferencesAsync(factory, userId)).AttentionPushCategoryPreferencesJson!;
        HttpResponseMessage rejected = await client.PutAsJsonAsync(
            "/api/notifications/attention-push-preferences",
            new { categories = new Dictionary<string, bool> { ["key-128"] = true } });

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadPreferencesAsync(factory, userId)).AttentionPushCategoryPreferencesJson
            .Should().Be(before);
    }

    [Fact]
    public async Task AttentionPreferences_MultibyteOverflow_Return400WithoutMutation()
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateNativePushClientAsync(factory, "unicode-bound");

        int keysAdded = 0;
        while (keysAdded < 114)
        {
            int batchSize = Math.Min(32, 114 - keysAdded);
            Dictionary<string, bool> batch = Enumerable.Range(keysAdded, batchSize)
                .ToDictionary(index => $"kb-{index:D3}-" + new string('x', 55), _ => false);
            HttpResponseMessage accepted = await client.PutAsJsonAsync(
                "/api/notifications/attention-push-preferences",
                new { categories = batch });
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
            keysAdded += batchSize;
        }

        string before = (await ReadPreferencesAsync(factory, userId)).AttentionPushCategoryPreferencesJson!;
        Encoding.UTF8.GetByteCount(before).Should().BeLessThan(8 * 1024);
        string unicodeKey = new('\uFFFD', 64);

        HttpResponseMessage rejected = await client.PutAsJsonAsync(
            "/api/notifications/attention-push-preferences",
            new { categories = new Dictionary<string, bool> { [unicodeKey] = true } });

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadPreferencesAsync(factory, userId)).AttentionPushCategoryPreferencesJson
            .Should().Be(before);
    }

    [Fact]
    public async Task Preferences_FullNineTokenPut_Return200AndPersistsEveryRow()
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateAuthenticatedClientAsync(factory, "all-nine");
        string[] tokens =
        [
            "JobStarted", "JobCompleted", "JobFailed", "JobPaused", "PrinterFailure",
            "FilamentRunout", "HarvestReady", "MaintenanceDue", "PrinterOffline",
        ];
        object[] rows = tokens.Select(token => new
        {
            eventType = token,
            inApp = true,
            email = true,
            push = false,
            telegram = true,
        }).Cast<object>().ToArray();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/notifications/preferences",
            new { eventChannelPreferences = rows });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        NotificationPreferences persisted = await ReadPreferencesAsync(factory, userId);
        foreach (string token in tokens)
        {
            typeof(NotificationPreferences).GetProperty($"InAppOn{token}")!.GetValue(persisted)
                .Should().Be(true);
            typeof(NotificationPreferences).GetProperty($"EmailOn{token}")!.GetValue(persisted)
                .Should().Be(true);
            typeof(NotificationPreferences).GetProperty($"PushOn{token}")!.GetValue(persisted)
                .Should().Be(false);
            typeof(NotificationPreferences).GetProperty($"TelegramOn{token}")!.GetValue(persisted)
                .Should().Be(true);
        }
    }

    [Fact]
    public async Task Preferences_UnknownEnum_Return400WithoutMutation()
    {
        await using var factory = new CustomWebApplicationFactory();
        (HttpClient client, Guid userId) = await CreateAuthenticatedClientAsync(factory, "unknown-enum");
        await using (AsyncServiceScope seedScope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NotificationPreferences.Add(new NotificationPreferences
            {
                UserId = userId,
                RetentionDays = 47,
                AttentionPushCategoryPreferencesJson = "{\"categories\":{\"failure\":false}}",
                EmailOnHarvestReady = true,
            });
            await db.SaveChangesAsync();
        }

        NotificationPreferences before = await ReadPreferencesAsync(factory, userId);
        string rawJson = """
            {
              "retentionDays": 1,
              "eventChannelPreferences": [{
                "eventType": "FutureUnknownEvent",
                "inApp": false,
                "email": false,
                "push": false,
                "telegram": false
              }]
            }
            """;
        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NotificationPreferences after = await ReadPreferencesAsync(factory, userId);
        after.RetentionDays.Should().Be(before.RetentionDays);
        after.AttentionPushCategoryPreferencesJson.Should().Be(before.AttentionPushCategoryPreferencesJson);
        after.EmailOnHarvestReady.Should().Be(before.EmailOnHarvestReady);
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateNativePushClientAsync(
        CustomWebApplicationFactory factory,
        string prefix)
    {
        (HttpClient client, Guid userId) = await CreateAuthenticatedClientAsync(factory, prefix);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IAppSettingsRepository settings = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        string json = JsonSerializer.Serialize(new OperatorFeatureSettings { NativePushEnabled = true });
        await settings.SetAsync(OperatorFeatureSettings.SectionName, json);
        await settings.SaveChangesAsync();
        return (client, userId);
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateAuthenticatedClientAsync(
        CustomWebApplicationFactory factory,
        string prefix)
    {
        string username = $"{prefix}-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(
            username,
            $"{username}@example.com");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid userId = await db.Users
            .Where(user => user.Username == username)
            .Select(user => user.Id)
            .SingleAsync();
        return (client, userId);
    }

    private static async Task<NotificationPreferences> ReadPreferencesAsync(
        CustomWebApplicationFactory factory,
        Guid userId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
    }
}
