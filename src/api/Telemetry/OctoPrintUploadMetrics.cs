using System.Diagnostics;
using System.Diagnostics.Metrics;
using Farm.Infrastructure.Services.Queue;

namespace Farm.Web.Api.Telemetry;

/// <summary>Low-cardinality metrics for external OctoPrint-compatible upload-and-print requests.</summary>
public static class OctoPrintUploadMetrics
{
    public const string MeterName = "PrintFarmer.API";

    private static readonly Meter s_meter = new(MeterName);
    private static readonly Counter<long> s_queueAdmissions = s_meter.CreateCounter<long>(
        "octoprint.upload_print.queue_admissions",
        description: "OctoPrint-compatible upload-and-print queue admission outcomes");

    public static void RecordQueued() =>
        s_queueAdmissions.Add(1, new KeyValuePair<string, object?>("outcome", "queued"));

    public static void RecordRejected() =>
        s_queueAdmissions.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));

    public static void RecordIndeterminate() =>
        s_queueAdmissions.Add(1, new KeyValuePair<string, object?>("outcome", "indeterminate"));

    public static string ToReasonCode(QueueJobAddFailureReason reason) =>
        reason switch
        {
            QueueJobAddFailureReason.GcodeFileNotFound => "gcode_file_not_found",
            QueueJobAddFailureReason.NoAvailablePrinter => "no_available_printer",
            QueueJobAddFailureReason.PrinterModelMismatch => "printer_model_mismatch",
            QueueJobAddFailureReason.PrinterAccessDenied => "printer_access_denied",
            QueueJobAddFailureReason.NozzleMismatch => "nozzle_mismatch",
            QueueJobAddFailureReason.MaterialMismatch => "material_mismatch",
            QueueJobAddFailureReason.QueueCapacity => "queue_capacity",
            QueueJobAddFailureReason.QueueGroupAccessDenied => "printer_group_access_denied",
            QueueJobAddFailureReason.UnexpectedError => "unexpected_error",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown queue admission failure reason.")
        };
}
