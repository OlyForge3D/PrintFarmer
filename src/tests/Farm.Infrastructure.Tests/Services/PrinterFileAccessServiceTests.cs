using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Infrastructure.Tests.Services;

public sealed class PrinterFileAccessServiceTests
{
    [Fact]
    public async Task CanReadAsync_RequiresBothCurrentPermissionAndSourceView()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.Add(("printer_files", "read"));
        Assert.True(await fixture.Service.CanReadAsync(fixture.Actor, fixture.Source));
        fixture.Resources.Setup(x => x.CanActorAccessPrinterAsync(
            fixture.Actor.ToString(), fixture.Source, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Assert.False(await fixture.Service.CanReadAsync(fixture.Actor, fixture.Source));
    }

    [Fact]
    public async Task CanAccessLocalAsync_RequiresCurrentLibraryPermissionAndIndependentGroupAccess()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        Guid asset = Guid.NewGuid();
        fixture.Resources.Setup(x => x.CanActorAccessGcodeFileAsync(
            fixture.Actor.ToString(), asset, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Assert.True(await fixture.Service.CanAccessLocalAsync(fixture.Actor, asset));
        Assert.False(await fixture.Service.CanAccessLocalAsync(fixture.Actor, asset, PrinterGroupAccessLevel.Submit));
        fixture.Grants.Remove(("gcode_library", "read"));
        Assert.False(await fixture.Service.CanAccessLocalAsync(fixture.Actor, asset));
    }

    [Fact]
    public async Task CanAccessLocalAsync_RealGroupRulesRejectMissingOrExpiredRolesAndPreserveViewLevel()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = "file-viewer", IsActive = true };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = "restricted library" };
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FileName = "private.gcode",
            FileHash = "private",
            Folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" },
            PrinterGroupId = group.Id,
        };
        fixture.Db.AddRange(role, group, file, new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            PrinterGroupId = group.Id,
            AccessLevel = PrinterGroupAccessLevel.View,
        });
        await fixture.Db.SaveChangesAsync();
        var service = new PrinterFileAccessService(
            fixture.Db, fixture.Users.Object, new QueueResourceAuthorizationService(fixture.Db));
        Assert.False(await service.CanAccessLocalAsync(fixture.Actor, file.Id));
        var assignment = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = fixture.Actor,
            RoleId = role.Id,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        };
        fixture.Db.UserRoles.Add(assignment);
        await fixture.Db.SaveChangesAsync();
        Assert.False(await service.CanAccessLocalAsync(fixture.Actor, file.Id));
        assignment.ExpiresAt = DateTime.UtcNow.AddMinutes(10);
        await fixture.Db.SaveChangesAsync();
        Assert.True(await service.CanAccessLocalAsync(fixture.Actor, file.Id));
        Assert.False(await service.CanAccessLocalAsync(fixture.Actor, file.Id, PrinterGroupAccessLevel.Submit));
        role.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        Assert.False(await service.CanAccessLocalAsync(fixture.Actor, file.Id));
    }

    [Fact]
    public async Task CanActAsync_CopyDoesNotGrantDeleteOrQueue()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.AddRange([("printer_files", "read"), ("printer_files", "copy")]);
        Assert.True(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Copy));
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Delete));
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Queue, fixture.Target));
    }

    [Fact]
    public async Task CanActAsync_QueueRequiresLiveDestinationSubmitAndEnabledRegistration()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.AddRange([("printer_files", "read"), ("printer_files", "copy"), ("queue", "write")]);
        Assert.True(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Queue, fixture.Target));
        Printer target = await fixture.Db.Printers.SingleAsync(x => x.Id == fixture.Target);
        target.IsEnabled = false;
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Queue, fixture.Target));
    }

    [Fact]
    public async Task CanActAsync_CopyCannotBypassRevokedLibraryWrite()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.AddRange([("printer_files", "read"), ("printer_files", "copy")]);
        fixture.Grants.Remove(("gcode_library", "create"));
        Assert.True(await fixture.Service.CanReadAsync(fixture.Actor, fixture.Source));
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Copy));
    }

    [Fact]
    public async Task CanActAsync_ExplicitDenySuppressesResourceAdmin()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.Add(("printer_files", "admin"));
        fixture.Denies.Add(("printer_files", "delete"));
        Assert.True(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Copy));
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Delete));
    }

    [Fact]
    public async Task CanReadAsync_DisablingActorRevokesRetainedWork()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Grants.Add(("printer_files", "read"));
        Assert.True(await fixture.Service.CanReadAsync(fixture.Actor, fixture.Source));
        User user = await fixture.Db.Users.SingleAsync();
        user.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Service.CanReadAsync(fixture.Actor, fixture.Source));
    }

    [Fact]
    public async Task CanActAsync_ExpiredAdministratorAssignmentIsNotAuthority()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = fixture.Actor,
            Role = new Role { Id = Guid.NewGuid(), Name = "farm_admin", IsActive = true },
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Service.CanActAsync(fixture.Actor, fixture.Source, PrinterFileAction.Delete));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Users.Setup(x => x.GetGrantedPermissionsAsync(Actor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Grants);
            Users.Setup(x => x.GetDeniedPermissionsAsync(Actor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Denies);
            Grants.AddRange([("gcode_library", "read"), ("gcode_library", "create")]);
            Resources.Setup(x => x.CanActorAccessPrinterAsync(
                    Actor.ToString(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Service = new PrinterFileAccessService(Db, Users.Object, Resources.Object);
        }

        public Guid Actor { get; } = Guid.NewGuid();

        public Guid Source { get; } = Guid.NewGuid();

        public Guid Target { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public Mock<IUsersRepository> Users { get; } = new();

        public Mock<IQueueResourceAuthorizationService> Resources { get; } = new();

        public List<(string Resource, string Action)> Grants { get; } = [];

        public List<(string Resource, string Action)> Denies { get; } = [];

        public PrinterFileAccessService Service { get; }

        public async Task SeedAsync()
        {
            Db.Users.Add(new User { Id = Actor, Username = "catalog-test", IsActive = true });
            Db.Printers.AddRange(
                new Printer { Id = Source, Name = "Source", ServerUrl = "http://source.invalid", IsEnabled = true },
                new Printer { Id = Target, Name = "Target", ServerUrl = "http://target.invalid", IsEnabled = true });
            await Db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
