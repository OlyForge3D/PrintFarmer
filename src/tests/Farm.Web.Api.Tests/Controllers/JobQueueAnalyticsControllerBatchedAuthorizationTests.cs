using System.Data.Common;
using System.Security.Claims;
using Farm.Api.Controllers;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
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

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Tests for issue #1496: <see cref="JobQueueAnalyticsController.GetAllQueueAsync"/> used to
/// authorize each returned job one-at-a-time via <c>CanAccessJobAsync</c>. It now performs a
/// single batched <c>FilterActorAccessibleJobIdsAsync</c> call, matching the pattern already
/// used by <c>JobQueueController.GetSubscriptionResourcesAsync</c>. These tests prove:
/// (1) the controller no longer issues one authorization call per job,
/// (2) the claims-based farm-admin fast path is preserved (zero authorization queries), and
/// (3) the authorized job set returned by the batched call is IDENTICAL to what the old
///     per-job loop would have produced, for a farm admin, a partial-access operator, and a
///     no-access user.
/// </summary>
public class JobQueueAnalyticsControllerBatchedAuthorizationTests
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

    private static QueuedPrintJobWithFileMetaDto MakeJob(Guid id) => new()
    {
        Job = new QueuedPrintJobDto { Id = id.ToString() },
        GcodeFile = new QueueGcodeFileMetaDto(),
    };

    [Fact]
    public async Task GetAllQueueAsync_FarmAdmin_ReturnsAllJobsWithoutAnyAuthorizationQueries()
    {
        List<QueuedPrintJobWithFileMetaDto> jobs =
            [MakeJob(Guid.NewGuid()), MakeJob(Guid.NewGuid()), MakeJob(Guid.NewGuid())];
        _printJobManagementServiceMock
            .Setup(s => s.GetAllQueuedJobsAsync(
                null, null, null, null, null, "priority", 100, 0, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>(MockBehavior.Strict);
        JobQueueAnalyticsController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(Guid.NewGuid(), isFarmAdmin: true));

        IActionResult result = await controller.GetAllQueueAsync(
            null, null, null, null, null, null, null, "priority", 100, 0, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IEnumerable<QueuedPrintJobWithFileMetaDto>>(okResult.Value).ToList();
        returned.Should().BeEquivalentTo(jobs);

        // MockBehavior.Strict means any unexpected call (CanAccessJobAsync or
        // FilterActorAccessibleJobIdsAsync) would already have thrown above; this is an
        // explicit assertion of the acceptance criterion for readability.
        resourceAuthorization.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAllQueueAsync_NonAdmin_CallsBatchedFilterExactlyOnceInsteadOfPerJobLoop()
    {
        Guid visibleJobId = Guid.NewGuid();
        Guid hiddenJobId = Guid.NewGuid();
        List<QueuedPrintJobWithFileMetaDto> jobs = [MakeJob(visibleJobId), MakeJob(hiddenJobId)];
        _printJobManagementServiceMock
            .Setup(s => s.GetAllQueuedJobsAsync(
                null, null, null, null, null, "priority", 100, 0, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs);

        Guid userId = Guid.NewGuid();
        var resourceAuthorization = new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(r => r.FilterActorAccessibleJobIdsAsync(
                userId.ToString(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(visibleJobId) && ids.Contains(hiddenJobId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { visibleJobId });
        JobQueueAnalyticsController controller = CreateController(
            resourceAuthorization.Object,
            CreatePrincipal(userId));

        IActionResult result = await controller.GetAllQueueAsync(
            null, null, null, null, null, null, null, "priority", 100, 0, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IEnumerable<QueuedPrintJobWithFileMetaDto>>(okResult.Value).ToList();
        QueuedPrintJobWithFileMetaDto onlyEntry = Assert.Single(returned);
        Assert.Equal(visibleJobId.ToString(), onlyEntry.Job.Id);

        resourceAuthorization.Verify(
            r => r.FilterActorAccessibleJobIdsAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        resourceAuthorization.Verify(
            r => r.CanAccessJobAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Correctness gate for the refactor: for three principals (farm admin, a partial-access
    /// operator, and a no-access user) the authorized job set produced by the new batched
    /// <see cref="IQueueResourceAuthorizationService.FilterActorAccessibleJobIdsAsync"/> call
    /// must be IDENTICAL to what looping <see cref="IQueueResourceAuthorizationService.CanAccessJobAsync"/>
    /// per job (the old behavior) would have produced. The farm admin case must also resolve
    /// via the claims-based fast path without issuing a single database command.
    /// </summary>
    [Fact]
    public async Task FilterActorAccessibleJobIdsAsync_ProducesIdenticalSetToPerJobLoop_ForAllThreePrincipals()
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
        Guid otherUserId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        // Job ownership is the only ACL dimension exercised here (no printer-group or
        // calibration-project scoping), which is sufficient to prove set-equivalence between
        // the old per-job loop and the new batched filter without needing to seed printers,
        // printer groups, or roles.
        Guid ownedByOperatorJobId = Guid.NewGuid();
        Guid ownedByOtherUserJobId = Guid.NewGuid();

        await using (var seed = new AppDbContext(options))
        {
            seed.PrintJobs.AddRange(
                new PrintJob
                {
                    Id = ownedByOperatorJobId,
                    Name = "operator-owned.gcode",
                    Status = PrintJobStatus.Queued,
                    CreatorSubject = operatorUserId.ToString(),
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new PrintJob
                {
                    Id = ownedByOtherUserJobId,
                    Name = "other-user-owned.gcode",
                    Status = PrintJobStatus.Queued,
                    CreatorSubject = otherUserId.ToString(),
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await seed.SaveChangesAsync();
        }

        Guid[] candidateJobIds = [ownedByOperatorJobId, ownedByOtherUserJobId];

        // --- Farm admin: claims-based fast path, zero DB commands, sees everything ---
        //
        // Note: a claims-based farm admin is not necessarily a DB-based farm admin (that is
        // the whole point of the fast path), so calling FilterActorAccessibleJobIdsAsync
        // directly for this principal's resolved actor subject would *not* short-circuit and
        // would not be equivalent -- exactly the divergence the acceptance criteria warn
        // about. The controller never calls the batched method for a claims-based admin at
        // all, so the "new style" here mirrors the controller's actual short-circuit
        // (IsFarmAdmin(principal) checked first) rather than calling the DB-backed method
        // unconditionally.
        interceptor.Reset();
        await using (var adminContext = new AppDbContext(options))
        {
            var adminAuthorization = new QueueResourceAuthorizationService(adminContext);
            ClaimsPrincipal adminPrincipal = CreatePrincipal(Guid.NewGuid(), isFarmAdmin: true);

            HashSet<Guid> adminOldStyle = [];
            foreach (Guid jobId in candidateJobIds)
            {
                if (await adminAuthorization.CanAccessJobAsync(adminPrincipal, jobId, PrinterGroupAccessLevel.View))
                {
                    adminOldStyle.Add(jobId);
                }
            }

            interceptor.CommandCount.Should().Be(
                0,
                "the claims-based farm-admin check is a zero-query fast path and must not fall through to the DB-backed check");

            IReadOnlySet<Guid> adminNewStyle = PrintFarmerPermissions.IsFarmAdmin(adminPrincipal)
                ? candidateJobIds.ToHashSet()
                : await adminAuthorization.FilterActorAccessibleJobIdsAsync(
                    QueueActorIdentity.Resolve(adminPrincipal),
                    candidateJobIds,
                    PrinterGroupAccessLevel.View);

            interceptor.CommandCount.Should().Be(
                0,
                "the new controller code path never calls the DB-backed batched filter for a claims-based farm admin");
            adminNewStyle.Should().BeEquivalentTo(adminOldStyle);
            adminOldStyle.Should().BeEquivalentTo(candidateJobIds);
        }

        // --- Partial-access operator: owns one of the two jobs ---
        await using (var operatorContext = new AppDbContext(options))
        {
            var operatorAuthorization = new QueueResourceAuthorizationService(operatorContext);
            ClaimsPrincipal operatorPrincipal = CreatePrincipal(operatorUserId);

            HashSet<Guid> operatorOldStyle = [];
            foreach (Guid jobId in candidateJobIds)
            {
                if (await operatorAuthorization.CanAccessJobAsync(operatorPrincipal, jobId, PrinterGroupAccessLevel.View))
                {
                    operatorOldStyle.Add(jobId);
                }
            }

            IReadOnlySet<Guid> operatorNewStyle = await operatorAuthorization.FilterActorAccessibleJobIdsAsync(
                QueueActorIdentity.Resolve(operatorPrincipal),
                candidateJobIds,
                PrinterGroupAccessLevel.View);

            operatorNewStyle.Should().BeEquivalentTo(operatorOldStyle);
            operatorOldStyle.Should().BeEquivalentTo(new[] { ownedByOperatorJobId });
        }

        // --- No-access user: owns neither job ---
        await using (var noAccessContext = new AppDbContext(options))
        {
            var noAccessAuthorization = new QueueResourceAuthorizationService(noAccessContext);
            ClaimsPrincipal noAccessPrincipal = CreatePrincipal(noAccessUserId);

            HashSet<Guid> noAccessOldStyle = [];
            foreach (Guid jobId in candidateJobIds)
            {
                if (await noAccessAuthorization.CanAccessJobAsync(noAccessPrincipal, jobId, PrinterGroupAccessLevel.View))
                {
                    noAccessOldStyle.Add(jobId);
                }
            }

            IReadOnlySet<Guid> noAccessNewStyle = await noAccessAuthorization.FilterActorAccessibleJobIdsAsync(
                QueueActorIdentity.Resolve(noAccessPrincipal),
                candidateJobIds,
                PrinterGroupAccessLevel.View);

            noAccessNewStyle.Should().BeEquivalentTo(noAccessOldStyle);
            noAccessOldStyle.Should().BeEmpty();
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
