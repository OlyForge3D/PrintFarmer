// <copyright file="JobQueueControllerBatchedAuthorizationTests.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Data.Common;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.PrintQueue.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Controllers;

/// <summary>
/// Tests for issue #1729: <see cref="JobQueueController.GetQueueAsync"/> used to authorize each
/// returned queue overview entry one-at-a-time via <c>CanAccessPrinterAsync</c>, costing 2-4 SQL
/// queries per printer (N+1 on the polled queue dashboard endpoint). It now performs a single
/// batched <c>FilterAccessiblePrinterIdsAsync</c> call, mirroring the identical fix already
/// applied to the sibling <see cref="Farm.Modules.PrintQueue.Controllers.JobQueueAnalyticsController.GetPrinterQueueSummariesAsync"/>
/// action for #1704/#1496. These tests prove: (1) the controller no longer issues one
/// authorization call per printer, (2) a farm admin still sees every queue entry via the batched
/// call's own zero-query claims-based short-circuit, (3) the authorized printer set produced by
/// the batched call is IDENTICAL to what the old per-printer loop would have produced for a
/// restricted user WITH group access, a restricted user WITHOUT access, a printer with no group
/// assigned, and a farm admin, and (4) for a 40-printer farm the batched call issues a single-digit
/// number of SQL commands versus the ~80-200 the old per-printer loop would issue.
/// </summary>
public class JobQueueControllerBatchedAuthorizationTests
{
    private readonly Mock<IJobQueueService> _queueServiceMock = new();
    private readonly Mock<IPrintJobManagementService> _printJobManagementServiceMock = new();
    private readonly Mock<ILogger<JobQueueController>> _loggerMock = new();
    private readonly Mock<IPrintJobCompletionService> _printJobCompletionServiceMock = new();
    private readonly Mock<IJobDispatchService> _jobDispatchServiceMock = new();
    private readonly Mock<IBatchDispatchService> _batchDispatchServiceMock = new();
    private readonly Mock<IBedClearAcknowledgementService> _bedClearAcknowledgementServiceMock = new();
    private readonly Mock<IPrinterStatusCacheReader> _printerStatusCacheMock = new();
    private readonly Mock<IPrintFarmerTelemetryService> _telemetryServiceMock = new();
    private readonly Mock<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService> _partHarvestServiceMock = new();
    private readonly Mock<IOperatorFeatureGate> _operatorFeatureGateMock = new();

    private JobQueueController CreateController(
        IQueueResourceAuthorizationService? resourceAuthorization,
        ClaimsPrincipal principal)
    {
        var controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _jobDispatchServiceMock.Object,
            _batchDispatchServiceMock.Object,
            _bedClearAcknowledgementServiceMock.Object,
            _printerStatusCacheMock.Object,
            _telemetryServiceMock.Object,
            _partHarvestServiceMock.Object,
            _operatorFeatureGateMock.Object,
            _loggerMock.Object,
            db: null,
            resourceAuthorization: resourceAuthorization);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static ClaimsPrincipal CreatePrincipal(Guid userId, bool isFarmAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (isFarmAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static QueueOverviewDto MakeOverview(Guid printerId) => new()
    {
        PrinterId = printerId,
        PrinterName = $"printer-{printerId}",
        PrinterModel = "Test Model",
        IsAvailable = true,
        QueuedJobsCount = 0,
    };

    [Fact]
    public async Task GetQueueAsync_FarmAdmin_ReturnsAllEntriesWithoutAnyDbBackedAuthorizationCall()
    {
        List<QueueOverviewDto> overview =
            [MakeOverview(Guid.NewGuid()), MakeOverview(Guid.NewGuid()), MakeOverview(Guid.NewGuid())];
        _queueServiceMock
            .Setup(s => s.GetQueueOverviewAsync(null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(overview);

        // FilterAccessiblePrinterIdsAsync has its own zero-query claims-based farm-admin
        // short-circuit, so the controller calls it unconditionally rather than adding a
        // duplicate fast path -- the mock still returns every printer id, proving the
        // end-to-end behavior without asserting on the internal short-circuit (that is covered
        // by QueueResourceAuthorizationService's own tests below).
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClaimsPrincipal _, IReadOnlyCollection<Guid> ids, PrinterGroupAccessLevel _, CancellationToken _) => ids.ToHashSet());
        JobQueueController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(Guid.NewGuid(), isFarmAdmin: true));

        ActionResult<IEnumerable<QueueOverviewDto>> result = await controller.GetQueueAsync();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IEnumerable<QueueOverviewDto>>(okResult.Value).ToList();
        returned.Should().BeEquivalentTo(overview);

        resourceAuthorization.Verify(
            r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetQueueAsync_NonAdmin_CallsBatchedFilterExactlyOnceInsteadOfPerPrinterLoop()
    {
        Guid visiblePrinterId = Guid.NewGuid();
        Guid hiddenPrinterId = Guid.NewGuid();
        List<QueueOverviewDto> overview = [MakeOverview(visiblePrinterId), MakeOverview(hiddenPrinterId)];
        _queueServiceMock
            .Setup(s => s.GetQueueOverviewAsync(null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(overview);

        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(visiblePrinterId) && ids.Contains(hiddenPrinterId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { visiblePrinterId });
        JobQueueController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(Guid.NewGuid()));

        ActionResult<IEnumerable<QueueOverviewDto>> result = await controller.GetQueueAsync();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IEnumerable<QueueOverviewDto>>(okResult.Value).ToList();
        QueueOverviewDto onlyEntry = Assert.Single(returned);
        Assert.Equal(visiblePrinterId, onlyEntry.PrinterId);

        resourceAuthorization.Verify(
            r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        resourceAuthorization.Verify(
            r => r.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Correctness gate for the refactor: for a farm admin, a restricted user WITH group access,
    /// a restricted user WITHOUT access, and a printer with <c>PrinterGroupId == null</c>, the
    /// authorized printer set produced by the new batched
    /// <see cref="IQueueResourceAuthorizationService.FilterAccessiblePrinterIdsAsync"/> call must
    /// be IDENTICAL to what looping <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/>
    /// per printer (the old behavior) would have produced. Runs against a real Sqlite connection
    /// (not the EF InMemory provider) because the point of this fix is the SQL round-trip count,
    /// and only a relational provider can prove that count. The farm admin case also asserts
    /// zero DB commands via <see cref="CommandCountingInterceptor"/>.
    /// </summary>
    [Fact]
    public async Task FilterAccessiblePrinterIdsAsync_ProducesIdenticalSetToPerPrinterLoop_ForAllPrincipalsAndNullGroupPrinter()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        Guid operatorUserId = Guid.NewGuid();
        Guid noAccessUserId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        Guid allowedGroupId = Guid.NewGuid();
        Guid restrictedGroupId = Guid.NewGuid();
        Guid operatorRoleId = Guid.NewGuid();
        Guid otherRoleId = Guid.NewGuid();

        Guid printerInAllowedGroupId = Guid.NewGuid();
        Guid printerInRestrictedGroupId = Guid.NewGuid();
        Guid printerWithNoGroupId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        await using (var seed = new AppDbContext(options))
        {
            seed.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = "Test Manufacturer",
                IsActive = true,
            });
            seed.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test Model",
                ManufacturerId = manufacturerId,
            });
            seed.Users.AddRange(
                new User
                {
                    Id = operatorUserId,
                    Username = "operator-user",
                    Email = "operator-user@example.test",
                    PasswordHash = "test-hash",
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new User
                {
                    Id = noAccessUserId,
                    Username = "no-access-user",
                    Email = "no-access-user@example.test",
                    PasswordHash = "test-hash",
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            seed.Roles.AddRange(
                new Role
                {
                    Id = operatorRoleId,
                    Name = "operator-role",
                    DisplayName = "Operator Role",
                    IsActive = true,
                    IsSystemRole = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Role
                {
                    Id = otherRoleId,
                    Name = "other-role",
                    DisplayName = "Other Role",
                    IsActive = true,
                    IsSystemRole = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            seed.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = operatorUserId,
                RoleId = operatorRoleId,
                AssignedAt = now,
                IsActive = true,
            });
            seed.PrinterGroups.AddRange(
                new PrinterGroup
                {
                    Id = allowedGroupId,
                    Name = "Allowed Group",
                    CreatedDate = now,
                    UpdatedDate = now,
                },
                new PrinterGroup
                {
                    Id = restrictedGroupId,
                    Name = "Restricted Group",
                    CreatedDate = now,
                    UpdatedDate = now,
                });
            seed.PrinterGroupAccesses.Add(new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = allowedGroupId,
                RoleId = operatorRoleId,
                AccessLevel = PrinterGroupAccessLevel.View,
                CreatedDate = now,
            });
            seed.PrinterGroupAccesses.Add(new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = restrictedGroupId,
                RoleId = otherRoleId,
                AccessLevel = PrinterGroupAccessLevel.View,
                CreatedDate = now,
            });
            seed.Printers.AddRange(
                new Printer
                {
                    Id = printerInAllowedGroupId,
                    Name = "allowed-group-printer",
                    ServerUrl = "http://allowed-group-printer.test",
                    PrinterGroupId = allowedGroupId,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                },
                new Printer
                {
                    Id = printerInRestrictedGroupId,
                    Name = "restricted-group-printer",
                    ServerUrl = "http://restricted-group-printer.test",
                    PrinterGroupId = restrictedGroupId,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                },
                new Printer
                {
                    Id = printerWithNoGroupId,
                    Name = "no-group-printer",
                    ServerUrl = "http://no-group-printer.test",
                    PrinterGroupId = null,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                });
            await seed.SaveChangesAsync();
        }

        Guid[] candidatePrinterIds = [printerInAllowedGroupId, printerInRestrictedGroupId, printerWithNoGroupId];

        // --- Farm admin: claims-based fast path inside FilterAccessiblePrinterIdsAsync itself,
        // zero DB commands, sees everything ---
        interceptor.Reset();
        await using (var adminContext = new AppDbContext(options))
        {
            var adminAuthorization = new QueueResourceAuthorizationService(adminContext);
            ClaimsPrincipal adminPrincipal = CreatePrincipal(Guid.NewGuid(), isFarmAdmin: true);

            HashSet<Guid> adminOldStyle = [];
            foreach (Guid printerId in candidatePrinterIds)
            {
                if (await adminAuthorization.CanAccessPrinterAsync(adminPrincipal, printerId, PrinterGroupAccessLevel.View))
                {
                    adminOldStyle.Add(printerId);
                }
            }

            interceptor.CommandCount.Should().Be(
                0,
                "the claims-based farm-admin check is a zero-query fast path and must not fall through to the DB-backed check");

            IReadOnlySet<Guid> adminNewStyle = await adminAuthorization.FilterAccessiblePrinterIdsAsync(
                adminPrincipal,
                candidatePrinterIds,
                PrinterGroupAccessLevel.View);

            interceptor.CommandCount.Should().Be(
                0,
                "FilterAccessiblePrinterIdsAsync has its own claims-based farm-admin short-circuit and must not issue any DB commands for a farm admin");
            adminNewStyle.Should().BeEquivalentTo(adminOldStyle);
            adminOldStyle.Should().BeEquivalentTo(candidatePrinterIds);
        }

        // --- Restricted user WITH group access: can view the allowed group's printer and the
        // no-group printer (open to all users by default), but not the restricted one ---
        await using (var operatorContext = new AppDbContext(options))
        {
            var operatorAuthorization = new QueueResourceAuthorizationService(operatorContext);
            ClaimsPrincipal operatorPrincipal = CreatePrincipal(operatorUserId);

            HashSet<Guid> operatorOldStyle = [];
            foreach (Guid printerId in candidatePrinterIds)
            {
                if (await operatorAuthorization.CanAccessPrinterAsync(operatorPrincipal, printerId, PrinterGroupAccessLevel.View))
                {
                    operatorOldStyle.Add(printerId);
                }
            }

            IReadOnlySet<Guid> operatorNewStyle = await operatorAuthorization.FilterAccessiblePrinterIdsAsync(
                operatorPrincipal,
                candidatePrinterIds,
                PrinterGroupAccessLevel.View);

            operatorNewStyle.Should().BeEquivalentTo(operatorOldStyle);
            operatorOldStyle.Should().BeEquivalentTo(new[] { printerInAllowedGroupId, printerWithNoGroupId });
        }

        // --- Restricted user WITHOUT access: has no role granting access to the restricted
        // group, and no role at all granting the allowed group, but still sees the no-group
        // printer ---
        await using (var noAccessContext = new AppDbContext(options))
        {
            var noAccessAuthorization = new QueueResourceAuthorizationService(noAccessContext);
            ClaimsPrincipal noAccessPrincipal = CreatePrincipal(noAccessUserId);

            HashSet<Guid> noAccessOldStyle = [];
            foreach (Guid printerId in candidatePrinterIds)
            {
                if (await noAccessAuthorization.CanAccessPrinterAsync(noAccessPrincipal, printerId, PrinterGroupAccessLevel.View))
                {
                    noAccessOldStyle.Add(printerId);
                }
            }

            IReadOnlySet<Guid> noAccessNewStyle = await noAccessAuthorization.FilterAccessiblePrinterIdsAsync(
                noAccessPrincipal,
                candidatePrinterIds,
                PrinterGroupAccessLevel.View);

            noAccessNewStyle.Should().BeEquivalentTo(noAccessOldStyle);
            noAccessOldStyle.Should().BeEquivalentTo(new[] { printerWithNoGroupId });
        }
    }

    /// <summary>
    /// Acceptance-criteria query-count proof for a 40-printer farm authenticated as a
    /// group-restricted (non-admin) user: the old per-printer loop issues ~80-200 SQL commands
    /// (2-4 per printer via <see cref="IQueueResourceAuthorizationService.CanAccessPrinterAsync"/>),
    /// while the new batched <see cref="IQueueResourceAuthorizationService.FilterAccessiblePrinterIdsAsync"/>
    /// call issues a single-digit, constant number of commands regardless of printer count.
    /// </summary>
    [Fact]
    public async Task FortyPrinterFarm_RestrictedNonAdminUser_BatchedCallIssuesSingleDigitCommandsVersusLoopsEightyToTwoHundred()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        Guid operatorUserId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        Guid allowedGroupId = Guid.NewGuid();
        Guid operatorRoleId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        const int printerCount = 40;
        var printerIds = new Guid[printerCount];
        for (int i = 0; i < printerCount; i++)
        {
            printerIds[i] = Guid.NewGuid();
        }

        await using (var seed = new AppDbContext(options))
        {
            seed.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = "Test Manufacturer",
                IsActive = true,
            });
            seed.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test Model",
                ManufacturerId = manufacturerId,
            });
            seed.Users.Add(new User
            {
                Id = operatorUserId,
                Username = "operator-user",
                Email = "operator-user@example.test",
                PasswordHash = "test-hash",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            seed.Roles.Add(new Role
            {
                Id = operatorRoleId,
                Name = "operator-role",
                DisplayName = "Operator Role",
                IsActive = true,
                IsSystemRole = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
            seed.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = operatorUserId,
                RoleId = operatorRoleId,
                AssignedAt = now,
                IsActive = true,
            });
            seed.PrinterGroups.Add(new PrinterGroup
            {
                Id = allowedGroupId,
                Name = "Allowed Group",
                CreatedDate = now,
                UpdatedDate = now,
            });
            seed.PrinterGroupAccesses.Add(new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = allowedGroupId,
                RoleId = operatorRoleId,
                AccessLevel = PrinterGroupAccessLevel.View,
                CreatedDate = now,
            });
            foreach (Guid printerId in printerIds)
            {
                seed.Printers.Add(new Printer
                {
                    Id = printerId,
                    Name = $"printer-{printerId}",
                    ServerUrl = $"http://printer-{printerId}.test",
                    PrinterGroupId = allowedGroupId,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                });
            }

            await seed.SaveChangesAsync();
        }

        await using (var countContext = new AppDbContext(options))
        {
            var countAuthorization = new QueueResourceAuthorizationService(countContext);
            ClaimsPrincipal operatorPrincipal = CreatePrincipal(operatorUserId);

            interceptor.Reset();
            foreach (Guid printerId in printerIds)
            {
                _ = await countAuthorization.CanAccessPrinterAsync(operatorPrincipal, printerId, PrinterGroupAccessLevel.View);
            }

            int oldStyleCommandCount = interceptor.CommandCount;

            interceptor.Reset();
            IReadOnlySet<Guid> newStyleResult = await countAuthorization.FilterAccessiblePrinterIdsAsync(
                operatorPrincipal,
                printerIds,
                PrinterGroupAccessLevel.View);
            int newStyleCommandCount = interceptor.CommandCount;

            newStyleResult.Should().BeEquivalentTo(printerIds);
            oldStyleCommandCount.Should().BeInRange(
                80,
                200,
                "the per-printer loop baseline (2-4 SQL commands per printer across 40 printers) is what this fix eliminates");
            newStyleCommandCount.Should().BeLessThan(
                10,
                "the batched call must issue a single-digit, constant number of round trips regardless of printer count");
        }
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset() => CommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
