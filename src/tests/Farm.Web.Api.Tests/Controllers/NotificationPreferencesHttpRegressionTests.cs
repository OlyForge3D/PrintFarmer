using System.Net;
using System.Reflection;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
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
