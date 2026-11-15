namespace Farm.Infrastructure.Services.Printers;

/// <summary>A backend definitively rejected a command because its endpoint is not supported.</summary>
public sealed class PrinterBackendUnsupportedException : NotSupportedException
{
    public PrinterBackendUnsupportedException()
    {
    }

    public PrinterBackendUnsupportedException(string message) : base(message)
    {
    }

    public PrinterBackendUnsupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
