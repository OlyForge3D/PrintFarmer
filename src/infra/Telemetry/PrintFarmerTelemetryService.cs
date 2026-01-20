using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Farm.Infrastructure.Telemetry;

public interface IPrintFarmerTelemetryService
{
    Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal);

    void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration);

    void RecordPrinterOperation(string operation, string printerId, bool success);

    void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null);

    void RecordFileOperation(string operation, string fileType, long? fileSize = null);

    void RecordDatabaseOperation(string table, string operation, int recordCount);
}

public sealed class PrintFarmerTelemetryService : IPrintFarmerTelemetryService, IDisposable
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _apiCallsCounter;
    private readonly Histogram<double> _apiCallDuration;
    private readonly Counter<long> _printerOperationsCounter;
    private readonly Counter<long> _slicerOperationsCounter;
    private readonly Counter<long> _fileOperationsCounter;
    private readonly Counter<long> _databaseOperationsCounter;
    private readonly Histogram<double> _slicerJobDuration;
    private readonly Histogram<long> _fileSizes;

    public PrintFarmerTelemetryService()
    {
        _activitySource = new ActivitySource("PrintFarmer.API");
        _meter = new Meter("PrintFarmer.API");

        // API metrics
        _apiCallsCounter = _meter.CreateCounter<long>(
            "printfarmer_api_calls_total",
            description: "Total number of API calls");

        _apiCallDuration = _meter.CreateHistogram<double>(
            "printfarmer_api_call_duration_seconds",
            unit: "s",
            description: "Duration of API calls in seconds");

        // Printer metrics
        _printerOperationsCounter = _meter.CreateCounter<long>(
            "printfarmer_printer_operations_total",
            description: "Total number of printer operations");

        // Slicer metrics
        _slicerOperationsCounter = _meter.CreateCounter<long>(
            "printfarmer_slicer_operations_total",
            description: "Total number of slicer operations");

        _slicerJobDuration = _meter.CreateHistogram<double>(
            "printfarmer_slicer_job_duration_seconds",
            unit: "s",
            description: "Duration of slicer jobs in seconds");

        // File metrics
        _fileOperationsCounter = _meter.CreateCounter<long>(
            "printfarmer_file_operations_total",
            description: "Total number of file operations");

        _fileSizes = _meter.CreateHistogram<long>(
            "printfarmer_file_size_bytes",
            unit: "bytes",
            description: "File sizes in bytes");

        // Database metrics
        _databaseOperationsCounter = _meter.CreateCounter<long>(
            "printfarmer_database_operations_total",
            description: "Total number of database operations");
    }

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return _activitySource.StartActivity(name, kind);
    }

    public void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration)
    {
        KeyValuePair<string, object?>[] tags = new[]
        {
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("status_code", statusCode),
            new KeyValuePair<string, object?>("status_class", GetStatusClass(statusCode))
        };

        _apiCallsCounter.Add(1, tags);
        _apiCallDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordPrinterOperation(string operation, string printerId, bool success)
    {
        KeyValuePair<string, object?>[] tags = new[]
        {
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("printer_id", printerId),
            new KeyValuePair<string, object?>("success", success)
        };

        _printerOperationsCounter.Add(1, tags);
    }

    public void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null)
    {
        KeyValuePair<string, object?>[] tags = new[]
        {
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("engine", engine),
            new KeyValuePair<string, object?>("success", success)
        };

        _slicerOperationsCounter.Add(1, tags);

        if (duration.HasValue)
        {
            _slicerJobDuration.Record(duration.Value.TotalSeconds, tags);
        }
    }

    public void RecordFileOperation(string operation, string fileType, long? fileSize = null)
    {
        KeyValuePair<string, object?>[] tags = new[]
        {
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("file_type", fileType)
        };

        _fileOperationsCounter.Add(1, tags);

        if (fileSize.HasValue)
        {
            _fileSizes.Record(fileSize.Value, tags);
        }
    }

    public void RecordDatabaseOperation(string table, string operation, int recordCount)
    {
        KeyValuePair<string, object?>[] tags = new[]
        {
            new KeyValuePair<string, object?>("table", table),
            new KeyValuePair<string, object?>("operation", operation)
        };

        _databaseOperationsCounter.Add(1, tags);
    }

    private static string GetStatusClass(int statusCode)
    {
        return statusCode switch
        {
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 => "5xx",
            _ => "unknown"
        };
    }

    public void Dispose()
    {
        _activitySource?.Dispose();
        _meter?.Dispose();
    }
}
