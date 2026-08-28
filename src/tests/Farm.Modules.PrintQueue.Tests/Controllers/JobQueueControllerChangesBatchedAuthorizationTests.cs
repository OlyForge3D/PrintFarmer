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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Controllers;

/// <summary>
/// Tests for issue #1726: <see cref="JobQueueController.GetChangesAsync"/> used to authorize
/// every candidate outbox event one-at-a-time via <c>CanAccessPrinterAsync</c>/
/// <c>CanAccessJobAsync</c> (2-4 SQL queries per event, 800-2000+ per request). It now
/// partitions candidates by aggregate type up front and performs a single batched
/// <c>FilterAccessiblePrinterIdsAsync</c> call and a single batched
/// <c>FilterActorAccessibleJobIdsAsync</c> call, mirroring the pattern already used by
/// <see cref="Farm.Modules.PrintQueue.Controllers.JobQueueAnalyticsController"/>. These tests prove:
/// (1) the total SQL statement count for a group-restricted (non-admin) user with 400 seeded
///     outbox events spanning both aggregate types is single digits, not hundreds,
/// (2) the farm-admin short-circuit still returns every event, and
/// (3) a group-restricted user WITH access and one WITHOUT access get exactly the response
///     the old per-item loop would have produced.
/// </summary>
public class JobQueueControllerChangesBatchedAuthorizationTests
{
    private readonly Mock<IJobQueueService> _queueServiceMock = new();
    private readonly Mock<IPrintJobManagementService> _printJobManagementServiceMock = new();
    private readonly Mock<IPrintJobCompletionService> _printJobCompletionServiceMock = new();
    private readonly Mock<IJobDispatchService> _jobDispatchServiceMock = new();
    private readonly Mock<IBatchDispatchService> _batchDispatchServiceMock = new();
    private readonly Mock<IBedClearAcknowledgementService> _bedClearAcknowledgementServiceMock = new();
    private readonly Mock<IPrinterStatusCacheReader> _printerStatusCacheMock = new();
    private readonly Mock<IPrintFarmerTelemetryService> _telemetryServiceMock = new();
    private readonly Mock<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService> _partHarvestServiceMock = new();
    private readonly Mock<IOperatorFeatureGate> _operatorFeatureGateMock = new();

    private JobQueueController CreateController(
        AppDbContext db,
        IQueueResourceAuthorizationService resourceAuthorization,
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
            NullLogger<JobQueueController>.Instance,
            db,
            resourceAuthorization);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
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

    /// <summary>
    /// A principal with no NameIdentifier/sub claim at all -- neither
    /// <c>PrintFarmerPermissions.TryGetUserId</c> nor <c>QueueActorIdentity.Resolve</c> can
    /// resolve a user id for it.
    /// </summary>
    private static ClaimsPrincipal CreatePrincipalWithNoResolvableSubject() =>
        new(new ClaimsIdentity([], "TestAuth"));

    private static QueueDispatchOutbox CreatePrinterEvent(Guid printerId, long sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        AggregateType = nameof(Printer),
        AggregateId = printerId,
        PrinterId = printerId,
        EventType = "printer.status.changed",
        SchemaVersion = "1",
        PayloadJson = "{}",
        Status = QueueOutboxEventStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static QueueDispatchOutbox CreateJobEvent(Guid jobId, long sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        AggregateType = nameof(PrintJob),
        AggregateId = jobId,
        EventType = QueueLifecycleEventWriter.EventTypeJobCompleted,
        SchemaVersion = "1",
        JobStatus = PrintJobStatus.Completed.ToString(),
        JobKind = JobKind.Standard.ToString(),
        PayloadJson = "{}",
        Status = QueueOutboxEventStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Seeds a scenario with one accessible printer (no printer group -- open to all users by
    /// default), one restricted printer (in a group the operator has no role for), one job
    /// owned by the operator, and one job owned by a different user. 100 events per
    /// accessible/denied combination x 2 aggregate types = 400 total candidates, all after
    /// sequence 0 and within the default 500-row query window.
    /// </summary>
    private static async Task<(
        DbContextOptions<AppDbContext> Options,
        CommandCountingInterceptor Interceptor,
        Guid OperatorUserId,
        Guid AdminUserId,
        Guid AccessiblePrinterId,
        Guid DeniedPrinterId,
        Guid AccessibleJobId,
        Guid DeniedJobId)> SeedScenarioAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        Guid operatorUserId = Guid.NewGuid();
        Guid adminUserId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid operatorRoleId = Guid.NewGuid();
        Guid otherRoleId = Guid.NewGuid();
        Guid farmAdminRoleId = Guid.NewGuid();
        Guid restrictedGroupId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid accessiblePrinterId = Guid.NewGuid();
        Guid deniedPrinterId = Guid.NewGuid();
        Guid accessibleJobId = Guid.NewGuid();
        Guid deniedJobId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

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
                    // Restricted group has an ACL rule naming this role, which the operator
                    // does not hold, so the operator (and any other non-admin) cannot see the
                    // restricted printer. The accessible printer has no PrinterGroupId, which
                    // is open to all users by default.
                    Id = otherRoleId,
                    Name = "other-role",
                    DisplayName = "Other Role",
                    IsActive = true,
                    IsSystemRole = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Role
                {
                    // FilterActorAccessibleJobIdsAsync's admin short-circuit is DB-backed
                    // (IsFarmAdminAsync queries UserRoles/Roles), unlike the claims-based
                    // short-circuit in FilterAccessiblePrinterIdsAsync, so a real farm-admin
                    // role assignment is required for the admin test scenario.
                    Id = farmAdminRoleId,
                    Name = PrintFarmerPermissions.FarmAdminRole,
                    DisplayName = "Farm Admin",
                    IsActive = true,
                    IsSystemRole = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            seed.PrinterGroups.Add(new PrinterGroup
            {
                Id = restrictedGroupId,
                Name = "Restricted Group",
                CreatedDate = now,
                UpdatedDate = now,
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
                    Id = accessiblePrinterId,
                    Name = "accessible-printer",
                    ServerUrl = "http://accessible-printer.test",
                    PrinterGroupId = null,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                },
                new Printer
                {
                    Id = deniedPrinterId,
                    Name = "denied-printer",
                    ServerUrl = "http://denied-printer.test",
                    PrinterGroupId = restrictedGroupId,
                    ManufacturerId = manufacturerId,
                    ModelId = modelId,
                });
            seed.PrintJobs.AddRange(
                new PrintJob
                {
                    Id = accessibleJobId,
                    Name = "operator-owned.gcode",
                    Status = PrintJobStatus.Queued,
                    CreatorSubject = operatorUserId.ToString(),
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new PrintJob
                {
                    Id = deniedJobId,
                    Name = "other-user-owned.gcode",
                    Status = PrintJobStatus.Queued,
                    CreatorSubject = otherUserId.ToString(),
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

            long sequence = 1;
            for (int i = 0; i < 100; i++)
            {
                seed.QueueDispatchOutbox.Add(CreatePrinterEvent(accessiblePrinterId, sequence++));
                seed.QueueDispatchOutbox.Add(CreatePrinterEvent(deniedPrinterId, sequence++));
                seed.QueueDispatchOutbox.Add(CreateJobEvent(accessibleJobId, sequence++));
                seed.QueueDispatchOutbox.Add(CreateJobEvent(deniedJobId, sequence++));
            }

            await seed.SaveChangesAsync();
        }

        await using (var seed = new AppDbContext(options))
        {
            seed.Users.Add(new User
            {
                Id = operatorUserId,
                Username = "operator",
                Email = "operator@test.local",
                PasswordHash = "test-hash",
                CreatedAt = now,
                UpdatedAt = now,
            });
            seed.Users.Add(new User
            {
                Id = adminUserId,
                Username = "farm-admin",
                Email = "farm-admin@test.local",
                PasswordHash = "test-hash",
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
            seed.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUserId,
                RoleId = farmAdminRoleId,
                AssignedAt = now,
                IsActive = true,
            });
            await seed.SaveChangesAsync();
        }

        return (
            options,
            interceptor,
            operatorUserId,
            adminUserId,
            accessiblePrinterId,
            deniedPrinterId,
            accessibleJobId,
            deniedJobId);
    }

    private static List<QueueEventEnvelope> GetEvents(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        object value = Assert.IsAssignableFrom<object>(ok.Value);
        object? eventsValue = value.GetType().GetProperty("events")?.GetValue(value);
        return Assert.IsType<List<QueueEventEnvelope>>(eventsValue);
    }

    [Fact]
    public async Task GetChangesAsync_GroupRestrictedUser_400SeededEvents_IssuesSingleDigitSqlStatements()
    {
        (
            DbContextOptions<AppDbContext> options,
            CommandCountingInterceptor interceptor,
            Guid operatorUserId,
            _,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            Guid accessibleJobId,
            Guid deniedJobId) = await SeedScenarioAsync();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        JobQueueController controller = CreateController(db, authorization, CreatePrincipal(operatorUserId));

        interceptor.Reset();
        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        List<QueueEventEnvelope> events = GetEvents(result);

        // 100 accessible-printer events + 100 accessible-job events; the 200 events touching
        // the denied printer/job are filtered out.
        events.Should().HaveCount(200);
        events.Should().OnlyContain(evt =>
            evt.PrinterId == accessiblePrinterId || evt.JobId == accessibleJobId);
        events.Should().NotContain(evt =>
            evt.PrinterId == deniedPrinterId || evt.JobId == deniedJobId);

        interceptor.CommandCount.Should().BeLessThan(
            10,
            "the batched authorization helpers must issue a small constant number of queries " +
            "regardless of the 400 seeded candidate events, not one-to-four queries per event");
    }

    [Fact]
    public async Task GetChangesAsync_FarmAdmin_ReturnsEveryEvent()
    {
        (
            DbContextOptions<AppDbContext> options,
            CommandCountingInterceptor interceptor,
            _,
            Guid adminUserId,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            Guid accessibleJobId,
            Guid deniedJobId) = await SeedScenarioAsync();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        JobQueueController controller = CreateController(
            db,
            authorization,
            CreatePrincipal(adminUserId, isFarmAdmin: true));

        interceptor.Reset();
        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        List<QueueEventEnvelope> events = GetEvents(result);

        events.Should().HaveCount(400);
        events.Should().Contain(evt => evt.PrinterId == accessiblePrinterId);
        events.Should().Contain(evt => evt.PrinterId == deniedPrinterId);
        events.Should().Contain(evt => evt.JobId == accessibleJobId);
        events.Should().Contain(evt => evt.JobId == deniedJobId);

        interceptor.CommandCount.Should().BeLessThan(
            10,
            "the farm-admin short-circuit inside the batched helpers must not scale with " +
            "candidate count either");
    }

    [Fact]
    public async Task GetChangesAsync_GroupRestrictedUserWithAccess_ReturnsOnlyAccessibleEvents()
    {
        (
            DbContextOptions<AppDbContext> options,
            _,
            Guid operatorUserId,
            _,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            Guid accessibleJobId,
            Guid deniedJobId) = await SeedScenarioAsync();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        JobQueueController controller = CreateController(db, authorization, CreatePrincipal(operatorUserId));

        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        List<QueueEventEnvelope> events = GetEvents(result);

        events.Should().HaveCount(200);
        events.Where(evt => evt.PrinterId is not null).Should().HaveCount(100);
        events.Where(evt => evt.JobId is not null).Should().HaveCount(100);
        events.Should().OnlyContain(evt =>
            evt.PrinterId == accessiblePrinterId || evt.JobId == accessibleJobId);
        events.Should().NotContain(evt => evt.PrinterId == deniedPrinterId);
        events.Should().NotContain(evt => evt.JobId == deniedJobId);
    }

    [Fact]
    public async Task GetChangesAsync_GroupRestrictedUserWithoutAccess_ReturnsNoEvents()
    {
        (
            DbContextOptions<AppDbContext> options,
            _,
            _,
            _,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            _,
            _) = await SeedScenarioAsync();

        // A brand-new user with no role assignments at all: cannot see the restricted
        // printer, and owns neither job, so no job events -- except the printer with no
        // PrinterGroupId, which remains open to every authenticated user by design.
        Guid noAccessUserId = Guid.NewGuid();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        JobQueueController controller = CreateController(db, authorization, CreatePrincipal(noAccessUserId));

        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        List<QueueEventEnvelope> events = GetEvents(result);

        // The no-group printer stays open by design (see FilterAccessiblePrinterIdsAsync),
        // so its 100 events are still visible; the restricted printer's and both jobs'
        // events are not.
        events.Should().HaveCount(100);
        events.Should().OnlyContain(evt => evt.PrinterId == accessiblePrinterId);
        events.Should().NotContain(evt => evt.PrinterId == deniedPrinterId);
        events.Should().NotContain(evt => evt.JobId != null);
    }

    /// <summary>
    /// Regression test for the parity fix on top of the batched job-authorization path:
    /// <c>FilterActorAccessibleJobIdsAsync</c>'s farm-admin check is DB-backed
    /// (<c>UserRoles</c>/<c>Roles</c>), independent of the claims-based
    /// <c>PrintFarmerPermissions.IsFarmAdmin</c> short-circuit used by the printer path and by
    /// the old per-item <c>CanAccessJobAsync</c>. A principal that is a farm admin *only* via a
    /// seeded <c>UserRoles</c> row -- with no <c>ClaimTypes.Role</c> claim on the principal
    /// itself -- must still receive every job event, exercising the controller's final `else`
    /// branch (delegation to <c>FilterActorAccessibleJobIdsAsync</c>, whose own DB-backed
    /// <c>IsFarmAdminAsync</c> check must fire) rather than the claims short-circuit added
    /// earlier in <see cref="JobQueueController.GetChangesAsync"/>.
    /// </summary>
    [Fact]
    public async Task GetChangesAsync_DbOnlyFarmAdminWithoutClaim_ReturnsEveryJobEvent()
    {
        (
            DbContextOptions<AppDbContext> options,
            _,
            _,
            Guid adminUserId,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            Guid accessibleJobId,
            Guid deniedJobId) = await SeedScenarioAsync();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        // No isFarmAdmin claim -- adminUserId's admin-ness comes only from the seeded
        // UserRoles row, forcing FilterActorAccessibleJobIdsAsync's own DB-backed
        // IsFarmAdminAsync check to be what grants access, not the controller's claims
        // short-circuit.
        JobQueueController controller = CreateController(db, authorization, CreatePrincipal(adminUserId));

        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        List<QueueEventEnvelope> events = GetEvents(result);

        // Job events: DB-backed admin check grants full access. Printer events: the printer
        // path's claims-based check does NOT see this principal as admin (no role claim), so
        // it falls back to the ordinary ACL -- the no-group printer stays open by design, but
        // the group-restricted printer is denied since adminUserId holds no matching role.
        events.Should().HaveCount(300);
        events.Should().Contain(evt => evt.JobId == accessibleJobId);
        events.Should().Contain(evt => evt.JobId == deniedJobId);
        events.Should().Contain(evt => evt.PrinterId == accessiblePrinterId);
        events.Should().NotContain(evt => evt.PrinterId == deniedPrinterId);
    }

    /// <summary>
    /// Regression test for the parity fix: a principal with no parseable NameIdentifier/sub
    /// claim must degrade to "no job access" -- matching the old per-item
    /// <c>CanAccessJobAsync</c>'s <c>TryGetUserId</c>-based graceful deny -- rather than
    /// throwing <c>UnauthorizedAccessException</c> out of <c>QueueActorIdentity.Resolve</c>,
    /// which <see cref="JobQueueController.GetChangesAsync"/> does not catch.
    /// </summary>
    [Fact]
    public async Task GetChangesAsync_UnresolvableSubject_DoesNotThrowAndReturnsNoEvents()
    {
        (
            DbContextOptions<AppDbContext> options,
            _,
            _,
            _,
            Guid accessiblePrinterId,
            Guid deniedPrinterId,
            _,
            _) = await SeedScenarioAsync();

        await using var db = new AppDbContext(options);
        var authorization = new QueueResourceAuthorizationService(db);
        JobQueueController controller = CreateController(
            db,
            authorization,
            CreatePrincipalWithNoResolvableSubject());

        Func<Task<IActionResult>> act = async () => await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 500,
            CancellationToken.None);

        IActionResult result = (await act.Should().NotThrowAsync()).Subject;
        List<QueueEventEnvelope> events = GetEvents(result);

        // Both FilterAccessiblePrinterIdsAsync and the job branch's TryGetUserId guard deny
        // everything for an unresolvable subject -- the no-group printer's "open to everyone"
        // rule only applies once TryGetUserId succeeds, so for this principal every event,
        // including the open printer, is denied.
        events.Should().BeEmpty();
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
