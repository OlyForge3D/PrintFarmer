using System;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Thrown by backend HTTP clients when a printer firmware refuses a control
/// command because the printer is currently busy (typically HTTP 409 Conflict
/// from PrusaLink or OctoPrint mid-print). The service layer translates this
/// into <see cref="PrinterControlOutcome.BackendBusy"/> so the API can return
/// 502 Bad Gateway instead of collapsing to a generic 404.
/// </summary>
public sealed class PrinterBackendBusyException : Exception
{
    public PrinterBackendBusyException()
    {
    }

    public PrinterBackendBusyException(string message) : base(message)
    {
    }

    public PrinterBackendBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
