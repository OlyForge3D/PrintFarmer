using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class NotificationPreferencesHttpRegressionTests
{
    private static readonly PropertyInfo[] AttentionProperties = typeof(NotificationPreferences)
        .GetProperties()
        .Where(property =>
            property.PropertyType == typeof(bool)
            && (property.Name.StartsWith("InAppOn", StringComparison.Ordinal)
                || property.Name.StartsWith("EmailOn", StringComparison.Ordinal)
                || property.Name.StartsWith("PushOn", StringComparison.Ordinal)
                || property.Name.StartsWith("TelegramOn", StringComparison.Ordinal)))
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"retentionDays\":30}")]
    public async Task UpdatePreferencesAsync_OmittedChannelMasters_PreservesEveryAttentionRow(string body)
    {
        await using var factory = new CustomWebApplicationFactory();
        string username = $"preference-omitted-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(
            username,
            $"{username}@example.com");
        Guid userId = await GetUserIdAsync(factory, username);

        Dictionary<string, bool> before;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preferences = new NotificationPreferences
            {
                UserId = userId,
                RetentionDays = 14,
                EnableEmailNotifications = false,
                EnablePushNotifications = false,
                EnableTelegramNotifications = false,
                EnableInAppNotifications = false,
                NotifyOnStart = false,
                NotifyOnCompletion = true,
                NotifyOnFailure = false,
                NotifyOnPause = true,
                Frequency = NotificationFrequency.Daily,
            };
            for (int index = 0; index < AttentionProperties.Length; index++)
            {
                AttentionProperties[index].SetValue(preferences, index % 3 == 0);
            }

            before = Snapshot(preferences);
            db.NotificationPreferences.Add(preferences);
            await db.SaveChangesAsync();
        }

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationPreferences persisted = await verifyDb.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        Snapshot(persisted).Should().BeEquivalentTo(before);
        persisted.RetentionDays.Should().Be(body == "{}" ? 14 : 30);
        persisted.EnableEmailNotifications.Should().BeFalse();
        persisted.EnablePushNotifications.Should().BeFalse();
        persisted.EnableTelegramNotifications.Should().BeFalse();
        persisted.EnableInAppNotifications.Should().BeFalse();
        persisted.NotifyOnStart.Should().BeFalse();
        persisted.NotifyOnCompletion.Should().BeTrue();
        persisted.NotifyOnFailure.Should().BeFalse();
        persisted.NotifyOnPause.Should().BeTrue();
        persisted.Frequency.Should().Be(NotificationFrequency.Daily);
    }

    [Theory]
    [InlineData("JobStarted", "jobstarted", true)]
    [InlineData("jobstarted", "JobStarted", false)]
    public async Task UpdatePreferencesAsync_CaseVariantDuplicateRows_LastWriteWins(
        string firstToken,
        string secondToken,
        bool secondValue)
    {
        await using var factory = new CustomWebApplicationFactory();
        string username = $"preference-duplicates-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(
            username,
            $"{username}@example.com");
        Guid userId = await GetUserIdAsync(factory, username);
        string firstValue = (!secondValue).ToString().ToLowerInvariant();
        string finalValue = secondValue.ToString().ToLowerInvariant();
        string body =
            $$"""
            {
              "eventChannelPreferences": [
                {
                  "eventType": "{{firstToken}}",
                  "inApp": {{firstValue}},
                  "email": {{firstValue}},
                  "push": {{firstValue}},
                  "telegram": {{firstValue}}
                },
                {
                  "eventType": "{{secondToken}}",
                  "inApp": {{finalValue}},
                  "email": {{finalValue}},
                  "push": {{finalValue}},
                  "telegram": {{finalValue}}
                }
              ]
            }
            """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationPreferences persisted = await verifyDb.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        persisted.InAppOnJobStarted.Should().Be(secondValue);
        persisted.EmailOnJobStarted.Should().Be(secondValue);
        persisted.PushOnJobStarted.Should().Be(secondValue);
        persisted.TelegramOnJobStarted.Should().Be(secondValue);
    }

    [Fact]
    public async Task UpdatePreferencesAsync_EventOnlyPartial_CannotSynthesizeEmailOrPushOptIns()
    {
        await using var factory = new CustomWebApplicationFactory();
        string username = $"preference-event-only-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(username, $"{username}@example.com");
        Guid userId = await GetUserIdAsync(factory, username);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NotificationPreferences.Add(new NotificationPreferences
            {
                UserId = userId,
                EnableEmailNotifications = true,
                EnablePushNotifications = true,
                EnableInAppNotifications = true,
                EnableTelegramNotifications = false,
                NotifyOnStart = false,
                EmailOnJobStarted = false,
                PushOnJobStarted = false,
                InAppOnJobStarted = false,
                TelegramOnJobStarted = false,
            });
            await db.SaveChangesAsync();
        }

        using var content = new StringContent("{\"notifyOnStart\":true}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationPreferences persisted = await verifyDb.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        persisted.NotifyOnStart.Should().BeTrue();
        persisted.InAppOnJobStarted.Should().BeFalse();
        persisted.EmailOnJobStarted.Should().BeFalse();
        persisted.PushOnJobStarted.Should().BeFalse();
        persisted.TelegramOnJobStarted.Should().BeFalse();
        persisted.EnableEmailNotifications.Should().BeTrue();
        persisted.EnablePushNotifications.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePreferencesAsync_ChannelOnlyPartial_PreservesEveryJobCell()
    {
        await using var factory = new CustomWebApplicationFactory();
        string username = $"preference-channel-only-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(username, $"{username}@example.com");
        Guid userId = await GetUserIdAsync(factory, username);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NotificationPreferences.Add(new NotificationPreferences
            {
                UserId = userId,
                EnableEmailNotifications = false,
                EnablePushNotifications = false,
                NotifyOnStart = true,
                NotifyOnCompletion = true,
                NotifyOnFailure = true,
                NotifyOnPause = true,
                EmailOnJobStarted = false,
                EmailOnJobCompleted = false,
                EmailOnJobFailed = false,
                EmailOnJobPaused = false,
                PushOnJobStarted = false,
                PushOnJobCompleted = false,
                PushOnJobFailed = false,
                PushOnJobPaused = false,
            });
            await db.SaveChangesAsync();
        }

        using var content = new StringContent(
            "{\"enableEmailNotifications\":true,\"enablePushNotifications\":true}",
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationPreferences persisted = await verifyDb.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        persisted.EnableEmailNotifications.Should().BeTrue();
        persisted.EnablePushNotifications.Should().BeTrue();
        persisted.EmailOnJobStarted.Should().BeFalse();
        persisted.EmailOnJobCompleted.Should().BeFalse();
        persisted.EmailOnJobFailed.Should().BeFalse();
        persisted.EmailOnJobPaused.Should().BeFalse();
        persisted.PushOnJobStarted.Should().BeFalse();
        persisted.PushOnJobCompleted.Should().BeFalse();
        persisted.PushOnJobFailed.Should().BeFalse();
        persisted.PushOnJobPaused.Should().BeFalse();
    }

    [Theory]
    [InlineData("Failure", false, "failure", true, true)]
    [InlineData("failure", true, "Failure", false, false)]
    public async Task AttentionPreferences_RawCaseVariantKeys_OrderedLastWriteWinsWithout500(
        string firstKey,
        bool firstValue,
        string secondKey,
        bool secondValue,
        bool expected)
    {
        await using var factory = new CustomWebApplicationFactory();
        string username = $"category-duplicates-{Guid.NewGuid():N}";
        HttpClient client = await factory.CreateAuthenticatedClientAsync(username, $"{username}@example.com");
        Guid userId = await GetUserIdAsync(factory, username);
        await EnableNativePushAsync(factory);
        string body = $$"""
            { "categories": { "{{firstKey}}": {{firstValue.ToString().ToLowerInvariant()}}, "{{secondKey}}": {{secondValue.ToString().ToLowerInvariant()}} } }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync(
            "/api/notifications/attention-push-preferences",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationPreferences persisted = await db.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        AttentionPushCategoryPreferences categories = AttentionPushCategoryPreferences.FromJson(
            persisted.AttentionPushCategoryPreferencesJson);
        categories.Categories.Should().ContainSingle();
        categories.Categories["failure"].Should().Be(expected);
    }

    private static async Task EnableNativePushAsync(CustomWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IAppSettingsRepository settings = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        string json = JsonSerializer.Serialize(new OperatorFeatureSettings { NativePushEnabled = true });
        await settings.SetAsync(OperatorFeatureSettings.SectionName, json);
        await settings.SaveChangesAsync();
    }

    private static Dictionary<string, bool> Snapshot(NotificationPreferences preferences)
        => AttentionProperties.ToDictionary(
            property => property.Name,
            property => (bool)property.GetValue(preferences)!,
            StringComparer.Ordinal);

    private static async Task<Guid> GetUserIdAsync(CustomWebApplicationFactory factory, string username)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users
            .Where(user => user.Username == username)
            .Select(user => user.Id)
            .SingleAsync();
    }
}
