using Farm.Infrastructure.Domain;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Domain;

public class MaintenanceModelsTests
{
    #region PrinterStatistics Tests

    [Fact]
    public void PrinterStatistics_Constructor_SetsDefaultValues()
    {
        var stats = new PrinterStatistics();

        stats.Id.Should().Be(Guid.Empty);
        stats.TotalPrintHours.Should().Be(0);
        stats.TotalJobsCompleted.Should().Be(0);
        stats.TotalJobsFailed.Should().Be(0);
        stats.TotalFilamentUsedGrams.Should().Be(0);
        stats.TotalFilamentUsedMeters.Should().Be(0);
        stats.LastSyncTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        stats.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        stats.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PrinterStatistics_CanSetProperties()
    {
        var printerId = Guid.NewGuid();
        var stats = new PrinterStatistics
        {
            PrinterId = printerId,
            TotalPrintHours = 100.5,
            TotalJobsCompleted = 50,
            TotalJobsFailed = 5,
            TotalFilamentUsedGrams = 5000.0,
            TotalFilamentUsedMeters = 1500.0
        };

        stats.PrinterId.Should().Be(printerId);
        stats.TotalPrintHours.Should().Be(100.5);
        stats.TotalJobsCompleted.Should().Be(50);
        stats.TotalJobsFailed.Should().Be(5);
        stats.TotalFilamentUsedGrams.Should().Be(5000.0);
        stats.TotalFilamentUsedMeters.Should().Be(1500.0);
    }

    #endregion

    #region MaintenanceSchedule Tests

    [Fact]
    public void MaintenanceSchedule_Constructor_SetsDefaultValues()
    {
        var schedule = new MaintenanceSchedule();

        schedule.Id.Should().Be(Guid.Empty);
        schedule.TaskName.Should().BeEmpty();
        schedule.Priority.Should().Be(2);
        schedule.IsActive.Should().BeTrue();
        schedule.IsDefault.Should().BeFalse();
        schedule.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        schedule.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MaintenanceSchedule_CanSetHourBasedInterval()
    {
        var schedule = new MaintenanceSchedule
        {
            TaskName = "Hotend Replacement",
            Component = "Hotend",
            IntervalHours = 500.0,
            Priority = 3
        };

        schedule.TaskName.Should().Be("Hotend Replacement");
        schedule.Component.Should().Be("Hotend");
        schedule.IntervalHours.Should().Be(500.0);
        schedule.IntervalDays.Should().BeNull();
        schedule.Priority.Should().Be(3);
    }

    [Fact]
    public void MaintenanceSchedule_CanSetDayBasedInterval()
    {
        var schedule = new MaintenanceSchedule
        {
            TaskName = "General Inspection",
            Component = "Overall",
            IntervalDays = 90,
            Priority = 2
        };

        schedule.TaskName.Should().Be("General Inspection");
        schedule.Component.Should().Be("Overall");
        schedule.IntervalDays.Should().Be(90);
        schedule.IntervalHours.Should().BeNull();
        schedule.Priority.Should().Be(2);
    }

    [Fact]
    public void MaintenanceSchedule_CanBeModelWideDefault()
    {
        var modelId = Guid.NewGuid();
        var schedule = new MaintenanceSchedule
        {
            PrinterModelId = modelId,
            TaskName = "Belt Tension Check",
            IntervalHours = 250.0,
            IsDefault = true,
            IsActive = true
        };

        schedule.PrinterModelId.Should().Be(modelId);
        schedule.PrinterId.Should().BeNull();
        schedule.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void MaintenanceSchedule_CanBePrinterSpecific()
    {
        var printerId = Guid.NewGuid();
        var schedule = new MaintenanceSchedule
        {
            PrinterId = printerId,
            TaskName = "Custom Maintenance",
            IntervalHours = 100.0,
            IsDefault = false
        };

        schedule.PrinterId.Should().Be(printerId);
        schedule.PrinterModelId.Should().BeNull();
        schedule.IsDefault.Should().BeFalse();
    }

    #endregion

    #region MaintenanceLog Tests

    [Fact]
    public void MaintenanceLog_Constructor_SetsDefaultValues()
    {
        var log = new MaintenanceLog();

        log.Id.Should().Be(Guid.Empty);
        log.TaskName.Should().BeEmpty();
        log.PerformedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        log.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MaintenanceLog_CanSetAllProperties()
    {
        var printerId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var alertId = Guid.NewGuid();
        var performedAt = DateTime.UtcNow.AddHours(-2);

        var log = new MaintenanceLog
        {
            PrinterId = printerId,
            MaintenanceScheduleId = scheduleId,
            ResolvedAlertId = alertId,
            TaskName = "Hotend Replacement",
            Notes = "Replaced worn hotend",
            Component = "Hotend",
            PerformedBy = "admin",
            PerformedAt = performedAt,
            DurationMinutes = 45,
            PartsReplaced = "E3D V6 Hotend",
            Cost = 49.99m,
            PrinterHoursAtMaintenance = 500.5
        };

        log.PrinterId.Should().Be(printerId);
        log.MaintenanceScheduleId.Should().Be(scheduleId);
        log.ResolvedAlertId.Should().Be(alertId);
        log.TaskName.Should().Be("Hotend Replacement");
        log.Notes.Should().Be("Replaced worn hotend");
        log.Component.Should().Be("Hotend");
        log.PerformedBy.Should().Be("admin");
        log.PerformedAt.Should().Be(performedAt);
        log.DurationMinutes.Should().Be(45);
        log.PartsReplaced.Should().Be("E3D V6 Hotend");
        log.Cost.Should().Be(49.99m);
        log.PrinterHoursAtMaintenance.Should().Be(500.5);
    }

    #endregion

    #region MaintenanceAlert Tests

    [Fact]
    public void MaintenanceAlert_Constructor_SetsDefaultValues()
    {
        var alert = new MaintenanceAlert();

        alert.Id.Should().Be(Guid.Empty);
        alert.Title.Should().BeEmpty();
        alert.Message.Should().BeEmpty();
        alert.Severity.Should().Be(2);
        alert.Status.Should().Be(MaintenanceAlertStatus.Active);
        alert.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        alert.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MaintenanceAlert_CanSetAllProperties()
    {
        var printerId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();

        var alert = new MaintenanceAlert
        {
            PrinterId = printerId,
            MaintenanceScheduleId = scheduleId,
            Title = "Hotend Maintenance Due",
            Message = "Printer has exceeded 500 hours",
            Severity = 3,
            Status = MaintenanceAlertStatus.Active,
            PrinterHoursAtTrigger = 505.5,
            HoursSinceLastMaintenance = 505.5
        };

        alert.PrinterId.Should().Be(printerId);
        alert.MaintenanceScheduleId.Should().Be(scheduleId);
        alert.Title.Should().Be("Hotend Maintenance Due");
        alert.Message.Should().Be("Printer has exceeded 500 hours");
        alert.Severity.Should().Be(3);
        alert.Status.Should().Be(MaintenanceAlertStatus.Active);
        alert.PrinterHoursAtTrigger.Should().Be(505.5);
        alert.HoursSinceLastMaintenance.Should().Be(505.5);
    }

    [Fact]
    public void MaintenanceAlert_CanBeAcknowledged()
    {
        var alert = new MaintenanceAlert
        {
            Status = MaintenanceAlertStatus.Acknowledged,
            AcknowledgedAt = DateTime.UtcNow,
            AcknowledgedBy = "admin"
        };

        alert.Status.Should().Be(MaintenanceAlertStatus.Acknowledged);
        alert.AcknowledgedAt.Should().NotBeNull();
        alert.AcknowledgedBy.Should().Be("admin");
    }

    [Fact]
    public void MaintenanceAlert_CanBeResolved()
    {
        var alert = new MaintenanceAlert
        {
            Status = MaintenanceAlertStatus.Resolved,
            ResolvedAt = DateTime.UtcNow,
            ResolvedBy = "technician"
        };

        alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);
        alert.ResolvedAt.Should().NotBeNull();
        alert.ResolvedBy.Should().Be("technician");
    }

    [Fact]
    public void MaintenanceAlert_CanBeDismissed()
    {
        var alert = new MaintenanceAlert
        {
            Status = MaintenanceAlertStatus.Dismissed,
            DismissedAt = DateTime.UtcNow,
            DismissedBy = "admin",
            DismissalReason = "False positive"
        };

        alert.Status.Should().Be(MaintenanceAlertStatus.Dismissed);
        alert.DismissedAt.Should().NotBeNull();
        alert.DismissedBy.Should().Be("admin");
        alert.DismissalReason.Should().Be("False positive");
    }

    [Fact]
    public void MaintenanceAlertStatus_HasCorrectValues()
    {
        var active = MaintenanceAlertStatus.Active;
        var acknowledged = MaintenanceAlertStatus.Acknowledged;
        var resolved = MaintenanceAlertStatus.Resolved;
        var dismissed = MaintenanceAlertStatus.Dismissed;

        ((int)active).Should().Be(0);
        ((int)acknowledged).Should().Be(1);
        ((int)resolved).Should().Be(2);
        ((int)dismissed).Should().Be(3);
    }

    #endregion
}
