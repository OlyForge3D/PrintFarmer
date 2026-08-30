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
///
/// <see cref="AppDbContext"/> is registered per-<see cref="IServiceScope"/> (not as a singleton)
/// so that each internal <c>CreateScope()</c> call inside the service under test — including the
/// dedicated per-printer scope used by the auto-apply path — resolves a genuinely fresh context
/// instance, exactly like production DI. Sharing a single context instance across those scopes
/// would let EF Core's relationship-fixup silently re-populate the <c>ServiceState</c> navigation
/// from already-tracked entities even when the real query is missing its <c>.Include()</c>,
/// masking the exact bug these tests exist to catch.
/// </summary>
public class CatalogUpdateDetectionServiceTests
{
    [Fact]
    public async Task DetectAndHandleUpdatesAsync_ModelUpdatedAfterLastSync_CreatesNotification()
    {
        // Model was updated AFTER the printer's last recorded model sync — a genuine update is
        // available and detection must fire.
        (string dbName, _, Guid userId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow,
            lastModelSyncAt: DateTime.UtcNow.AddDays(-1));

        CatalogUpdateDetectionService service = CreateService(dbName, userId, autoApply: false, out CatalogUpdateSettings settings);

        await service.DetectAndHandleUpdatesAsync(settings, CancellationToken.None);

        await using AppDbContext db = OpenDbContext(dbName);
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
        (string dbName, _, _) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow.AddDays(-1),
            lastModelSyncAt: DateTime.UtcNow);

        CatalogUpdateDetectionService service = CreateService(dbName, Guid.NewGuid(), autoApply: false, out CatalogUpdateSettings settings);

        await service.DetectAndHandleUpdatesAsync(settings, CancellationToken.None);

        await using AppDbContext db = OpenDbContext(dbName);
        List<Notification> notifications = await db.Notifications.ToListAsync();
        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAndHandleUpdatesAsync_AutoApplyEnabled_PersistsSyncWithoutDuplicateServiceStateRow()
    {
        // Regression coverage for a review finding on the #2228 fix: the AutoApply re-query
        // (which reloads each outdated printer WITH tracking, in its own per-printer service
        // scope) must also Include(ServiceState). PrintersService's real ApplyModelTemplateAsync
        // unconditionally calls an "EnsureServiceState" helper that creates a brand-new
        // PrinterServiceState when the navigation is null. If the re-query omits the Include,
        // that helper always sees null (every outdated printer already has a ServiceState row by
        // construction of the "outdated" filter) and creates a duplicate row colliding on the
        // PrinterId primary key — SaveChangesAsync then throws, is swallowed by the per-printer
        // catch block, and LastModelSyncAt silently never advances, reproducing the exact
        // "detection is dead" symptom of #2228 one level deeper, in the auto-apply path.
        (string dbName, Guid printerId, Guid userId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow,
            lastModelSyncAt: DateTime.UtcNow.AddDays(-1));

        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.ApplyModelTemplateAsync(It.IsAny<Printer>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Printer, bool, CancellationToken>((printer, _, _) =>
            {
                // Mirrors PrintersService.EnsureServiceState + the "always mark sync complete"
                // line in ApplyModelTemplateAsync: creates a ServiceState row from scratch when
                // the navigation was never loaded, exactly like the production helper does.
                printer.ServiceState ??= new PrinterServiceState { PrinterId = printer.Id };
                printer.ServiceState.LastModelSyncAt = DateTime.UtcNow;
            })
            .ReturnsAsync(true);

        CatalogUpdateDetectionService service = CreateService(
            dbName, userId, autoApply: true, out CatalogUpdateSettings settings, printersService.Object);

        await service.DetectAndHandleUpdatesAsync(settings, CancellationToken.None);

        await using AppDbContext db = OpenDbContext(dbName);

        (await db.PrinterServiceStates.AsNoTracking().CountAsync()).Should().Be(
            1, "the auto-apply re-query must Include(ServiceState) so the existing row is reused " +
               "instead of creating a duplicate that collides on the PrinterId primary key");

        PrinterServiceState? state = await db.PrinterServiceStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.PrinterId == printerId);
        state.Should().NotBeNull();
        state!.LastModelSyncAt.Should().NotBeNull().And.BeAfter(DateTime.UtcNow.AddMinutes(-1),
            "a successful auto-apply must persist the new sync timestamp — before the ServiceState " +
            "Include was added to the re-query, the duplicate-key failure was silently swallowed and " +
            "LastModelSyncAt never advanced from its stale seeded value");
    }

    private static async Task<(string DbName, Guid PrinterId, Guid UserId)> SeedAsync(
        DateTime modelUpdatedAt,
        DateTime? lastModelSyncAt)
    {
        string dbName = $"CatalogUpdateDetectionServiceTests_{Guid.NewGuid():N}";

        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        await using AppDbContext db = OpenDbContext(dbName);

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
        return (dbName, printerId, userId);
    }

    private static AppDbContext OpenDbContext(string dbName)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new AppDbContext(options);
    }

    private static CatalogUpdateDetectionService CreateService(
        string dbName,
        Guid userId,
        bool autoApply,
        out CatalogUpdateSettings settings,
        IPrintersService? printersService = null)
    {
        settings = new CatalogUpdateSettings { Enabled = true, AutoApply = autoApply };

        var usersRepository = new Mock<IUsersRepository>();
        usersRepository
            .Setup(r => r.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<UserDto>)new List<UserDto>
            {
                new() { Id = userId, Username = "active-user", IsActive = true }
            });

        var services = new ServiceCollection();

        // Scoped (the AddDbContext default), backed by the same named InMemory database, so
        // every IServiceScope created inside the service under test — including the dedicated
        // per-printer scopes in the auto-apply path — gets its own fresh AppDbContext instance
        // that must rely on explicit .Include() calls, just like production.
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(usersRepository.Object);
        services.AddSingleton(printersService ?? Mock.Of<IPrintersService>());
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
