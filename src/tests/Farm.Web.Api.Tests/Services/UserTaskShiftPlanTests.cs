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

    [Fact]
    public async Task GetShiftPlanAsync_GroupsAndOrdersAnchors_NowFirst_ThenAtWindowByBoundary_ThenAnytime()
    {
        DateTime baseUtc = new(2026, 07, 12, 12, 00, 00, DateTimeKind.Utc);

        UserTask now = Task("now", UserTaskAnchorKind.Now);
        UserTask atEarly = Task("at-early", UserTaskAnchorKind.At, anchorAt: baseUtc.AddMinutes(30));
        UserTask atLate = Task("at-late", UserTaskAnchorKind.At, anchorAt: baseUtc.AddMinutes(90));
        UserTask window = Task("win", UserTaskAnchorKind.Window,
            windowStart: baseUtc.AddMinutes(45), windowEnd: baseUtc.AddMinutes(75));
        UserTask anytime = Task("anytime", UserTaskAnchorKind.AnytimeToday);
        UserTask legacy = Task("legacy", UserTaskAnchorKind.Unspecified);

        // Return in shuffled order to prove the service imposes order, not the repo.
        _repo.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { anytime, atLate, legacy, window, now, atEarly });

        ShiftPlanDto plan = await _service.GetShiftPlanAsync();

        // Groups appear in canonical order: Now, At, Window, AnytimeToday (Unspecified absorbed).
        Assert.Collection(
            plan.Groups,
            g => Assert.Equal(UserTaskAnchorKind.Now, g.AnchorKind),
            g => Assert.Equal(UserTaskAnchorKind.At, g.AnchorKind),
            g => Assert.Equal(UserTaskAnchorKind.Window, g.AnchorKind),
            g => Assert.Equal(UserTaskAnchorKind.AnytimeToday, g.AnchorKind));

        Assert.Single(plan.Groups[0].Tasks, t => t.Title == "now");

        // At bucket ordered by AnchorAtUtc ascending.
        Assert.Collection(
            plan.Groups[1].Tasks,
            t => Assert.Equal("at-early", t.Title),
            t => Assert.Equal("at-late", t.Title));

        Assert.Single(plan.Groups[2].Tasks, t => t.Title == "win");

        // Legacy Unspecified tasks land in AnytimeToday alongside AnytimeToday tasks.
        Assert.Contains(plan.Groups[3].Tasks, t => t.Title == "anytime");
        Assert.Contains(plan.Groups[3].Tasks, t => t.Title == "legacy");
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

    private static UserTask Task(
        string title,
        UserTaskAnchorKind anchor,
        DateTime? anchorAt = null,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        UserTaskPriority priority = UserTaskPriority.Normal,
        DateTime? createdAt = null) => new()
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
        };
}
