using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Farm.Web.Api.Tests.Middleware;

/// <summary>
/// Regression tests for issue #2348: a client abort must not be recorded as a 500 in
/// the API-call telemetry/SLO metric, since it's not a server-side error.
/// </summary>
public class TelemetryMiddlewareCancellationTests
{
    private sealed class RecordingTelemetryService : IPrintFarmerTelemetryService
    {
        public readonly List<(string Endpoint, string Method, int StatusCode)> RecordedCalls = new();

        public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) => null;

        public void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration)
        {
            RecordedCalls.Add((endpoint, method, statusCode));
        }

        public void RecordPrinterOperation(string operation, string printerId, bool success)
        {
        }

        public void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null)
        {
        }

        public void RecordFileOperation(string operation, string fileType, long? fileSize = null)
        {
        }

        public void RecordDatabaseOperation(string table, string operation, int recordCount)
        {
        }

        public void RecordPagedQuery(string endpoint, int rowCount, long payloadBytes, bool cappedToMaxPageSize)
        {
        }
    }

    [Fact]
    public async Task ClientAbort_RecordsStatus499NotStatus500_AndStillPropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        DefaultHttpContext context = new()
        {
            RequestAborted = cts.Token
        };

        RecordingTelemetryService telemetry = new();
        TelemetryMiddleware middleware = new(_ => throw new OperationCanceledException(cts.Token), telemetry);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        (string _, string _, int statusCode) = Assert.Single(telemetry.RecordedCalls);
        Assert.Equal(499, statusCode);
    }

    [Fact]
    public async Task UnrelatedCancellation_StillRecordsStatus500()
    {
        using CancellationTokenSource unrelatedCts = new();
        unrelatedCts.Cancel();

        DefaultHttpContext context = new()
        {
            RequestAborted = CancellationToken.None
        };

        RecordingTelemetryService telemetry = new();
        TelemetryMiddleware middleware = new(_ => throw new OperationCanceledException(unrelatedCts.Token), telemetry);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        (string _, string _, int statusCode) = Assert.Single(telemetry.RecordedCalls);
        Assert.Equal(500, statusCode);
    }
}
