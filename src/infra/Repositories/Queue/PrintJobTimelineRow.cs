using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Narrow, untracked timeline input projected from a print job and the names of its related
/// G-code file and assigned printer, instead of materializing the full entity graph (issue #3908).
/// </summary>
/// <param name="Id">The print job identifier.</param>
/// <param name="Name">The print job display name, used when no G-code file name is available.</param>
/// <param name="GcodeFileName">The related G-code file name, or <c>null</c> when no file row is joined.</param>
/// <param name="AssignedPrinterName">The assigned printer name, or <c>null</c> when no printer row is joined.</param>
/// <param name="Status">The persisted job status.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="ActualStartTime">When printing actually started, if it has.</param>
/// <param name="ActualEndTime">When printing actually ended, if it has.</param>
/// <param name="ActualPrintTime">The recorded actual print duration, if any.</param>
/// <param name="EstimatedPrintTime">The estimated print duration, if any.</param>
public sealed record PrintJobTimelineRow(
    Guid Id,
    string Name,
    string? GcodeFileName,
    string? AssignedPrinterName,
    PrintJobStatus Status,
    DateTime CreatedAt,
    DateTime? ActualStartTime,
    DateTime? ActualEndTime,
    TimeSpan? ActualPrintTime,
    TimeSpan? EstimatedPrintTime);
