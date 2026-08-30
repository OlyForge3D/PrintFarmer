using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// Regression coverage for issue #2228: <see cref="CatalogUpdateDetectionService"/>'s
/// printer query used to load <c>Model</c> and <c>Toolheads</c> via <c>.Include()</c> but never
/// <c>ServiceState</c>, then filtered in-memory on <c>p.ServiceState != null</c>. Since
/// <c>ServiceState</c> was never included (and there is no lazy-loading proxy configured), it
/// was always <c>null</c>, so the "outdated printers" filter always produced zero matches and
/// catalog update detection silently never fired for any printer, ever. These tests exercise
/// <see cref="CatalogUpdateDetectionService.DetectAndHandleUpdatesAsync"/> (made <c>internal</c>
/// for this purpose) directly against a real EF Core query pipeline, proving detection actually
/// fires when a printer's model is genuinely newer than its last recorded sync.
/// </summary>
public class CatalogUpdateDetectionServiceTests
{
    [Fact]
    public async Task DetectAndHandleUpdatesAsync_ModelUpdatedAfterLastSync_CreatesNotification()
    {
        // Model was updated AFTER the printer's last recorded model sync — a genuine update is
        // available and detection must fire.
        (AppDbContext db, Guid printerId, Guid userId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow,
            lastModelSyncAt: DateTime.UtcNow.AddDays(-1));

        CatalogUpdateDetectionService service = CreateService(db, userId, out CatalogUpdateSettings settings);

        await service.DetectAndHandleUpdatesAsync(settings, CancellationToken.None);

        List<Notification> notifications = await db.Notifications.ToListAsync();
        notifications.Should().ContainSingle(
            n => n.UserId == userId && n.Type == NotificationType.CatalogUpdateAvailable,
            "the printer's model was updated after its last recorded sync, so detection must " +
            "fire and notify active users — before the #2228 fix, ServiceState was never " +
            "Include()d so this filter always evaluated to false and no notification was ever created");
    }

    [Fact]
    public async Task DetectAndHandleUpdatesAsync_ModelSyncedAfterModelUpdate_CreatesNoNotification()
    {
        // Negative control: the last recorded sync happened AFTER the model's most recent
        // update, so no catalog update is pending and no notification should be created.
        (AppDbContext db, Guid printerId, Guid userId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow.AddDays(-1),
            lastModelSyncAt: DateTime.UtcNow);

        CatalogUpdateDetectionService service = CreateService(db, userId, out CatalogUpdateSettings settings);

        await service.DetectAndHandleUpdatesAsync(settings, CancellationToken.None);

        List<Notification> notifications = await db.Notifications.ToListAsync();
        notifications.Should().BeEmpty();
        _ = printerId;
        _ = userId;
    }

    private static async Task<(AppDbContext Db, Guid PrinterId, Guid UserId)> SeedAsync(
        DateTime modelUpdatedAt,
        DateTime? lastModelSyncAt)
    {
        AppDbContext db = CreateDbContext();

        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = "Test model",
            UpdatedAt = modelUpdatedAt
        });

        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Catalog detection test printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId,
            ServerUrl = "http://catalog-detection-test.local",
            IsEnabled = true
        });

        db.PrinterServiceStates.Add(new PrinterServiceState
        {
            PrinterId = printerId,
            LastModelSyncAt = lastModelSyncAt
        });

        await db.SaveChangesAsync();
        return (db, printerId, userId);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"CatalogUpdateDetectionServiceTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static CatalogUpdateDetectionService CreateService(AppDbContext db, Guid userId, out CatalogUpdateSettings settings)
    {
        settings = new CatalogUpdateSettings { Enabled = true, AutoApply = false };

        var usersRepository = new Mock<IUsersRepository>();
        usersRepository
            .Setup(r => r.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<UserDto>)new List<UserDto>
            {
                new() { Id = userId, Username = "active-user", IsActive = true }
            });

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(usersRepository.Object);
        services.AddSingleton(Mock.Of<IPrintersService>());
        ServiceProvider provider = services.BuildServiceProvider();

        var settingsMonitor = new Mock<IOptionsMonitor<CatalogUpdateSettings>>();
        settingsMonitor.Setup(m => m.CurrentValue).Returns(settings);

        return new CatalogUpdateDetectionService(
            provider,
            NullLogger<CatalogUpdateDetectionService>.Instance,
            settingsMonitor.Object,
            Mock.Of<IBackgroundServiceMonitor>());
    }
}
