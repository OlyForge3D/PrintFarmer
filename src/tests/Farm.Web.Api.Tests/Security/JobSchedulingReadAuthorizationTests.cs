using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class JobSchedulingReadAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SchedulingReads_FilterAndReturnNotFoundAcrossEveryResourceBoundary()
    {
        Guid actorId = Guid.NewGuid();
        SchedulingFixture fixture = await SeedFixtureAsync(actorId);
        using HttpClient client = CreateOperatorClient(actorId);

        List<ScheduledJobDto>? scheduled =
            await client.GetFromJsonAsync<List<ScheduledJobDto>>(
                "/api/job-scheduling/scheduled");

        scheduled.Should().NotBeNull();
        scheduled!.Select(item => item.JobId).Should().Equal(fixture.AllowedJobId);

        HttpResponseMessage allowedDetail = await client.GetAsync(
            $"/api/job-scheduling/{fixture.AllowedJobId}");
        allowedDetail.StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage allowedHistory = await client.GetAsync(
            $"/api/job-scheduling/{fixture.AllowedJobId}/executions");
        allowedHistory.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (Guid deniedJobId in fixture.DeniedJobIds)
        {
            HttpResponseMessage detail = await client.GetAsync(
                $"/api/job-scheduling/{deniedJobId}");
            detail.StatusCode.Should().Be(
                HttpStatusCode.NotFound,
                "cross-user scheduling details must not reveal resource existence");

            HttpResponseMessage history = await client.GetAsync(
                $"/api/job-scheduling/{deniedJobId}/executions");
            history.StatusCode.Should().Be(
                HttpStatusCode.NotFound,
                "cross-user execution history must not reveal resource existence");
        }
    }

    [Fact]
    public async Task SchedulingWire_NonUtcWallTimeAndDstValidation_MatchReactSchema()
    {
        Guid actorId = Guid.NewGuid();
        SchedulingFixture fixture = await SeedFixtureAsync(actorId);
        using HttpClient client = CreateOperatorClient(
            actorId,
            PrintFarmerPermissions.Queue.Read,
            PrintFarmerPermissions.Queue.Write);
        var request = new
        {
            scheduledLocalTime = "2026-03-07T09:30:00",
            timeZone = "America/New_York",
            recurrencePattern = "Daily",
            recurrenceInterval = 2,
            recurrenceEndLocalTime = "2026-03-20T09:30:00",
        };

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/job-scheduling/{fixture.AllowedJobId}/schedule",
            request);

        response.EnsureSuccessStatusCode();
        ScheduledJobDto? scheduled =
            await response.Content.ReadFromJsonAsync<ScheduledJobDto>();
        scheduled.Should().NotBeNull();
        scheduled!.ScheduledStartTimeUtc.Should().Be(
            new DateTime(2026, 3, 7, 14, 30, 0, DateTimeKind.Utc));
        scheduled.ScheduledLocalTime.Should().Be(
            new DateTime(2026, 3, 7, 9, 30, 0, DateTimeKind.Unspecified));
        scheduled.TimeZone.Should().Be("America/New_York");
        scheduled.RecurrencePattern.Should().Be("Daily");
        scheduled.RecurrenceInterval.Should().Be(2);

        HttpResponseMessage invalidDst = await client.PutAsJsonAsync(
            $"/api/job-scheduling/{fixture.AllowedJobId}/reschedule",
            new
            {
                scheduledLocalTime = "2026-03-08T02:30:00",
                timeZone = "America/New_York",
                recurrencePattern = "Daily",
                recurrenceInterval = 1,
            });
        invalidDst.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient CreateOperatorClient(
        Guid actorId,
        params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                permissions.Length == 0
                    ? [PrintFarmerPermissions.Queue.Read]
                    : permissions));
        return client;
    }

    private async Task<SchedulingFixture> SeedFixtureAsync(Guid actorId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Schedule HTTP maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Schedule HTTP model {Guid.NewGuid():N}",
        };
        var openPrinter = CreatePrinter(manufacturer, model, "open");
        var restrictedGroup = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"Schedule restricted {Guid.NewGuid():N}",
        };
        var restrictedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"schedule-restricted-{Guid.NewGuid():N}",
            DisplayName = "Schedule restricted",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var restrictedPrinter = CreatePrinter(
            manufacturer,
            model,
            "restricted");
        restrictedPrinter.PrinterGroupId = restrictedGroup.Id;

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.PrinterGroups.Add(restrictedGroup);
        db.Roles.Add(restrictedRole);
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = restrictedGroup.Id,
            RoleId = restrictedRole.Id,
            AccessLevel = PrinterGroupAccessLevel.View,
        });
        db.Printers.AddRange(openPrinter, restrictedPrinter);

        var foreignProject = new CalibrationProject
        {
            Id = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            Name = "Foreign calibration",
            PrinterId = openPrinter.Id,
            FilamentProvider = "test",
            FilamentProductId = "foreign-product",
            FilamentProductName = "Foreign filament",
            FilamentMaterial = "PLA",
            CreateRequestId = Guid.NewGuid().ToString(),
            CreatedBySubject = "foreign",
            UpdatedBySubject = "foreign",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.CalibrationProjects.Add(foreignProject);

        PrintJob allowed = CreateJob(actorId.ToString(), openPrinter.Id, "allowed");
        PrintJob foreignJob = CreateJob(
            Guid.NewGuid().ToString(),
            openPrinter.Id,
            "foreign-job");
        PrintJob deniedPrinter = CreateJob(
            actorId.ToString(),
            restrictedPrinter.Id,
            "denied-printer");
        PrintJob deniedProject = CreateJob(
            actorId.ToString(),
            openPrinter.Id,
            "denied-project");
        deniedProject.JobKind = JobKind.FilamentCalibration;
        deniedProject.CalibrationProjectId = foreignProject.Id;
        db.PrintJobs.AddRange(
            allowed,
            foreignJob,
            deniedPrinter,
            deniedProject);

        JobSchedule[] schedules =
        [
            CreateSchedule(allowed, actorId, now),
            CreateSchedule(foreignJob, Guid.NewGuid(), now),
            CreateSchedule(deniedPrinter, actorId, now),
            CreateSchedule(deniedProject, actorId, now),
        ];
        db.JobSchedules.AddRange(schedules);
        db.JobExecutions.AddRange(schedules.Select(schedule => new JobExecution
        {
            JobScheduleId = schedule.Id,
            ScheduledExecutionTime = schedule.ScheduledStartTime,
            Status = "Completed",
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await db.SaveChangesAsync();

        return new SchedulingFixture(
            allowed.Id,
            [foreignJob.Id, deniedPrinter.Id, deniedProject.Id]);
    }

    private static Printer CreatePrinter(
        Manufacturer manufacturer,
        PrinterModel model,
        string suffix) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = $"Schedule {suffix} {Guid.NewGuid():N}",
            ServerUrl = $"http://schedule-{suffix}-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };

    private static PrintJob CreateJob(
        string creatorSubject,
        Guid printerId,
        string suffix)
    {
        DateTime now = DateTime.UtcNow;
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Schedule {suffix}",
            AssignedPrinterId = printerId,
            CreatorSubject = creatorSubject,
            JobKind = JobKind.Standard,
            Status = PrintJobStatus.Queued,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = Math.Abs(Guid.NewGuid().GetHashCode()),
            Copies = 1,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
        };
    }

    private static JobSchedule CreateSchedule(
        PrintJob job,
        Guid actorId,
        DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ScheduledStartTime = now.AddHours(1),
            TimeZone = "UTC",
            IsActive = true,
            IsPaused = false,
            InitiatingActorSubject = actorId.ToString(),
            RequiresOperatorReauthorization = false,
            RecurrenceInterval = 1,
            ScheduledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private sealed record SchedulingFixture(
        Guid AllowedJobId,
        IReadOnlyList<Guid> DeniedJobIds);
}
