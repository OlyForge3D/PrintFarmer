using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

public sealed class NotificationServicePreferenceRaceTests
{
    [Fact]
    public async Task FirstCreate_TwoPooledFactoryCallers_ConvergeAndRetryWithFreshContext()
    {
        await using var host = new CustomWebApplicationFactory();
        using var client = host.CreateClient();
        IDbContextFactory<AppDbContext> factory =
            host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        factory.GetType().Name.Should().Contain("PooledDbContextFactory");

        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId, "first-create");

        NotificationPreferencesUpdate patchA = MatrixPatch(
            NotificationPreferenceEvent.JobStarted,
            inApp: false,
            email: true,
            push: false,
            telegram: false);
        NotificationPreferencesUpdate patchB = MatrixPatch(
            NotificationPreferenceEvent.MaintenanceDue,
            inApp: false,
            email: false,
            push: false,
            telegram: true);

        var contexts = new ConcurrentBag<string>();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstAttempts = 0;
        int classifierCalls = 0;

        async Task FirstHook(AppDbContext context, CancellationToken _)
        {
            contexts.Add(context.ContextId.ToString());
            if (Interlocked.Increment(ref firstAttempts) == 1)
            {
                firstRead.TrySetResult();
                throw new DbUpdateConcurrencyException("Forced stale first-create read.");
            }

            await Task.CompletedTask;
        }

        Task second = RunAfterSignalAsync(
            firstRead.Task,
            () => RunPatchAsync(
                host,
                userId,
                patchB,
                (context, _) =>
                {
                    contexts.Add(context.ContextId.ToString());
                    return Task.CompletedTask;
                },
                () => { }),
            secondCommitted);
        Task first = Task.Run(() => RunPatchAsync(host, userId, patchA, FirstHook, () =>
        {
            Interlocked.Increment(ref classifierCalls);
            secondCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }));
        await Task.WhenAll(first, second);

        await using AppDbContext verify = await factory.CreateDbContextAsync();
        NotificationPreferences[] rows = await verify.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToArrayAsync();

        rows.Should().ContainSingle();
        rows[0].EmailOnJobStarted.Should().BeTrue();
        rows[0].TelegramOnMaintenanceDue.Should().BeTrue();
        classifierCalls.Should().BeGreaterThan(0, "one concurrent insert must be classified and retried");
        contexts.Distinct().Should().HaveCountGreaterThan(2, "the retry must lease a fresh pooled context");
    }

    [Fact]
    public async Task DisjointAttentionColumns_TwoPooledFactoryCallers_PreserveBothWritesAfterRetry()
    {
        await using var host = new CustomWebApplicationFactory();
        using var client = host.CreateClient();
        IDbContextFactory<AppDbContext> factory =
            host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

        Guid userId = Guid.NewGuid();
        var seed = NotificationPreferencesDefaults.Create(userId);
        seed.InAppOnJobStarted = false;
        seed.PushOnPrinterFailure = false;
        await SeedUserAsync(factory, userId, "disjoint-columns", seed);

        NotificationPreferencesUpdate patchA = MatrixPatch(
            NotificationPreferenceEvent.JobStarted,
            inApp: true,
            email: false,
            push: false,
            telegram: false);
        NotificationPreferencesUpdate patchB = MatrixPatch(
            NotificationPreferenceEvent.PrinterFailure,
            inApp: false,
            email: false,
            push: true,
            telegram: false);

        var contexts = new ConcurrentBag<string>();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstAttempts = 0;
        int classifierCalls = 0;

        async Task FirstHook(AppDbContext context, CancellationToken _)
        {
            contexts.Add(context.ContextId.ToString());
            if (Interlocked.Increment(ref firstAttempts) == 1)
            {
                firstRead.TrySetResult();
                throw new DbUpdateConcurrencyException("Forced stale disjoint-column read.");
            }

            await Task.CompletedTask;
        }

        Task second = RunAfterSignalAsync(
            firstRead.Task,
            () => RunPatchAsync(
                host,
                userId,
                patchB,
                (context, _) =>
                {
                    contexts.Add(context.ContextId.ToString());
                    return Task.CompletedTask;
                },
                () => { }),
            secondCommitted);
        Task first = Task.Run(() => RunPatchAsync(host, userId, patchA, FirstHook, () =>
        {
            Interlocked.Increment(ref classifierCalls);
            secondCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }));
        await Task.WhenAll(first, second);

        await using AppDbContext verify = await factory.CreateDbContextAsync();
        NotificationPreferences persisted = await verify.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId);

        persisted.InAppOnJobStarted.Should().BeTrue();
        persisted.PushOnPrinterFailure.Should().BeTrue();
        persisted.EnableInAppNotifications.Should().BeTrue();
        persisted.EnablePushNotifications.Should().BeTrue();
        classifierCalls.Should().BeGreaterThan(0, "the forced adverse interleaving must exercise retry classification");
        contexts.Distinct().Should().HaveCountGreaterThan(2, "the losing write must retry in a fresh context");
    }

    private static async Task RunPatchAsync(
        CustomWebApplicationFactory host,
        Guid userId,
        NotificationPreferencesUpdate patch,
        Func<AppDbContext, CancellationToken, Task> barrier,
        Action recordClassification)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var service = (NotificationService)scope.ServiceProvider.GetRequiredService<INotificationService>();
        service.OnAfterPreferenceReadForTestsAsync = barrier;
        service.PreferenceConflictClassifierForTests = exception =>
        {
            recordClassification();
            return PreferenceConcurrencyRetry.Classify(exception);
        };

        _ = await service.UpdatePreferencesAsync(userId, patch, CancellationToken.None);
    }

    private static async Task RunAfterSignalAsync(
        Task signal,
        Func<Task> operation,
        TaskCompletionSource completion)
    {
        await signal.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await operation();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
    }

    private static async Task SeedUserAsync(
        IDbContextFactory<AppDbContext> factory,
        Guid userId,
        string username,
        NotificationPreferences? preferences = null)
    {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        db.Users.Add(new User
        {
            Id = userId,
            Username = $"{username}-{userId:N}",
            Email = $"{username}-{userId:N}@test.local",
            PasswordHash = "x",
        });
        if (preferences is not null)
        {
            db.NotificationPreferences.Add(preferences);
        }

        await db.SaveChangesAsync();
    }

    private static NotificationPreferencesUpdate MatrixPatch(
        NotificationPreferenceEvent eventType,
        bool inApp,
        bool email,
        bool push,
        bool telegram) =>
        new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: null,
            EnableTelegramNotifications: null,
            NotifyOnStart: null,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows:
            [
                new NotificationPreferencesRowPatch(eventType, inApp, email, push, telegram),
            ]);
}
