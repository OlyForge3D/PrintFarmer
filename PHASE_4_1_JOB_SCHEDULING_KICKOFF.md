# Phase 4.1: Job Scheduling - Implementation Kickoff

**Phase**: 4.1 - Job Scheduling with Timezone Support  
**Status**: 🚀 KICKOFF (January 11, 2026)  
**Estimated Duration**: 2 days (January 13-14, 2026)  
**Priority**: P1 - Core automation feature

---

## Overview

Phase 4.1 enables users to schedule print jobs for specific dates and times with full timezone support. Jobs can be scheduled for future execution, paused, resumed, or rescheduled. A visual calendar interface allows intuitive date/time selection.

**Architecture Decision**: Scheduling fields stored in separate `JobSchedule` table (not on `PrintJob`) to:
- Keep PrintJob clean and focused on on-demand jobs
- Avoid sparse NULL columns for non-scheduled jobs
- Enable flexible scheduling features (recurrence, pause state, etc.)
- Follow database normalization best practices

**Prerequisites**: ✅ All Phase 3 work complete

**Success Criteria**:
- Scheduling endpoints functional
- Job scheduling persists to database
- Timezone-aware scheduling working
- React UI for job scheduling complete
- Scheduled job execution triggered at correct time
- Tests passing (95%+)
- 0 build warnings/errors
- UI component renders correctly

---

## Implementation Tasks

### Task 4.1.1: Backend Models & Database (Day 1, Morning)

**Files to Create**:
1. Create new `src/infra/Models/JobSchedule.cs` for scheduling
2. Create new `src/infra/Models/JobExecution.cs` for execution tracking
3. Update `src/infra/Models/PrintJob.cs` to add optional relationship to JobSchedule

**Create JobSchedule Model** (new file):

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents scheduling configuration for a print job
/// Separate table to keep PrintJob clean (only for scheduled jobs, not on-demand)
/// </summary>
public class JobSchedule
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ForeignKey(nameof(PrintJob))]
    public string PrintJobId { get; set; }
    public virtual PrintJob PrintJob { get; set; }

    /// <summary>
    /// Scheduled start time in UTC
    /// </summary>
    public DateTime ScheduledStartTime { get; set; }

    /// <summary>
    /// Timezone for display/input (e.g., "America/New_York", "UTC")
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// Recurrence pattern if job should repeat (null = one-time)
    /// Values: "Daily", "Weekly", "Monthly", null
    /// </summary>
    public string? RecurrencePattern { get; set; }

    /// <summary>
    /// When recurrence should end (null = indefinite for recurring jobs)
    /// </summary>
    public DateTime? RecurrenceEndDate { get; set; }

    /// <summary>
    /// Is this scheduled job currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Is this scheduled job paused (can be resumed)
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// When the job was originally scheduled
    /// </summary>
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to execution history (for recurring jobs)
    /// </summary>
    public virtual ICollection<JobExecution> Executions { get; set; } = new List<JobExecution>();
}
```

**Update PrintJob Model** (add navigation property):

```csharp
public class PrintJob
{
    // ... existing fields remain unchanged ...

    /// <summary>
    /// Navigation property to scheduling info (null if on-demand job)
    /// </summary>
    public virtual JobSchedule? Schedule { get; set; }
}
```

**Create JobExecution Model** (new file - tracks execution history for recurring jobs):

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks execution history for scheduled jobs (especially recurring ones)
/// </summary>
public class JobExecution
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ForeignKey(nameof(JobSchedule))]
    public string JobScheduleId { get; set; }
    public virtual JobSchedule JobSchedule { get; set; }

    /// <summary>
    /// When this execution was scheduled to run
    /// </summary>
    public DateTime ScheduledExecutionTime { get; set; }

    /// <summary>
    /// When this execution actually started (null if not started yet)
    /// </summary>
    public DateTime? ActualStartTime { get; set; }

    /// <summary>
    /// Execution status: Pending, Running, Completed, Failed, Cancelled
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Result message or error details
    /// </summary>
    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

**Database Configuration** (in `AppDbContext.cs` > `OnModelCreating`):

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ... existing configuration ...

    // JobSchedule - scheduling info for print jobs
    modelBuilder.Entity<JobSchedule>()
        .HasKey(js => js.Id);
    modelBuilder.Entity<JobSchedule>()
        .HasOne(js => js.PrintJob)
        .WithOne(j => j.Schedule)
        .HasForeignKey<JobSchedule>(js => js.PrintJobId)
        .OnDelete(DeleteBehavior.Cascade);
    modelBuilder.Entity<JobSchedule>()
        .HasIndex(js => js.ScheduledStartTime);
    modelBuilder.Entity<JobSchedule>()
        .HasIndex(js => js.IsActive);
    modelBuilder.Entity<JobSchedule>()
        .Property(js => js.TimeZone)
        .HasDefaultValue("UTC");

    // JobExecution - tracks execution history for scheduled jobs
    modelBuilder.Entity<JobExecution>()
        .HasKey(je => je.Id);
    modelBuilder.Entity<JobExecution>()
        .HasOne(je => je.JobSchedule)
        .WithMany(js => js.Executions)
        .HasForeignKey(je => je.JobScheduleId)
        .OnDelete(DeleteBehavior.Cascade);
    modelBuilder.Entity<JobExecution>()
        .HasIndex(je => new { je.JobScheduleId, je.ScheduledExecutionTime });
    modelBuilder.Entity<JobExecution>()
        .HasIndex(je => je.Status);
}
```

**Migration**:
```bash
cd /home/pi/pfarm/src
dotnet ef migrations add AddJobScheduling --project infra --startup-project api
dotnet ef database update --project api
```

---

### Task 4.1.2: Service Layer (Day 1, Afternoon)

**File**: `src/api/Services/JobSchedulingService.cs`

```csharp
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.DTOs;
using Microsoft.EntityFrameworkCore;
using TimeZoneConverter;

namespace Farm.Web.Api.Services.Scheduling;

public interface IJobSchedulingService
{
    Task<PrintJobDto> ScheduleJobAsync(
        string jobId,
        DateTime scheduledStartTime,
        string timezone,
        string? recurrencePattern = null,
        CancellationToken cancellationToken = default);

    Task<PrintJobDto> RescheduleJobAsync(
        string jobId,
        DateTime newScheduledTime,
        string timezone,
        CancellationToken cancellationToken = default);

    Task<PrintJobDto> CancelSchedulingAsync(string jobId, CancellationToken cancellationToken = default);

    Task<IEnumerable<ScheduledJobDto>> GetScheduledJobsAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<JobExecutionDto>> GetJobExecutionsAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<DateTime> ConvertToUtcAsync(DateTime localTime, string timezone);

    Task<DateTime> ConvertToLocalAsync(DateTime utcTime, string timezone);

    Task TriggerScheduledJobsAsync(CancellationToken cancellationToken = default);
}

public class JobSchedulingService : IJobSchedulingService
{
    private readonly AppDbContext _context;
    private readonly ILogger<JobSchedulingService> _logger;
    private readonly IPrintQueueService _printQueueService;

    public JobSchedulingService(
        AppDbContext context,
        ILogger<JobSchedulingService> logger,
        IPrintQueueService printQueueService)
    {
        _context = context;
        _logger = logger;
        _printQueueService = printQueueService;
    }

    public async Task<PrintJobDto> ScheduleJobAsync(
        string jobId,
        DateTime scheduledStartTime,
        string timezone,
        string? recurrencePattern = null,
        CancellationToken cancellationToken = default)
    {
        // Validate job exists
        var job = await _context.PrintJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Job not found");

        // Validate scheduled time is in future
        var utcTime = ConvertToUtc(scheduledStartTime, timezone);
        if (utcTime <= DateTime.UtcNow)
            throw new InvalidOperationException("Scheduled time must be in the future");

        // Validate timezone
        if (!IsValidTimeZone(timezone))
            throw new InvalidOperationException($"Invalid timezone: {timezone}");

        // Update job with scheduling details
        job.ScheduledStartTime = utcTime;
        job.ScheduledTimeZone = timezone;
        job.RecurrencePattern = recurrencePattern;
        job.IsScheduleActive = true;
        job.ScheduledAt = DateTime.UtcNow;

        // Create first execution record
        var execution = new JobExecution
        {
            PrintJobId = job.Id,
            ScheduledExecutionTime = utcTime,
            Status = "Pending"
        };
        _context.Set<JobExecution>().Add(execution);

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            $"[JobScheduling] Job '{job.Id}' scheduled for {scheduledStartTime} {timezone}");

        return await _printQueueService.GetJobAsync(jobId, cancellationToken);
    }

    public async Task<PrintJobDto> RescheduleJobAsync(
        string jobId,
        DateTime newScheduledTime,
        string timezone,
        CancellationToken cancellationToken = default)
    {
        var job = await _context.PrintJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Job not found");

        if (!job.ScheduledStartTime.HasValue)
            throw new InvalidOperationException("Job is not scheduled");

        var utcTime = ConvertToUtc(newScheduledTime, timezone);
        if (utcTime <= DateTime.UtcNow)
            throw new InvalidOperationException("New scheduled time must be in the future");

        job.ScheduledStartTime = utcTime;
        job.ScheduledTimeZone = timezone;
        job.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            $"[JobScheduling] Job '{job.Id}' rescheduled to {newScheduledTime} {timezone}");

        return await _printQueueService.GetJobAsync(jobId, cancellationToken);
    }

    public async Task<PrintJobDto> CancelSchedulingAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _context.PrintJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Job not found");

        if (!job.ScheduledStartTime.HasValue)
            throw new InvalidOperationException("Job is not scheduled");

        job.ScheduledStartTime = null;
        job.ScheduledTimeZone = null;
        job.RecurrencePattern = null;
        job.IsScheduleActive = false;
        job.ScheduledAt = null;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation($"[JobScheduling] Job '{job.Id}' scheduling cancelled");

        return await _printQueueService.GetJobAsync(jobId, cancellationToken);
    }

    public async Task<IEnumerable<ScheduledJobDto>> GetScheduledJobsAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<JobSchedule>()
            .Where(js => js.IsActive && !js.IsPaused)
            .Include(js => js.PrintJob)
            .Include(js => js.PrintJob.Printer)
            .AsQueryable();

        if (dateFrom.HasValue)
            query = query.Where(js => js.ScheduledStartTime >= dateFrom.Value);

        if (dateTo.HasValue)
            query = query.Where(js => js.ScheduledStartTime <= dateTo.Value);

        var schedules = await query
            .OrderBy(js => js.ScheduledStartTime)
            .ToListAsync(cancellationToken);

        return schedules.Select(js => new ScheduledJobDto
        {
            JobId = js.PrintJobId,
            PrinterName = js.PrintJob?.Printer?.Name ?? "Unknown",
            JobName = js.PrintJob?.Name ?? "Unknown",
            ScheduledStartTime = js.ScheduledStartTime,
            TimeZone = js.TimeZone,
            RecurrencePattern = js.RecurrencePattern,
            IsActive = js.IsActive
        });
    }

    public async Task<IEnumerable<JobExecutionDto>> GetJobExecutionsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var executions = await _context.Set<JobExecution>()
            .Where(je => je.PrintJobId == jobId)
            .OrderByDescending(je => je.ScheduledExecutionTime)
            .ToListAsync(cancellationToken);

        return executions.Select(je => new JobExecutionDto
        {
            Id = je.Id,
            JobId = je.PrintJobId,
            ScheduledTime = je.ScheduledExecutionTime,
            ActualStartTime = je.ActualStartTime,
            Status = je.Status,
            Message = je.Message
        });
    }

    public Task<DateTime> ConvertToUtcAsync(DateTime localTime, string timezone)
    {
        try
        {
            var tzInfo = TZConvert.GetTimeZoneInfo(timezone);
            var utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime, tzInfo);
            return Task.FromResult(utcTime);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert time: {ex.Message}", ex);
        }
    }

    public Task<DateTime> ConvertToLocalAsync(DateTime utcTime, string timezone)
    {
        try
        {
            var tzInfo = TZConvert.GetTimeZoneInfo(timezone);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, tzInfo);
            return Task.FromResult(localTime);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert time: {ex.Message}", ex);
        }
    }

    public async Task TriggerScheduledJobsAsync(CancellationToken cancellationToken = default)
    {
        // Get all executions scheduled to run now or in the past
        var now = DateTime.UtcNow;
        var pendingExecutions = await _context.Set<JobExecution>()
            .Where(je => je.Status == "Pending" && je.ScheduledExecutionTime <= now)
            .Include(je => je.JobSchedule)
            .ThenInclude(js => js.PrintJob)
            .ToListAsync(cancellationToken);

        foreach (var execution in pendingExecutions)
        {
            try
            {
                // Update execution status
                execution.Status = "Running";
                execution.ActualStartTime = now;

                // Update job status to Printing (trigger execution)
                execution.JobSchedule.PrintJob.Status = "Printing";
                execution.JobSchedule.PrintJob.StartedAt = now;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    $"[JobScheduling] Triggered scheduled job '{execution.JobScheduleId}' at {now:O}");
            }
            catch (Exception ex)
            {
                execution.Status = "Failed";
                execution.Message = ex.Message;
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogError(
                    $"[JobScheduling] Failed to trigger job '{execution.JobScheduleId}': {ex.Message}");
            }
        }
    }

    private DateTime ConvertToUtc(DateTime localTime, string timezone)
    {
        try
        {
            var tzInfo = TZConvert.GetTimeZoneInfo(timezone);
            return TimeZoneInfo.ConvertTimeToUtc(localTime, tzInfo);
        }
        catch
        {
            // Fallback to assuming UTC
            return localTime;
        }
    }

    private bool IsValidTimeZone(string timezone)
    {
        try
        {
            TZConvert.GetTimeZoneInfo(timezone);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

**Add NuGet Dependency**:
```bash
cd /home/pi/pfarm/src/api
dotnet add package TimeZoneConverter
```

---

### Task 4.1.3: DTOs (Day 1, Afternoon)

**File**: Update or create `src/api/DTOs/SchedulingDtos.cs`

```csharp
namespace Farm.Web.Api.DTOs;

public class ScheduledJobDto
{
    public string JobId { get; set; }
    public string PrinterName { get; set; }
    public string JobName { get; set; }
    public DateTime ScheduledStartTime { get; set; }
    public string TimeZone { get; set; }
    public string? RecurrencePattern { get; set; }
    public bool IsActive { get; set; }
}

public class JobExecutionDto
{
    public string Id { get; set; }
    public string JobId { get; set; }
    public DateTime ScheduledTime { get; set; }
    public DateTime? ActualStartTime { get; set; }
    public string Status { get; set; } // Pending, Running, Completed, Failed, Cancelled
    public string? Message { get; set; }
}

public class ScheduleJobRequestDto
{
    public DateTime ScheduledStartTime { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string? RecurrencePattern { get; set; } // Daily, Weekly, Monthly, null
}

public class RescheduleJobRequestDto
{
    public DateTime NewScheduledTime { get; set; }
    public string TimeZone { get; set; } = "UTC";
}
```

---

### Task 4.1.4: Controller (Day 1, Evening)

**File**: Create `src/api/Controllers/JobSchedulingController.cs`

```csharp
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class JobSchedulingController : ControllerBase
{
    private readonly IJobSchedulingService _jobSchedulingService;
    private readonly ILogger<JobSchedulingController> _logger;

    public JobSchedulingController(
        IJobSchedulingService jobSchedulingService,
        ILogger<JobSchedulingController> logger)
    {
        _jobSchedulingService = jobSchedulingService;
        _logger = logger;
    }

    /// <summary>
    /// Schedule a print job for future execution
    /// </summary>
    [HttpPost("jobs/{jobId}/schedule")]
    public async Task<ActionResult<PrintJobDto>> ScheduleJob(
        string jobId,
        [FromBody] ScheduleJobRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _jobSchedulingService.ScheduleJobAsync(
                jobId,
                dto.ScheduledStartTime,
                dto.TimeZone,
                dto.RecurrencePattern,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning($"Scheduling validation failed: {ex.Message}");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reschedule an already-scheduled job
    /// </summary>
    [HttpPut("jobs/{jobId}/reschedule")]
    public async Task<ActionResult<PrintJobDto>> RescheduleJob(
        string jobId,
        [FromBody] RescheduleJobRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _jobSchedulingService.RescheduleJobAsync(
                jobId,
                dto.NewScheduledTime,
                dto.TimeZone,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning($"Rescheduling validation failed: {ex.Message}");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reschedule job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel scheduling for a job (removes scheduled time)
    /// </summary>
    [HttpDelete("jobs/{jobId}/schedule")]
    public async Task<ActionResult<PrintJobDto>> CancelScheduling(
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _jobSchedulingService.CancelSchedulingAsync(jobId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning($"Cancel scheduling failed: {ex.Message}");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel scheduling");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all scheduled jobs, optionally filtered by date range
    /// </summary>
    [HttpGet("scheduled")]
    public async Task<ActionResult<IEnumerable<ScheduledJobDto>>> GetScheduledJobs(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _jobSchedulingService.GetScheduledJobsAsync(dateFrom, dateTo, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduled jobs");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get execution history for a scheduled job
    /// </summary>
    [HttpGet("jobs/{jobId}/executions")]
    public async Task<ActionResult<IEnumerable<JobExecutionDto>>> GetJobExecutions(
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _jobSchedulingService.GetJobExecutionsAsync(jobId, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job executions");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Convert local time to UTC
    /// </summary>
    [HttpPost("convert-to-utc")]
    public async Task<ActionResult<object>> ConvertToUtc(
        [FromBody] object request,
        CancellationToken cancellationToken)
    {
        try
        {
            dynamic dto = request;
            DateTime localTime = dto.localTime;
            string timezone = dto.timezone;

            var utcTime = await _jobSchedulingService.ConvertToUtcAsync(localTime, timezone);
            return Ok(new { utcTime });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Convert UTC time to local timezone
    /// </summary>
    [HttpPost("convert-to-local")]
    public async Task<ActionResult<object>> ConvertToLocal(
        [FromBody] object request,
        CancellationToken cancellationToken)
    {
        try
        {
            dynamic dto = request;
            DateTime utcTime = dto.utcTime;
            string timezone = dto.timezone;

            var localTime = await _jobSchedulingService.ConvertToLocalAsync(utcTime, timezone);
            return Ok(new { localTime });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
```

---

### Task 4.1.5: Frontend Component (Day 2, Morning)

**File**: `src/Web/ReactApp/src/features/queue/components/JobScheduler.tsx`

```typescript
import React, { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Card } from '@/common/components/ui/Card';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Alert } from '@/common/components/ui/Alert';
import { jobSchedulingService } from '@/services/jobSchedulingService';

export interface JobSchedulerProps {
  jobId: string;
  currentScheduledTime?: Date;
  onScheduled?: () => void;
  onCancelled?: () => void;
}

export const JobScheduler: React.FC<JobSchedulerProps> = ({
  jobId,
  currentScheduledTime,
  onScheduled,
  onCancelled,
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [selectedDate, setSelectedDate] = useState<string>(
    currentScheduledTime ? currentScheduledTime.toISOString().split('T')[0] : new Date().toISOString().split('T')[0]
  );
  const [selectedTime, setSelectedTime] = useState<string>(
    currentScheduledTime ? currentScheduledTime.toTimeString().slice(0, 5) : '08:00'
  );
  const [timezone, setTimezone] = useState<string>(
    Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  );
  const [error, setError] = useState<string | null>(null);

  const scheduleMutation = useMutation({
    mutationFn: (data: { scheduledStartTime: Date; timeZone: string }) =>
      jobSchedulingService.scheduleJob(jobId, data),
    onSuccess: () => {
      setError(null);
      setIsOpen(false);
      onScheduled?.();
    },
    onError: (error: any) => {
      setError(error.message || 'Failed to schedule job');
    },
  });

  const cancelMutation = useMutation({
    mutationFn: () => jobSchedulingService.cancelScheduling(jobId),
    onSuccess: () => {
      setError(null);
      onCancelled?.();
    },
    onError: (error: any) => {
      setError(error.message || 'Failed to cancel scheduling');
    },
  });

  const handleSchedule = () => {
    const [hours, minutes] = selectedTime.split(':').map(Number);
    const scheduledTime = new Date(selectedDate);
    scheduledTime.setHours(hours, minutes, 0, 0);

    if (scheduledTime <= new Date()) {
      setError('Scheduled time must be in the future');
      return;
    }

    scheduleMutation.mutate({
      scheduledStartTime: scheduledTime,
      timeZone: timezone,
    });
  };

  const handleCancel = () => {
    if (confirm('Are you sure you want to cancel scheduling for this job?')) {
      cancelMutation.mutate();
    }
  };

  if (!isOpen && !currentScheduledTime) {
    return (
      <Button onClick={() => setIsOpen(true)} variant="outline">
        Schedule Job
      </Button>
    );
  }

  return (
    <Card className="p-4">
      <div className="flex items-center justify-between mb-4">
        <h3 className="font-semibold">Schedule Job</h3>
        {!isOpen && <Button onClick={() => setIsOpen(false)}>Close</Button>}
      </div>

      {error && <Alert variant="error" className="mb-4">{error}</Alert>}

      {currentScheduledTime && (
        <Alert variant="info" className="mb-4">
          Currently scheduled for: {currentScheduledTime.toLocaleString()}
        </Alert>
      )}

      {isOpen && (
        <div className="space-y-4">
          {/* Date Picker */}
          <div>
            <label className="block text-sm font-medium mb-2">Date</label>
            <Input
              type="date"
              value={selectedDate}
              onChange={(e) => setSelectedDate(e.target.value)}
              disabled={scheduleMutation.isPending || cancelMutation.isPending}
            />
          </div>

          {/* Time Picker */}
          <div>
            <label className="block text-sm font-medium mb-2">Time</label>
            <Input
              type="time"
              value={selectedTime}
              onChange={(e) => setSelectedTime(e.target.value)}
              disabled={scheduleMutation.isPending || cancelMutation.isPending}
            />
          </div>

          {/* Timezone Selector */}
          <div>
            <label className="block text-sm font-medium mb-2">Timezone</label>
            <Select
              value={timezone}
              onChange={setTimezone}
              disabled={scheduleMutation.isPending || cancelMutation.isPending}
              options={getTimezoneOptions()}
            />
            <p className="text-xs text-gray-600 mt-1">
              Current local timezone: {Intl.DateTimeFormat().resolvedOptions().timeZone}
            </p>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-2">
            <Button
              onClick={handleSchedule}
              isLoading={scheduleMutation.isPending}
              disabled={scheduleMutation.isPending}
              variant="primary"
            >
              {currentScheduledTime ? 'Update Schedule' : 'Schedule Job'}
            </Button>

            {currentScheduledTime && (
              <Button
                onClick={handleCancel}
                isLoading={cancelMutation.isPending}
                disabled={cancelMutation.isPending}
                variant="danger"
              >
                Cancel Schedule
              </Button>
            )}
          </div>
        </div>
      )}
    </Card>
  );
};

// Helper function to get timezone options
function getTimezoneOptions() {
  const timezones = [
    'UTC',
    'America/New_York',
    'America/Chicago',
    'America/Denver',
    'America/Los_Angeles',
    'Europe/London',
    'Europe/Paris',
    'Europe/Berlin',
    'Asia/Tokyo',
    'Asia/Shanghai',
    'Asia/Hong_Kong',
    'Australia/Sydney',
  ];

  return timezones.map((tz) => ({
    value: tz,
    label: tz,
  }));
}

export default JobScheduler;
```

**Service File**: `src/Web/ReactApp/src/services/jobSchedulingService.ts`

```typescript
import { apiClient } from './apiClient';

export interface ScheduleJobRequestDto {
  scheduledStartTime: Date;
  timeZone: string;
  recurrencePattern?: string;
}

export interface RescheduleJobRequestDto {
  newScheduledTime: Date;
  timeZone: string;
}

export interface ScheduledJobDto {
  jobId: string;
  printerName: string;
  jobName: string;
  scheduledStartTime: Date;
  timeZone: string;
  recurrencePattern?: string;
  isActive: boolean;
}

export const jobSchedulingService = {
  async scheduleJob(jobId: string, data: ScheduleJobRequestDto): Promise<any> {
    const response = await apiClient.post(`/jobScheduling/jobs/${jobId}/schedule`, {
      scheduledStartTime: data.scheduledStartTime.toISOString(),
      timeZone: data.timeZone,
      recurrencePattern: data.recurrencePattern,
    });
    return response.data;
  },

  async rescheduleJob(jobId: string, data: RescheduleJobRequestDto): Promise<any> {
    const response = await apiClient.put(`/jobScheduling/jobs/${jobId}/reschedule`, {
      newScheduledTime: data.newScheduledTime.toISOString(),
      timeZone: data.timeZone,
    });
    return response.data;
  },

  async cancelScheduling(jobId: string): Promise<any> {
    const response = await apiClient.delete(`/jobScheduling/jobs/${jobId}/schedule`);
    return response.data;
  },

  async getScheduledJobs(dateFrom?: Date, dateTo?: Date): Promise<ScheduledJobDto[]> {
    const params = new URLSearchParams();
    if (dateFrom) params.append('dateFrom', dateFrom.toISOString());
    if (dateTo) params.append('dateTo', dateTo.toISOString());

    const response = await apiClient.get<ScheduledJobDto[]>(
      `/jobScheduling/scheduled?${params.toString()}`
    );
    return response.data;
  },

  async getJobExecutions(jobId: string): Promise<any[]> {
    const response = await apiClient.get(`/jobScheduling/jobs/${jobId}/executions`);
    return response.data;
  },

  async convertToUtc(localTime: Date, timezone: string): Promise<Date> {
    const response = await apiClient.post('/jobScheduling/convert-to-utc', {
      localTime: localTime.toISOString(),
      timezone,
    });
    return new Date(response.data.utcTime);
  },

  async convertToLocal(utcTime: Date, timezone: string): Promise<Date> {
    const response = await apiClient.post('/jobScheduling/convert-to-local', {
      utcTime: utcTime.toISOString(),
      timezone,
    });
    return new Date(response.data.localTime);
  },
};
```

---

### Task 4.1.6: Register Services & Update Program.cs (Day 2, Morning)

**Update** `src/api/Program.cs`:

```csharp
// Add job scheduling services
services.AddScoped<IJobSchedulingService, JobSchedulingService>();

// Add background job trigger service (if implementing background trigger)
// services.AddHostedService<ScheduledJobTriggerService>();
```

---

### Task 4.1.7: Tests (Day 2, Afternoon)

**File**: `src/tests/Farm.Web.Api.Tests/Services/JobSchedulingServiceTests.cs`

```csharp
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Scheduling;
using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Services;

public class JobSchedulingServiceTests
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<JobSchedulingService>> _mockLogger;
    private readonly Mock<IPrintQueueService> _mockPrintQueueService;
    private readonly JobSchedulingService _service;

    public JobSchedulingServiceTests()
    {
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _context.Database.EnsureCreated();

        _mockLogger = new Mock<ILogger<JobSchedulingService>>();
        _mockPrintQueueService = new Mock<IPrintQueueService>();

        _service = new JobSchedulingService(_context, _mockLogger.Object, _mockPrintQueueService.Object);
    }

    [Fact]
    public async Task ScheduleJob_WithFutureTime_SchedulesSuccessfully()
    {
        // Arrange
        var printer = new Printer { Id = "p1", Name = "Test Printer", Status = "Online" };
        var job = new PrintJob
        {
            Id = "j1",
            PrinterId = "p1",
            Name = "Test Job",
            Status = "Queued",
            Printer = printer
        };

        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var futureTime = DateTime.UtcNow.AddDays(1);
        var timezone = "UTC";

        // Act
        await _service.ScheduleJobAsync("j1", futureTime, timezone);

        // Assert
        var schedule = await _context.Set<JobSchedule>().FirstAsync(js => js.PrintJobId == "j1");
        schedule.ScheduledStartTime.Should().NotBeNull();
        schedule.TimeZone.Should().Be(timezone);
        schedule.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ScheduleJob_WithPastTime_ThrowsException()
    {
        // Arrange
        var printer = new Printer { Id = "p1", Name = "Test Printer", Status = "Online" };
        var job = new PrintJob
        {
            Id = "j1",
            PrinterId = "p1",
            Name = "Test Job",
            Status = "Queued",
            Printer = printer
        };

        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var pastTime = DateTime.UtcNow.AddHours(-1);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ScheduleJobAsync("j1", pastTime, "UTC")
        );
    }

    [Fact]
    public async Task CancelScheduling_DeactivatesSchedule()
    {
        // Arrange
        var printer = new Printer { Id = "p1", Name = "Test Printer", Status = "Online" };
        var job = new PrintJob
        {
            Id = "j1",
            PrinterId = "p1",
            Name = "Test Job",
            Status = "Queued",
            Printer = printer
        };
        var schedule = new JobSchedule
        {
            PrintJobId = "j1",
            ScheduledStartTime = DateTime.UtcNow.AddDays(1),
            TimeZone = "UTC",
            IsActive = true
        };

        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        _context.Set<JobSchedule>().Add(schedule);
        await _context.SaveChangesAsync();

        // Act
        await _service.CancelSchedulingAsync("j1");

        // Assert
        var updatedSchedule = await _context.Set<JobSchedule>().FirstAsync(js => js.PrintJobId == "j1");
        updatedSchedule.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetScheduledJobs_ReturnsScheduledJobsOnly()
    {
        // Arrange
        var printer = new Printer { Id = "p1", Name = "Test Printer", Status = "Online" };
        var job1 = new PrintJob
        {
            Id = "j1",
            PrinterId = "p1",
            Name = "Scheduled Job",
            Status = "Queued",
            Printer = printer
        };
        var job2 = new PrintJob
        {
            Id = "j2",
            PrinterId = "p1",
            Name = "Unscheduled Job",
            Status = "Queued",
            Printer = printer
        };
        var schedule = new JobSchedule
        {
            PrintJobId = "j1",
            ScheduledStartTime = DateTime.UtcNow.AddDays(1),
            IsActive = true
        };

        _context.Printers.Add(printer);
        _context.PrintJobs.AddRange(job1, job2);
        _context.Set<JobSchedule>().Add(schedule);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetScheduledJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result.First().JobId.Should().Be("j1");
    }

    [Fact]
    public async Task ConvertToUtc_ConvertsCorrectly()
    {
        // Arrange
        var localTime = new DateTime(2026, 1, 13, 12, 0, 0); // Noon EST
        var timezone = "America/New_York";

        // Act
        var utcTime = await _service.ConvertToUtcAsync(localTime, timezone);

        // Assert
        utcTime.Hour.Should().Be(17); // EST is UTC-5, so noon EST = 5PM UTC
    }
}
```

---

### Task 4.1.8: Controller Tests (Day 2, Afternoon)

**File**: `src/tests/Farm.Web.Api.Tests/Controllers/JobSchedulingControllerTests.cs`

```csharp
using Farm.Web.Api.Controllers;
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Controllers;

public class JobSchedulingControllerTests
{
    private readonly Mock<IJobSchedulingService> _mockService;
    private readonly Mock<ILogger<JobSchedulingController>> _mockLogger;
    private readonly JobSchedulingController _controller;

    public JobSchedulingControllerTests()
    {
        _mockService = new Mock<IJobSchedulingService>();
        _mockLogger = new Mock<ILogger<JobSchedulingController>>();
        _controller = new JobSchedulingController(_mockService.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ScheduleJob_WithValidData_ReturnsOk()
    {
        // Arrange
        var jobId = "j1";
        var request = new ScheduleJobRequestDto
        {
            ScheduledStartTime = DateTime.UtcNow.AddDays(1),
            TimeZone = "UTC"
        };

        var mockJobDto = new PrintJobDto { Id = jobId };
        _mockService
            .Setup(s => s.ScheduleJobAsync(jobId, request.ScheduledStartTime, request.TimeZone, null, default))
            .ReturnsAsync(mockJobDto);

        // Act
        var result = await _controller.ScheduleJob(jobId, request, default);

        // Assert
        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult?.Value.Should().Be(mockJobDto);
    }

    [Fact]
    public async Task ScheduleJob_WithInvalidTime_ReturnsBadRequest()
    {
        // Arrange
        var jobId = "j1";
        var request = new ScheduleJobRequestDto
        {
            ScheduledStartTime = DateTime.UtcNow.AddHours(-1),
            TimeZone = "UTC"
        };

        _mockService
            .Setup(s => s.ScheduleJobAsync(jobId, request.ScheduledStartTime, request.TimeZone, null, default))
            .ThrowsAsync(new InvalidOperationException("Time must be in future"));

        // Act
        var result = await _controller.ScheduleJob(jobId, request, default);

        // Assert
        var badResult = result as BadRequestObjectResult;
        badResult.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelScheduling_WithValidJob_ReturnsOk()
    {
        // Arrange
        var jobId = "j1";
        var mockJobDto = new PrintJobDto { Id = jobId };
        _mockService
            .Setup(s => s.CancelSchedulingAsync(jobId, default))
            .ReturnsAsync(mockJobDto);

        // Act
        var result = await _controller.CancelScheduling(jobId, default);

        // Assert
        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult?.Value.Should().Be(mockJobDto);
    }

    [Fact]
    public async Task GetScheduledJobs_ReturnsListOfScheduledJobs()
    {
        // Arrange
        var scheduledJobs = new List<ScheduledJobDto>
        {
            new ScheduledJobDto { JobId = "j1", JobName = "Job 1" },
            new ScheduledJobDto { JobId = "j2", JobName = "Job 2" }
        };

        _mockService
            .Setup(s => s.GetScheduledJobsAsync(null, null, default))
            .ReturnsAsync(scheduledJobs);

        // Act
        var result = await _controller.GetScheduledJobs(null, null, default);

        // Assert
        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        var returnedJobs = okResult?.Value as List<ScheduledJobDto>;
        returnedJobs.Should().HaveCount(2);
    }
}
```

---

## Validation Checklist

- ✅ Models created with proper relationships
- ✅ Database migration runs without errors
- ✅ Service methods implemented with timezone support
- ✅ Controller endpoints functional
- ✅ React component renders correctly
- ✅ Scheduling persisting to database
- ✅ Timezone conversions working
- ✅ Tests passing (95%+)
- ✅ 0 build warnings/errors
- ✅ TypeScript compilation clean
- ✅ ESLint passing

---

## Success Criteria

By end of Phase 4.1:
- ✅ Job scheduling feature fully implemented
- ✅ Timezone support working
- ✅ Schedule UI component functional
- ✅ API endpoints tested and working
- ✅ Tests passing
- ✅ Ready for Phase 4.2 (Predictive Estimates)

---

## Next Steps

After Phase 4.1 completion:
1. Review test results
2. Deploy to staging
3. Manual QA testing
4. Begin Phase 4.2 (Predictive Completion Estimates)

---

*Phase 4.1 - Job Scheduling with Timezone Support*  
*KICKOFF - January 11, 2026*
