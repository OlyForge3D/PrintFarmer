using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Infrastructure.Tests.Services;

public sealed class PrinterFileDeleteAccessTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Delete_RequiresCurrentExplicitDeleteAndSourceManage_NotSubmitOrQueueWrite(
        bool deletePermission, bool manage, bool expected)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid actor = Guid.NewGuid();
        Guid source = Guid.NewGuid();
        db.Users.Add(new User { Id = actor, Username = "delete-test", IsActive = true });
        db.Printers.Add(new Printer
        {
            Id = source,
            Name = "fake",
            ServerUrl = "http://printer.invalid",
            IsEnabled = true,
        });
        await db.SaveChangesAsync();
        var users = new Mock<IUsersRepository>();
        List<(string Resource, string Action)> grants =
        [
            ("printer_files", "read"), ("printer_files", "copy"), ("queue", "write"),
            ("gcode_library", "delete"), ("gcode_library", "read"), ("gcode_library", "create"),
        ];
        if (deletePermission)
        {
            grants.Add(("printer_files", "delete"));
        }

        users.Setup(x => x.GetGrantedPermissionsAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(grants);
        users.Setup(x => x.GetDeniedPermissionsAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)>());
        var resources = new Mock<IQueueResourceAuthorizationService>();
        resources.Setup(x => x.CanActorAccessPrinterAsync(actor.ToString(), source,
                It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid _, PrinterGroupAccessLevel level, CancellationToken _) =>
                level != PrinterGroupAccessLevel.Manage || manage);
        var access = new PrinterFileAccessService(db, users.Object, resources.Object);
        Assert.Equal(expected, await access.CanActAsync(actor, source, PrinterFileAction.Delete));
        (await db.Users.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await access.CanActAsync(actor, source, PrinterFileAction.Delete));
    }
}
