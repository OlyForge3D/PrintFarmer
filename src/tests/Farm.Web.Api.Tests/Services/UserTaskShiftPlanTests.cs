using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Focused coverage for <see cref="UserTaskService.GetShiftPlanAsync"/>:
/// deterministic anchor grouping/order, legacy-task tolerance, and stable tie-breaking.
/// </summary>
public class UserTaskShiftPlanTests
{
    private readonly Mock<IUserTaskRepository> _repo = new();
    private readonly UserTaskService _service;

    public UserTaskShiftPlanTests()
    {
        _service = new UserTaskService(
            _repo.Object,
            NullLogger<UserTaskService>.Instance,
            Mock.Of<ITaskBroadcaster>());
    }

    /// <summary>
    /// Fix 5: At and Window tasks must be interleaved into a single "Timeline" group
    /// ordered by their earliest boundary, sitting between Now and AnytimeToday.
    /// </summary>
    [Fact]
    public async Task GetShiftPlanAsync_GroupsAndOrdersAnchors_NowFirst_ThenTimelineInterleaved_ThenAnytime()
    {
        DateTime baseUtc = new(2026, 07, 12, 12, 00, 00, DateTimeKind.Utc);

        UserTask now = Task("now", UserTaskAnchorKind.Now);
        UserTask atEarly = Task("at-early", UserTaskAnchorKind.At, anchorAt: baseUtc.AddMinutes(30));
        UserTask win = Task("win", UserTaskAnchorKind.Window,
            windowStart: baseUtc.AddMinutes(45), windowEnd: baseUtc.AddMinutes(75));
        UserTask atLate = Task("at-late", UserTaskAnchorKind.At, anchorAt: baseUtc.AddMinutes(90));
        UserTask anytime = Task("anytime", UserTaskAnchorKind.AnytimeToday);
        UserTask legacy = Task("legacy", UserTaskAnchorKind.Unspecified);

        // Return in shuffled order to prove the service imposes order, not the repo.
        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { anytime, atLate, legacy, win, now, atEarly });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync();

        // Fix 5: At and Window merge into a single Timeline group (not separate groups).
        Assert.Collection(
            plan.Groups,
            g => Assert.Equal(UserTaskAnchorKind.Now, g.AnchorKind),
            g => Assert.Equal(UserTaskAnchorKind.Timeline, g.AnchorKind),
            g => Assert.Equal(UserTaskAnchorKind.AnytimeToday, g.AnchorKind));

        Assert.Single(plan.Groups[0].Tasks, t => t.Title == "now");

        // Timeline group: At-early (30min boundary) → Window (45min boundary) → At-late (90min).
        Assert.Collection(
            plan.Groups[1].Tasks,
            t => Assert.Equal("at-early", t.Title),
            t => Assert.Equal("win", t.Title),
            t => Assert.Equal("at-late", t.Title));

        // Individual tasks in Timeline retain their own AnchorKind (not "timeline").
        Assert.Equal(UserTaskAnchorKind.At, plan.Groups[1].Tasks[0].AnchorKind);
        Assert.Equal(UserTaskAnchorKind.Window, plan.Groups[1].Tasks[1].AnchorKind);
        Assert.Equal(UserTaskAnchorKind.At, plan.Groups[1].Tasks[2].AnchorKind);

        // Legacy Unspecified tasks land in AnytimeToday alongside AnytimeToday tasks.
        Assert.Contains(plan.Groups[2].Tasks, t => t.Title == "anytime");
        Assert.Contains(plan.Groups[2].Tasks, t => t.Title == "legacy");
    }

    [Fact]
    public async Task GetShiftPlanAsync_TieBreak_ByPriorityDesc_ThenCreatedAsc()
    {
        DateTime t = new(2026, 07, 12, 09, 00, 00, DateTimeKind.Utc);
        UserTask low = Task("low", UserTaskAnchorKind.Now, priority: UserTaskPriority.Low, createdAt: t.AddSeconds(-30));
        UserTask high = Task("high", UserTaskAnchorKind.Now, priority: UserTaskPriority.High, createdAt: t);
        UserTask normalOld = Task("normal-old", UserTaskAnchorKind.Now, priority: UserTaskPriority.Normal, createdAt: t.AddSeconds(-60));
        UserTask normalNew = Task("normal-new", UserTaskAnchorKind.Now, priority: UserTaskPriority.Normal, createdAt: t.AddSeconds(-10));

        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { low, high, normalNew, normalOld });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync();

        List<string> titles = [.. plan.Groups.Single().Tasks.Select(x => x.Title)];
        Assert.Equal(new[] { "high", "normal-old", "normal-new", "low" }, titles);
    }

    /// <summary>Fix 8: maintenance tasks are excluded for non-admin callers (isAdmin=false).</summary>
    [Fact]
    public async Task GetShiftPlanAsync_NonAdmin_MaintenanceTasksExcluded()
    {
        UserTask maintenance = Task("maint", UserTaskAnchorKind.Now, sourceKind: UserTaskSourceKind.Maintenance);
        UserTask normal = Task("normal", UserTaskAnchorKind.Now, sourceKind: UserTaskSourceKind.FailureIncident);

        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { maintenance, normal });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync(isAdmin: false);

        IEnumerable<UserTaskDto> allTasks = plan.Groups.SelectMany(g => g.Tasks);
        Assert.DoesNotContain(allTasks, t => t.Title == "maint");
        Assert.Contains(allTasks, t => t.Title == "normal");
    }

    /// <summary>Fix 8: maintenance tasks are visible to admin callers.</summary>
    [Fact]
    public async Task GetShiftPlanAsync_Admin_MaintenanceTasksIncluded()
    {
        UserTask maintenance = Task("maint", UserTaskAnchorKind.Now, sourceKind: UserTaskSourceKind.Maintenance);

        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { maintenance });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync(isAdmin: true);

        IEnumerable<UserTaskDto> allTasks = plan.Groups.SelectMany(g => g.Tasks);
        Assert.Contains(allTasks, t => t.Title == "maint");
    }

    /// <summary>
    /// Fix H: a Window task that also carries an (earlier) AnchorAtUtc must sort by its
    /// WindowStartUtc boundary, not the point anchor — otherwise it jumps ahead of
    /// earlier-window tasks in the Timeline.
    /// </summary>
    [Fact]
    public async Task GetShiftPlanAsync_WindowTaskWithAnchorAt_SortsByWindowStart()
    {
        DateTime baseUtc = new(2026, 07, 12, 12, 00, 00, DateTimeKind.Utc);

        // Window task's point anchor (10min) is earlier than its window-start (60min).
        UserTask win = Task("win", UserTaskAnchorKind.Window,
            anchorAt: baseUtc.AddMinutes(10),
            windowStart: baseUtc.AddMinutes(60),
            windowEnd: baseUtc.AddMinutes(90));
        UserTask at = Task("at", UserTaskAnchorKind.At, anchorAt: baseUtc.AddMinutes(40));

        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { win, at });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync();

        // Correct ordering keys the Window task by its 60min window-start, so the 40min
        // At task precedes it. The pre-fix bug keyed Window by its 10min anchor.
        Assert.Collection(
            plan.Groups.Single().Tasks,
            t => Assert.Equal("at", t.Title),
            t => Assert.Equal("win", t.Title));
    }

    private static UserTask Task(
        string title,
        UserTaskAnchorKind anchor,
        DateTime? anchorAt = null,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        UserTaskPriority priority = UserTaskPriority.Normal,
        DateTime? createdAt = null,
        UserTaskSourceKind sourceKind = UserTaskSourceKind.Unspecified) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            TaskType = UserTaskType.Custom,
            Status = UserTaskStatus.Pending,
            Priority = priority,
            AnchorKind = anchor,
            AnchorAtUtc = anchorAt,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = createdAt ?? DateTime.UtcNow,
            SourceKind = sourceKind,
        };
}
