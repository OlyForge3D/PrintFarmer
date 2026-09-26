namespace Farm.Infrastructure.Services.Printers;

public sealed class PrinterControlException : Exception
{
    public PrinterControlException()
        : this(503, "admission_unavailable", "Control persistence is unavailable.")
    {
    }

    public PrinterControlException(string message)
        : this(503, "admission_unavailable", message)
    {
    }

    public PrinterControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrinterControlException(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; } = 503;

    public string Code { get; } = "admission_unavailable";
}
