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

    [Theory]
    [InlineData("full")]
    [InlineData("readOnly")]
    [InlineData("copyNoDelete")]
    [InlineData("deleteDenied")]
    [InlineData("noRead")]
    [InlineData("admin")]
    [InlineData("adminExpired")]
    [InlineData("inactiveUser")]
    [InlineData("libraryRevoked")]
    [InlineData("libraryReadDenied")]
    public async Task CatalogAuthority_MixedLivePermissions_MatchesSingleResourceChecks(string profile)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.SeedAccessMatrixAsync();
        fixture.Grants.AddRange([("printer_files", "read"), ("printer_files", "copy"), ("printer_files", "delete")]);
        switch (profile)
        {
            case "readOnly":
                fixture.Grants.RemoveAll(x => x.Resource == "printer_files" && x.Action != "read");
                break;
            case "copyNoDelete":
                fixture.Grants.Remove(("printer_files", "delete"));
                break;
            case "deleteDenied":
                fixture.Grants.Add(("printer_files", "admin"));
                fixture.Denies.Add(("printer_files", "delete"));
                break;
            case "noRead":
                fixture.Grants.Remove(("printer_files", "read"));
                break;
            case "admin":
            case "adminExpired":
                fixture.Grants.Clear();
                fixture.Db.UserRoles.Add(new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = fixture.Actor,
                    Role = new Role { Id = Guid.NewGuid(), Name = "farm_admin", IsActive = true },
                    IsActive = true,
                    ExpiresAt = profile == "adminExpired" ? DateTime.UtcNow.AddDays(-1) : null,
                });
                break;
            case "inactiveUser":
                (await fixture.Db.Users.SingleAsync()).IsActive = false;
                break;
            case "libraryRevoked":
                fixture.Grants.Remove(("gcode_library", "create"));
                break;
            case "libraryReadDenied":
                fixture.Denies.Add(("gcode_library", "read"));
                break;
        }

        await fixture.Db.SaveChangesAsync();
        await AssertCatalogMatchesSingleChecksAsync(fixture);
    }

    [Fact]
    public async Task CatalogAuthority_RoleRevokedBetweenRequests_RecomputesLiveAuthority()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.SeedAccessMatrixAsync();
        fixture.Grants.AddRange([("printer_files", "read"), ("printer_files", "copy"), ("printer_files", "delete")]);
        var resources = new QueueResourceAuthorizationService(fixture.Db);
        var service = new PrinterFileAccessService(fixture.Db, fixture.Users.Object, resources);
        Guid[] ids = await fixture.Db.Printers.Select(x => x.Id).ToArrayAsync();
        PrinterFileCatalogAuthority before = await service.GetCatalogAuthorityAsync(fixture.Actor, ids);

        Role manager = await fixture.Db.Roles.SingleAsync(x => x.Name == "manager");
        manager.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        PrinterFileCatalogAuthority after = await service.GetCatalogAuthorityAsync(fixture.Actor, ids);
        Assert.True(before.Manageable.Count > after.Manageable.Count);
        await AssertCatalogMatchesSingleChecksAsync(fixture);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("admin")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(QueueActorIdentity.AutoDispatch)]
    [InlineData(QueueActorIdentity.Scheduler)]
    public async Task ResourceFilters_MixedScopesAndActors_MatchSingleResourceChecks(string actor)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.SeedAccessMatrixAsync();
        if (actor == "admin")
        {
            fixture.Db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = fixture.Actor,
                Role = new Role { Id = Guid.NewGuid(), Name = "farm_admin", IsActive = true },
                IsActive = true,
            });
            await fixture.Db.SaveChangesAsync();
        }

        string subject = actor is "user" or "admin" ? fixture.Actor.ToString() : actor;
        var resources = new QueueResourceAuthorizationService(fixture.Db);
        Guid[] printers = [.. await fixture.Db.Printers.Select(x => x.Id).ToListAsync(), Guid.NewGuid(), fixture.Source];
        Guid[] files = [.. await fixture.Db.GcodeFiles.Select(x => x.Id).ToListAsync(), Guid.NewGuid()];
        foreach (PrinterGroupAccessLevel level in Enum.GetValues<PrinterGroupAccessLevel>())
        {
            var expectedPrinters = new HashSet<Guid>();
            var expectedFiles = new HashSet<Guid>();
            foreach (Guid id in printers)
            {
                if (await resources.CanActorAccessPrinterAsync(subject, id, level))
                {
                    expectedPrinters.Add(id);
                }
            }

            foreach (Guid id in files)
            {
                if (await resources.CanActorAccessGcodeFileAsync(subject, id, level))
                {
                    expectedFiles.Add(id);
                }
            }

            Assert.Equal(expectedPrinters.Order(),
                (await resources.FilterActorAccessiblePrinterIdsAsync(subject, printers, level)).Order());
            Assert.Equal(expectedFiles.Order(),
                (await resources.FilterActorAccessibleGcodeFileIdsAsync(subject, files, level)).Order());
            Assert.Empty(await resources.FilterActorAccessiblePrinterIdsAsync(subject, [], level));
            Assert.Empty(await resources.FilterActorAccessibleGcodeFileIdsAsync(subject, [], level));
        }
    }

    private static async Task AssertCatalogMatchesSingleChecksAsync(Fixture fixture)
    {
        var resources = new QueueResourceAuthorizationService(fixture.Db);
        var service = new PrinterFileAccessService(fixture.Db, fixture.Users.Object, resources);
        Guid[] printers = [.. await fixture.Db.Printers.Select(x => x.Id).ToListAsync(), Guid.NewGuid(), fixture.Source];
        var readable = new HashSet<Guid>();
        var manageable = new HashSet<Guid>();
        var copyable = new HashSet<Guid>();
        var deletable = new HashSet<Guid>();
        foreach (Guid id in printers)
        {
            if (await service.CanReadAsync(fixture.Actor, id))
            {
                readable.Add(id);
                if (await resources.CanActorAccessPrinterAsync(fixture.Actor.ToString(), id, PrinterGroupAccessLevel.Manage))
                {
                    manageable.Add(id);
                }
            }

            if (await service.CanActAsync(fixture.Actor, id, PrinterFileAction.Copy))
            {
                copyable.Add(id);
            }

            if (await service.CanActAsync(fixture.Actor, id, PrinterFileAction.Delete))
            {
                deletable.Add(id);
            }
        }

        PrinterFileCatalogAuthority actual = await service.GetCatalogAuthorityAsync(fixture.Actor, printers);
        Assert.Equal(readable.Order(), actual.Readable.Order());
        Assert.Equal(manageable.Order(), actual.Manageable.Order());
        Assert.Equal(copyable.Order(), actual.Copyable.Order());
        Assert.Equal(deletable.Order(), actual.Deletable.Order());
        Assert.Empty((await service.GetCatalogAuthorityAsync(fixture.Actor, [])).Readable);
        Assert.Empty((await service.GetCatalogAuthorityAsync(Guid.Empty, printers)).Readable);

        Guid[] files = [.. await fixture.Db.GcodeFiles.Select(x => x.Id).ToListAsync(), Guid.NewGuid()];
        foreach (PrinterGroupAccessLevel level in Enum.GetValues<PrinterGroupAccessLevel>())
        {
            var expectedFiles = new HashSet<Guid>();
            foreach (Guid id in files)
            {
                if (await service.CanAccessLocalAsync(fixture.Actor, id, level))
                {
                    expectedFiles.Add(id);
                }
            }

            Assert.Equal(expectedFiles.Order(),
                (await service.FilterAccessibleLocalAsync(fixture.Actor, files, level)).Order());
            Assert.Empty(await service.FilterAccessibleLocalAsync(fixture.Actor, [], level));
            Assert.Empty(await service.FilterAccessibleLocalAsync(Guid.Empty, files, level));
        }
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

        public async Task SeedAccessMatrixAsync()
        {
            var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
            var owned = new CalibrationProject { Id = Guid.NewGuid(), Name = "owned", OwnerUserId = Actor };
            var foreign = new CalibrationProject { Id = Guid.NewGuid(), Name = "foreign", OwnerUserId = Guid.NewGuid() };
            Db.AddRange(folder, owned, foreign);
            foreach ((string name, PrinterGroupAccessLevel level) in new[]
                     {
                         ("viewer", PrinterGroupAccessLevel.View),
                         ("submitter", PrinterGroupAccessLevel.Submit),
                         ("manager", PrinterGroupAccessLevel.Manage),
                         ("expired", PrinterGroupAccessLevel.Manage),
                         ("inactiveRole", PrinterGroupAccessLevel.Manage),
                         ("inactiveAssignment", PrinterGroupAccessLevel.Manage),
                         ("foreign", PrinterGroupAccessLevel.Manage),
                         ("ruleless", PrinterGroupAccessLevel.Manage),
                         ("ungrouped", PrinterGroupAccessLevel.Manage),
                     })
            {
                Guid? groupId = null;
                if (name != "ungrouped")
                {
                    var group = new PrinterGroup { Id = Guid.NewGuid(), Name = name };
                    groupId = group.Id;
                    Db.Add(group);
                    if (name != "ruleless")
                    {
                        var role = new Role { Id = Guid.NewGuid(), Name = name, IsActive = name != "inactiveRole" };
                        Db.AddRange(role, new PrinterGroupAccess
                        {
                            Id = Guid.NewGuid(),
                            PrinterGroupId = group.Id,
                            RoleId = role.Id,
                            AccessLevel = level,
                        });
                        if (name != "foreign")
                        {
                            Db.UserRoles.Add(new UserRole
                            {
                                Id = Guid.NewGuid(),
                                UserId = Actor,
                                RoleId = role.Id,
                                IsActive = name != "inactiveAssignment",
                                ExpiresAt = name == "expired" ? DateTime.UtcNow.AddDays(-1) : null,
                            });
                        }
                    }
                }

                foreach (bool enabled in new[] { true, false })
                {
                    Db.Printers.Add(new Printer
                    {
                        Id = Guid.NewGuid(),
                        Name = $"{name}-{enabled}",
                        ServerUrl = $"http://{name}.invalid",
                        PrinterGroupId = groupId,
                        IsEnabled = enabled,
                    });
                }

                foreach (Guid? projectId in new Guid?[] { null, owned.Id, foreign.Id })
                {
                    Db.GcodeFiles.Add(new GcodeFile
                    {
                        Id = Guid.NewGuid(),
                        FileName = $"{name}.gcode",
                        FileHash = Guid.NewGuid().ToString(),
                        FolderId = folder.Id,
                        PrinterGroupId = groupId,
                        CalibrationProjectId = projectId,
                    });
                }
            }

            await Db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
