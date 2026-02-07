using System;

namespace Farm.Web.Api.Controllers.Responses;

public record UpcomingMaintenanceTaskDto(
    string Id,
    Guid ScheduleId,
    Guid PrinterId,
    string PrinterName,
    string TaskName,
    string? Component,
    string? Description,
    int Priority,
    string IntervalType,
    double IntervalValue,
    DateTime? DueDate,
    int? DaysUntilDue,
    double? HoursUntilDue,
    bool IsOverdue,
    bool IsDueToday,
    DateTime? LastPerformedAt);
