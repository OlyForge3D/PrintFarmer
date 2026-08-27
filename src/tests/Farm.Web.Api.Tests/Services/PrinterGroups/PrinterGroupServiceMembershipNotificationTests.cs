using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterGroups;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.PrinterGroups;

/// <summary>
/// #1731: verifies PrinterGroupService notifies IQueueSubscriptionMembershipNotifier
/// exactly once whenever printer-group membership actually changes -- reassigning a
/// printer to a different group, removing a printer from a group, deleting a group, or
/// changing which roles may access a group (since access-rule changes gate which
/// printers a role's members are authorized to see, and therefore subscribe to, just
/// like printer membership does) -- and never on operations that cannot change
/// subscription membership (create/rename). This is the mandatory membership-change
/// acceptance scenario from the issue.
/// </summary>
public class PrinterGroupServiceMembershipNotificationTests
{
    [Fact]
    public async Task AddPrinterAsync_NotifiesMembershipChangedExactlyOnce()
    {
        await using AppDbContext db = CreateDbContext();
        Guid modelId = SeedPrinterModel(db);
        PrinterGroup sourceGroup = CreateGroup("Source");
        PrinterGroup targetGroup = CreateGroup("Target");
        Printer printer = CreatePrinter(modelId);
        db.PrinterGroups.AddRange(sourceGroup, targetGroup);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        notifier
            .Setup(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrinterGroupService service = CreateService(db, notifier.Object);

        // Reassigning a printer to a different group -- the mandatory acceptance scenario.
        await service.AddPrinterAsync(targetGroup.Id, printer.Id, CancellationToken.None);

        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
        Printer? updated = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printer.Id);
        updated.PrinterGroupId.Should().Be(targetGroup.Id);
    }

    [Fact]
    public async Task RemovePrinterAsync_NotifiesMembershipChangedExactlyOnce()
    {
        await using AppDbContext db = CreateDbContext();
        Guid modelId = SeedPrinterModel(db);
        PrinterGroup group = CreateGroup("Source");
        Printer printer = CreatePrinter(modelId, group.Id);
        db.PrinterGroups.Add(group);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        notifier
            .Setup(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrinterGroupService service = CreateService(db, notifier.Object);

        await service.RemovePrinterAsync(group.Id, printer.Id, CancellationToken.None);

        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
        Printer? updated = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printer.Id);
        updated.PrinterGroupId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NotifiesMembershipChangedExactlyOnce()
    {
        await using AppDbContext db = CreateDbContext();
        PrinterGroup group = CreateGroup("Doomed");
        db.PrinterGroups.Add(group);
        await db.SaveChangesAsync();

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        notifier
            .Setup(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrinterGroupService service = CreateService(db, notifier.Object);

        await service.DeleteAsync(group.Id, CancellationToken.None);

        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAccessRulesAsync_NotifiesMembershipChangedExactlyOnce()
    {
        await using AppDbContext db = CreateDbContext();
        PrinterGroup group = CreateGroup("Restricted");
        var role = new Role { Id = Guid.NewGuid(), Name = "farm_operator" };
        db.PrinterGroups.Add(group);
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        notifier
            .Setup(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrinterGroupService service = CreateService(db, notifier.Object);

        // Revoking/granting a role's access to a group changes which printers that
        // role's members are authorized to see -- and therefore subscribe to -- just
        // like adding/removing a printer from the group does.
        await service.SetAccessRulesAsync(
            group.Id,
            new SetAccessRulesDto(new[] { new SetAccessRuleItem(role.Id, PrinterGroupAccessLevel.View) }),
            CancellationToken.None);

        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_DoesNotNotifyMembershipChanged()
    {
        await using AppDbContext db = CreateDbContext();
        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        PrinterGroupService service = CreateService(db, notifier.Object);

        await service.CreateAsync(new CreatePrinterGroupDto { Name = "New Group" }, CancellationToken.None);

        // Creating an empty group cannot change any printer's subscription membership.
        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_RenameOnly_DoesNotNotifyMembershipChanged()
    {
        await using AppDbContext db = CreateDbContext();
        PrinterGroup group = CreateGroup("Old Name");
        db.PrinterGroups.Add(group);
        await db.SaveChangesAsync();

        var notifier = new Mock<IQueueSubscriptionMembershipNotifier>();
        PrinterGroupService service = CreateService(db, notifier.Object);

        await service.UpdateAsync(group.Id, new UpdatePrinterGroupDto { Name = "New Name" }, CancellationToken.None);

        // Renaming a group does not change which printers are members of it.
        notifier.Verify(n => n.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Seeds a Manufacturer + PrinterModel row so printers referencing this ModelId resolve
    /// through PrinterGroupService.AddPrinterAsync's required `Include(p => p.Model)` query.
    /// </summary>
    private static Guid SeedPrinterModel(AppDbContext db)
    {
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.SaveChanges();
        return model.Id;
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrinterGroupServiceMembershipNotificationTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static PrinterGroupService CreateService(AppDbContext db, IQueueSubscriptionMembershipNotifier notifier)
    {
        var repository = new EfPrinterGroupRepository(db);
        return new PrinterGroupService(
            repository,
            db,
            NullLogger<PrinterGroupService>.Instance,
            notifier);
    }

    private static PrinterGroup CreateGroup(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedDate = DateTimeOffset.UtcNow,
        UpdatedDate = DateTimeOffset.UtcNow,
    };

    private static Printer CreatePrinter(Guid modelId, Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Voron",
        ServerUrl = "http://voron.local",
        FrontendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
        ModelId = modelId,
        PrinterGroupId = groupId,
    };
}
