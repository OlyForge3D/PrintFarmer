using System.Data.Common;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
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
/// Tests for issue #1704: <see cref="JobQueueAnalyticsController.GetPrinterQueueSummariesAsync"/>
/// used to authorize each returned printer summary one-at-a-time via
/// <c>CanAccessPrinterAsync</c>, reintroducing the N-per-printer round trips the endpoint's own
/// doc comment says it exists to remove. It now performs a single batched
/// <c>FilterAccessiblePrinterIdsAsync</c> call, mirroring the fix already applied to the sibling
/// <see cref="JobQueueAnalyticsController.GetAllQueueAsync"/> action for #1496. These tests
/// prove: (1) the controller no longer issues one authorization call per printer, (2) a farm
/// admin still sees every summary via the batched call's own zero-query claims-based
/// short-circuit, and (3) the authorized printer set produced by the batched call is IDENTICAL
/// to what the old per-printer loop would have produced for a farm admin, a partial-access
/// operator, a no-access user, and a printer with <c>PrinterGroupId == null</c>.
/// </summary>
public class JobQueueAnalyticsControllerPrinterSummariesBatchedAuthorizationTests
{
    private readonly Mock<IPrintJobManagementService> _printJobManagementServiceMock = new();
    private readonly Mock<IJobCostCalculationService> _jobCostCalculationServiceMock = new();

    private JobQueueAnalyticsController CreateController(
        IQueueResourceAuthorizationService? resourceAuthorization,
        ClaimsPrincipal principal)
    {
        var controller = new JobQueueAnalyticsController(
            _printJobManagementServiceMock.Object,
            _jobCostCalculationServiceMock.Object,
            NullLogger<JobQueueAnalyticsController>.Instance,
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

    private static PrinterQueueSummaryDto MakeSummary(Guid printerId) => new(printerId, 1, 0, null);

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_FarmAdmin_ReturnsAllSummariesWithoutAnyDbBackedAuthorizationCall()
    {
        List<PrinterQueueSummaryDto> summaries =
            [MakeSummary(Guid.NewGuid()), MakeSummary(Guid.NewGuid()), MakeSummary(Guid.NewGuid())];
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(summaries);

        // FilterAccessiblePrinterIdsAsync has its own zero-query claims-based farm-admin
        // short-circuit (unlike FilterActorAccessibleJobIdsAsync), so the controller calls it
        // unconditionally rather than adding a duplicate fast path -- the mock still returns
        // every printer id, proving the end-to-end behavior without asserting on the internal
        // short-circuit (that is covered by QueueResourceAuthorizationService's own tests below).
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClaimsPrincipal _, IReadOnlyCollection<Guid> ids, PrinterGroupAccessLevel _, CancellationToken _) => ids.ToHashSet());
        JobQueueAnalyticsController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(Guid.NewGuid(), isFarmAdmin: true));

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IEnumerable<PrinterQueueSummaryDto>>(okResult.Value).ToList();
        returned.Should().BeEquivalentTo(summaries);

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
    public async Task GetPrinterQueueSummariesAsync_NonAdmin_CallsBatchedFilterExactlyOnceInsteadOfPerPrinterLoop()
    {
        Guid visiblePrinterId = Guid.NewGuid();
        Guid hiddenPrinterId = Guid.NewGuid();
        List<PrinterQueueSummaryDto> summaries = [MakeSummary(visiblePrinterId), MakeSummary(hiddenPrinterId)];
        _printJobManagementServiceMock
            .Setup(s => s.GetPrinterQueueSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(summaries);

        Guid userId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(visiblePrinterId) && ids.Contains(hiddenPrinterId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { visiblePrinterId });
        JobQueueAnalyticsController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(userId));

        IActionResult result = await controller.GetPrinterQueueSummariesAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IEnumerable<PrinterQueueSummaryDto>>(okResult.Value).ToList();
        PrinterQueueSummaryDto onlyEntry = Assert.Single(returned);
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
    /// Correctness gate for the refactor: for a farm admin, a partial-access operator, a
    /// no-access user, and a printer with <c>PrinterGroupId == null</c>, the authorized printer
    /// set produced by the new batched
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

        // --- Partial-access operator: can view the allowed group's printer and the
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

        // --- No-access user: has no role granting access to the restricted group, and no
        // role at all granting the allowed group, but still sees the no-group printer ---
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

        // --- Query-count proof: the batched call issues a constant number of round trips
        // regardless of candidate printer count, unlike the old per-printer loop ---
        await using (var countContext = new AppDbContext(options))
        {
            var countAuthorization = new QueueResourceAuthorizationService(countContext);
            ClaimsPrincipal operatorPrincipal = CreatePrincipal(operatorUserId);

            interceptor.Reset();
            foreach (Guid printerId in candidatePrinterIds)
            {
                _ = await countAuthorization.CanAccessPrinterAsync(operatorPrincipal, printerId, PrinterGroupAccessLevel.View);
            }
            int oldStyleCommandCount = interceptor.CommandCount;

            interceptor.Reset();
            _ = await countAuthorization.FilterAccessiblePrinterIdsAsync(
                operatorPrincipal,
                candidatePrinterIds,
                PrinterGroupAccessLevel.View);
            int newStyleCommandCount = interceptor.CommandCount;

            newStyleCommandCount.Should().BeLessThan(
                oldStyleCommandCount,
                "the batched call must issue fewer round trips than looping the single-printer check across the same candidates");
            // 3 queries total regardless of printer count: Printers scope lookup,
            // PrinterGroupAccesses rules lookup, UserRoles lookup.
            newStyleCommandCount.Should().Be(3);
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
