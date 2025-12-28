using System;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Tests.Builders;

/// <summary>
/// Builder for creating PrintJob test objects with fluent API
/// </summary>
public class PrintJobBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Test Print Job";
    private Guid _gcodeFileId = Guid.NewGuid();
    private Guid? _assignedPrinterId = Guid.NewGuid();
    private PrintJobStatus _status = PrintJobStatus.Queued;
    private int _priority = 0;
    private int _queuePosition = 1;
    private decimal? _requiredNozzleDiameter;
    private string? _requiredMaterialType;
    private TimeSpan? _estimatedPrintTime;
    private double? _estimatedFilamentUsage;
    private DateTime? _actualStartTime;
    private DateTime? _actualEndTime;
    private TimeSpan? _actualPrintTime;
    private double? _actualFilamentUsage;
    private string? _failureReason;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime _updatedAt = DateTime.UtcNow;
    private DateTime _queuedAt = DateTime.UtcNow;
    private GcodeFile? _gcodeFile;
    private Printer? _assignedPrinter;

    public PrintJobBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public PrintJobBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public PrintJobBuilder WithGcodeFileId(Guid gcodeFileId)
    {
        _gcodeFileId = gcodeFileId;
        return this;
    }

    public PrintJobBuilder WithGcodeFile(GcodeFile gcodeFile)
    {
        _gcodeFile = gcodeFile;
        _gcodeFileId = gcodeFile.Id;
        return this;
    }

    public PrintJobBuilder WithAssignedPrinterId(Guid? printerId)
    {
        _assignedPrinterId = printerId;
        return this;
    }

    public PrintJobBuilder WithAssignedPrinter(Printer printer)
    {
        _assignedPrinter = printer;
        _assignedPrinterId = printer.Id;
        return this;
    }

    public PrintJobBuilder WithStatus(PrintJobStatus status)
    {
        _status = status;
        return this;
    }

    public PrintJobBuilder WithPriority(int priority)
    {
        _priority = priority;
        return this;
    }

    public PrintJobBuilder WithQueuePosition(int position)
    {
        _queuePosition = position;
        return this;
    }

    public PrintJobBuilder WithRequiredNozzleDiameter(decimal diameter)
    {
        _requiredNozzleDiameter = diameter;
        return this;
    }

    public PrintJobBuilder WithRequiredMaterialType(string materialType)
    {
        _requiredMaterialType = materialType;
        return this;
    }

    public PrintJobBuilder WithEstimatedPrintTime(TimeSpan time)
    {
        _estimatedPrintTime = time;
        return this;
    }

    public PrintJobBuilder WithEstimatedFilamentUsage(double grams)
    {
        _estimatedFilamentUsage = grams;
        return this;
    }

    public PrintJobBuilder WithActualStartTime(DateTime startTime)
    {
        _actualStartTime = startTime;
        return this;
    }

    public PrintJobBuilder WithActualEndTime(DateTime endTime)
    {
        _actualEndTime = endTime;
        return this;
    }

    public PrintJobBuilder WithActualPrintTime(TimeSpan time)
    {
        _actualPrintTime = time;
        return this;
    }

    public PrintJobBuilder WithActualFilamentUsage(double grams)
    {
        _actualFilamentUsage = grams;
        return this;
    }

    public PrintJobBuilder WithFailureReason(string reason)
    {
        _failureReason = reason;
        return this;
    }

    public PrintJobBuilder WithCreatedAt(DateTime createdAt)
    {
        _createdAt = createdAt;
        return this;
    }

    public PrintJobBuilder WithQueuedAt(DateTime queuedAt)
    {
        _queuedAt = queuedAt;
        return this;
    }

    /// <summary>
    /// Creates a high priority job (priority = 10)
    /// </summary>
    public PrintJobBuilder AsHighPriority()
    {
        _priority = 10;
        return this;
    }

    /// <summary>
    /// Creates a queued job
    /// </summary>
    public PrintJobBuilder AsQueued()
    {
        _status = PrintJobStatus.Queued;
        return this;
    }

    /// <summary>
    /// Creates an assigned job
    /// </summary>
    public PrintJobBuilder AsAssigned()
    {
        _status = PrintJobStatus.Assigned;
        return this;
    }

    /// <summary>
    /// Creates a printing job with start time
    /// </summary>
    public PrintJobBuilder AsPrinting()
    {
        _status = PrintJobStatus.Printing;
        _actualStartTime = DateTime.UtcNow.AddMinutes(-5);
        return this;
    }

    /// <summary>
    /// Creates a completed job with times
    /// </summary>
    public PrintJobBuilder AsCompleted()
    {
        _status = PrintJobStatus.Completed;
        _actualStartTime = DateTime.UtcNow.AddHours(-2);
        _actualEndTime = DateTime.UtcNow.AddHours(-1);
        _actualPrintTime = TimeSpan.FromHours(1);
        return this;
    }

    /// <summary>
    /// Creates a failed job with failure reason
    /// </summary>
    public PrintJobBuilder AsFailed(string reason = "Test failure")
    {
        _status = PrintJobStatus.Failed;
        _failureReason = reason;
        _actualStartTime = DateTime.UtcNow.AddHours(-1);
        _actualEndTime = DateTime.UtcNow;
        return this;
    }

    /// <summary>
    /// Creates a cancelled job
    /// </summary>
    public PrintJobBuilder AsCancelled()
    {
        _status = PrintJobStatus.Cancelled;
        return this;
    }

    public PrintJob Build()
    {
        GcodeFile gcodeFile = _gcodeFile ?? new GcodeFile
        {
            Id = _gcodeFileId,
            OriginalFileName = "Test.gcode",
            DisplayName = "Test.gcode",
            FilePath = "/tmp/Test.gcode",
            FileSizeBytes = 0,
            FileHash = string.Empty,
            UploadedAt = _createdAt,
            Source = GcodeSource.Upload,
            CreatedAt = _createdAt,
            UpdatedAt = _updatedAt
        };

        return new PrintJob
        {
            Id = _id,
            Name = _name,
            GcodeFileId = _gcodeFileId,
            AssignedPrinterId = _assignedPrinterId,
            Status = _status,
            Priority = _priority,
            QueuePosition = _queuePosition,
            RequiredNozzleDiameter = _requiredNozzleDiameter,
            RequiredMaterialType = _requiredMaterialType,
            EstimatedPrintTime = _estimatedPrintTime,
            EstimatedFilamentUsage = _estimatedFilamentUsage,
            ActualStartTime = _actualStartTime,
            ActualEndTime = _actualEndTime,
            ActualPrintTime = _actualPrintTime,
            ActualFilamentUsage = _actualFilamentUsage,
            FailureReason = _failureReason,
            CreatedAt = _createdAt,
            UpdatedAt = _updatedAt,
            QueuedAt = _queuedAt,
            GcodeFile = gcodeFile,
            AssignedPrinter = _assignedPrinter
        };
    }
}
