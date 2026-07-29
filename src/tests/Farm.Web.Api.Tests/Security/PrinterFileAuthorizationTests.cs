using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

public sealed class PrinterFileAuthorizationTests : IAsyncLifetime
{
    private readonly Mock<IPrintersService> _printers = new();
    private readonly PrinterFileFactory _factory;

    public PrinterFileAuthorizationTests()
    {
        _factory = new PrinterFileFactory(_printers);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task PrinterFiles_CrossUserDenied_ProduceZeroBackendEffects()
    {
        Guid actorId = Guid.NewGuid();
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                PrintFarmerPermissions.Queue.Read,
                PrintFarmerPermissions.Queue.Write));

        HttpResponseMessage list = await client.GetAsync(
            $"/api/printers/{printerId}/files");
        HttpResponseMessage download = await client.GetAsync(
            $"/api/printers/{printerId}/files/download?filename=secret.gcode");
        HttpResponseMessage delete = await client.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/printers/{printerId}/files")
            {
                Content = JsonContent.Create(new { fileName = "secret.gcode" }),
            });

        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _printers.Verify(service => service.GetFileListAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _printers.Verify(service => service.DownloadPrinterFileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _printers.Verify(service => service.DeletePrinterFileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePrinterFile_Authorized_UsesBarrierAndWritesTypedAudit()
    {
        Guid actorId = Guid.NewGuid();
        Guid printerId = await SeedAuthorizedPrinterAsync();
        _printers.Setup(service => service.DeletePrinterFileAsync(
                printerId,
                "reviewed.gcode",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using HttpClient client = CreateOperatorClient(actorId);

        HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/printers/{printerId}/files")
            {
                Content = JsonContent.Create(new { fileName = "reviewed.gcode" }),
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _printers.Verify(service => service.DeletePrinterFileAsync(
            printerId,
            "reviewed.gcode",
            It.IsAny<CancellationToken>()), Times.Once);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterDispatchState state = await db.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        state.PhysicalControlCommandId.Should().BeNull();
        state.PhysicalControlRequiresReconciliation.Should().BeFalse();
        (await db.QueueOperationAudits.CountAsync(audit =>
            audit.PrinterId == printerId &&
            audit.Operation == QueueAuditOperations.PrinterFileDelete)).Should().Be(2);
    }

    [Fact]
    public async Task DeletePrinterFile_BackendException_IsRedactedAndRetainsBarrier()
    {
        Guid actorId = Guid.NewGuid();
        Guid printerId = await SeedAuthorizedPrinterAsync();
        _printers.Setup(service => service.DeletePrinterFileAsync(
                printerId,
                "reviewed.gcode",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "secret=/private/path?token=do-not-return"));
        using HttpClient client = CreateOperatorClient(actorId);

        HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/printers/{printerId}/files")
            {
                Content = JsonContent.Create(new { fileName = "reviewed.gcode" }),
            });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("private");
        body.Should().NotContain("token");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterDispatchState state = await db.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        state.PhysicalControlCommandId.Should().NotBeNull();
        state.PhysicalControlRequiresReconciliation.Should().BeTrue();
    }

    private HttpClient CreateOperatorClient(Guid actorId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                PrintFarmerPermissions.Queue.Read,
                PrintFarmerPermissions.Queue.Write));
        return client;
    }

    private async Task<Guid> SeedAuthorizedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"File maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"File model {Guid.NewGuid():N}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Authorized file printer",
            ServerUrl = $"http://file-printer-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(
            manufacturer,
            model,
            printer,
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return printer.Id;
    }

    private async Task<Guid> SeedRestrictedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"File ACL maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"File ACL model {Guid.NewGuid():N}",
        };
        var group = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"File ACL group {Guid.NewGuid():N}",
        };
        var foreignRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"file-foreign-{Guid.NewGuid():N}",
            DisplayName = "Foreign file role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Restricted file printer",
            ServerUrl = "http://restricted-file-printer",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = group.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(
            manufacturer,
            model,
            group,
            foreignRole,
            printer,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = foreignRole.Id,
                AccessLevel = PrinterGroupAccessLevel.Manage,
            },
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return printer.Id;
    }

    private sealed class PrinterFileFactory(Mock<IPrintersService> printers)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers.Object);
            });
        }
    }
}
