using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.SignalR;
using Farm.Modules.Inventory.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Inventory.Tests.Controllers;

public sealed class FilamentFallbackGroupsResourceAuthorizationTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"fallback-authorization-{Guid.NewGuid()}")
        .Options);
    private readonly ClaimsPrincipal _user;
    private readonly FilamentFallbackGroupService _service;
    private readonly FilamentFallbackGroupsController _controller;
    private readonly Mock<IHubContext<PrinterHub>> _hub = new(MockBehavior.Strict);
    private readonly Guid _allowedPrinterId = Guid.NewGuid();
    private readonly Guid _deniedPrinterId = Guid.NewGuid();
    private readonly Guid _missingPrinterId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();
    private readonly Guid _backupId = Guid.NewGuid();
    private readonly Guid _deniedSourceId = Guid.NewGuid();
    private readonly Guid _allowedGroupId = Guid.NewGuid();
    private readonly Guid _deniedGroupId = Guid.NewGuid();

    public FilamentFallbackGroupsResourceAuthorizationTests()
    {
        Guid userId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        Guid allowedGroup = Guid.NewGuid();
        Guid deniedGroup = Guid.NewGuid();
        _user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        _db.PrinterGroups.AddRange(
            new PrinterGroup { Id = allowedGroup, Name = "Allowed" },
            new PrinterGroup { Id = deniedGroup, Name = "Denied" });
        _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, IsActive = true });
        _db.PrinterGroupAccesses.AddRange(
            new PrinterGroupAccess { PrinterGroupId = allowedGroup, RoleId = roleId, AccessLevel = PrinterGroupAccessLevel.View },
            new PrinterGroupAccess { PrinterGroupId = deniedGroup, RoleId = Guid.NewGuid(), AccessLevel = PrinterGroupAccessLevel.View });
        SeedPrinter(_allowedPrinterId, allowedGroup, _allowedGroupId, _sourceId, _backupId);
        SeedPrinter(_deniedPrinterId, deniedGroup, _deniedGroupId, _deniedSourceId, Guid.NewGuid());
        _db.SaveChanges();

        _service = new FilamentFallbackGroupService(
            _db, NullLogger<FilamentFallbackGroupService>.Instance, new QueueResourceAuthorizationService(_db));
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        _controller = new FilamentFallbackGroupsController(
            _service, gate.Object, _hub.Object, NullLogger<FilamentFallbackGroupsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = _user } }
        };
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("available")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Endpoints_DeniedAndMissingPrinter_ReturnSame404WithoutMutationOrBroadcast(string operation)
    {
        IActionResult denied = await InvokeEndpointAsync(operation, _deniedPrinterId, _deniedGroupId, _deniedSourceId);
        IActionResult missing = await InvokeEndpointAsync(operation, _missingPrinterId, _deniedGroupId, _deniedSourceId);

        if (denied is NotFoundObjectResult deniedObject)
        {
            NotFoundObjectResult missingObject = missing.Should().BeOfType<NotFoundObjectResult>().Subject;
            deniedObject.Value.Should().BeEquivalentTo(missingObject.Value);
        }
        else
        {
            denied.Should().BeOfType<NotFoundResult>();
            missing.Should().BeOfType<NotFoundResult>();
        }

        (await _db.FilamentFallbackGroups.CountAsync()).Should().Be(2);
        (await _db.FilamentFallbackGroups.FindAsync(_deniedGroupId))!.Name.Should().Be("Original");
        _hub.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("available")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("batch")]
    public async Task Service_DeniedAndMissingPrinter_ThrowsUniformNotFound(string operation)
    {
        Func<Task> denied = () => InvokeServiceAsync(operation, _user, _deniedPrinterId);
        Func<Task> missing = () => InvokeServiceAsync(operation, _user, _missingPrinterId);
        await denied.Should().ThrowAsync<KeyNotFoundException>().WithMessage("Printer not found.");
        await missing.Should().ThrowAsync<KeyNotFoundException>().WithMessage("Printer not found.");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("available")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("batch")]
    public async Task Service_UnauthenticatedCaller_RejectsAccess(string operation)
    {
        Func<Task> act = () => InvokeServiceAsync(operation, new ClaimsPrincipal(new ClaimsIdentity()), _allowedPrinterId);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Service_AllowedPrinter_PreservesReadsAndMutations()
    {
        IReadOnlyList<FilamentFallbackGroupDto> list = await _service.ListForPrinterAsync(_user, _allowedPrinterId, default);
        list.Should().ContainSingle().Which.Id.Should().Be(_allowedGroupId);
        (await _controller.ListAsync(_allowedPrinterId, default)).Result.Should().BeOfType<OkObjectResult>();
        (await _controller.GetAsync(_allowedPrinterId, _allowedGroupId, default)).Result.Should().BeOfType<OkObjectResult>();
        (await _controller.GetAvailableFallbackAsync(_allowedPrinterId, _sourceId, "PLA", default))
            .Result.Should().BeOfType<OkObjectResult>();
        AvailableFallbackMember? fallback = await _service.FindAvailableFallbackAsync(_user, _allowedPrinterId, _sourceId, "PLA", default);
        fallback!.ToolheadId.Should().Be(_backupId);
        fallback.LoadedSpoolId.Should().Be(42);

        FilamentFallbackGroupDto created = await _service.CreateAsync(
            _user, _allowedPrinterId, new("New", "PLA", null, [_sourceId, _backupId]), default);
        (await _service.GetAsync(_user, _allowedPrinterId, created.Id, default))!.Name.Should().Be("New");
        FilamentFallbackGroupDto updated = await _service.UpdateAsync(
            _user, _allowedPrinterId, created.Id, new("Updated", "PETG", null, [_backupId, _sourceId]), default);
        updated.Name.Should().Be("Updated");
        updated.Members[0].ToolheadId.Should().Be(_backupId);
        await _service.DeleteAsync(_user, _allowedPrinterId, created.Id, default);
        (await _service.GetAsync(_user, _allowedPrinterId, created.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task GetAvailableFallbacksAsync_MixedScope_RejectsEntireBatch()
    {
        foreach (Guid inaccessibleId in new[] { _deniedPrinterId, _missingPrinterId })
        {
            Func<Task> act = () => _service.GetAvailableFallbacksAsync(_user, [_allowedPrinterId, inaccessibleId], default);
            await act.Should().ThrowAsync<KeyNotFoundException>();
        }

        var results = await _service.GetAvailableFallbacksAsync(_user, [_allowedPrinterId, _allowedPrinterId], default);
        results.Should().ContainSingle();
        results.Keys.Should().OnlyContain(key => key.PrinterId == _allowedPrinterId);
    }

    [Fact]
    public async Task BackgroundResolver_WithoutHttpCaller_PreservesTrustedResolution()
    {
        var results = await ((IFilamentFallbackGroupResolver)_service)
            .GetAvailableFallbacksAsync([_allowedPrinterId, _deniedPrinterId], default);
        results.Keys.Select(key => key.PrinterId).Should().BeEquivalentTo([_allowedPrinterId, _deniedPrinterId]);
    }

    [Fact]
    public async Task Endpoints_ForeignOrMissingChild_DoNotExposeOrModifyOtherPrinter()
    {
        foreach (Guid groupId in new[] { _deniedGroupId, Guid.NewGuid() })
        {
            (await _controller.GetAsync(_allowedPrinterId, groupId, default)).Result.Should().BeOfType<NotFoundResult>();
            (await _controller.UpdateAsync(_allowedPrinterId, groupId,
                new("Changed", "PLA", null, [_sourceId, _backupId]), default))
                .Result.Should().BeOfType<NotFoundObjectResult>();
            // Existing delete contract is idempotent for an absent group on an accessible printer.
            await _service.DeleteAsync(_user, _allowedPrinterId, groupId, default);
        }

        foreach (Guid toolheadId in new[] { _deniedSourceId, Guid.NewGuid() })
        {
            (await _controller.GetAvailableFallbackAsync(_allowedPrinterId, toolheadId, "PLA", default))
                .Result.Should().BeOfType<NotFoundResult>();
        }

        (await _db.FilamentFallbackGroups.FindAsync(_deniedGroupId))!.Name.Should().Be("Original");
        _hub.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Endpoints_AllowedPrinterWithoutFallback_PreservesEmptyResponses()
    {
        _db.FilamentFallbackGroups.Remove((await _db.FilamentFallbackGroups.FindAsync(_allowedGroupId))!);
        await _db.SaveChangesAsync();
        (await _service.ListForPrinterAsync(_user, _allowedPrinterId, default)).Should().BeEmpty();
        (await _controller.GetAvailableFallbackAsync(_allowedPrinterId, _sourceId, "PLA", default))
            .Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Service_AdminWithMissingPrinter_StillReturnsNotFound()
    {
        ClaimsPrincipal admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "farm_admin")], "Test"));
        Func<Task> missing = () => _service.ListForPrinterAsync(admin, _missingPrinterId, default);
        await missing.Should().ThrowAsync<KeyNotFoundException>().WithMessage("Printer not found.");
        (await _service.ListForPrinterAsync(admin, _deniedPrinterId, default)).Should().ContainSingle();
    }

    private async Task<IActionResult> InvokeEndpointAsync(string operation, Guid printerId, Guid groupId, Guid sourceId) =>
        operation switch
        {
            "list" => (await _controller.ListAsync(printerId, default)).Result!,
            "get" => (await _controller.GetAsync(printerId, groupId, default)).Result!,
            "available" => (await _controller.GetAvailableFallbackAsync(printerId, sourceId, "PLA", default)).Result!,
            "create" => (await _controller.CreateAsync(printerId, new("New", "PLA", null, [_sourceId, _backupId]), default)).Result!,
            "update" => (await _controller.UpdateAsync(printerId, groupId, new("Updated", "PLA", null, [_sourceId, _backupId]), default)).Result!,
            "delete" => await _controller.DeleteAsync(printerId, groupId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private Task InvokeServiceAsync(string operation, ClaimsPrincipal principal, Guid printerId) =>
        operation switch
        {
            "list" => _service.ListForPrinterAsync(principal, printerId, default),
            "get" => _service.GetAsync(principal, printerId, _deniedGroupId, default),
            "available" => _service.FindAvailableFallbackAsync(principal, printerId, _deniedSourceId, "PLA", default),
            "create" => _service.CreateAsync(principal, printerId, new("New", "PLA", null, [_sourceId, _backupId]), default),
            "update" => _service.UpdateAsync(principal, printerId, _deniedGroupId, new("Updated", "PLA", null, [_sourceId, _backupId]), default),
            "delete" => _service.DeleteAsync(principal, printerId, _deniedGroupId, default),
            "batch" => _service.GetAvailableFallbacksAsync(principal, [printerId], default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private void SeedPrinter(Guid printerId, Guid printerGroupId, Guid fallbackGroupId, Guid sourceId, Guid backupId)
    {
        _db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Printer",
            PrinterGroupId = printerGroupId,
            IsEnabled = true,
            ServerUrl = $"http://{printerId}.local",
        });
        _db.Toolheads.AddRange(
            new Toolhead { Id = sourceId, PrinterId = printerId, Index = 0, ToolheadType = ToolheadType.Physical },
            new Toolhead
            {
                Id = backupId,
                PrinterId = printerId,
                Index = 1,
                ToolheadType = ToolheadType.Physical,
                CurrentMaterial = "PLA",
                CurrentSpoolId = 42,
            });
        _db.FilamentFallbackGroups.Add(new FilamentFallbackGroup
        {
            Id = fallbackGroupId,
            PrinterId = printerId,
            Name = "Original",
            NameNormalized = "original",
            MaterialType = "PLA",
            Members =
            [
                new FilamentFallbackGroupMember { Id = Guid.NewGuid(), FallbackGroupId = fallbackGroupId, ToolheadId = sourceId, Position = 0 },
                new FilamentFallbackGroupMember { Id = Guid.NewGuid(), FallbackGroupId = fallbackGroupId, ToolheadId = backupId, Position = 1 },
            ],
        });
    }
}
