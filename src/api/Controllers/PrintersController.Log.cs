namespace Farm.Web.Api.Controllers;

internal static partial class PrintersControllerLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Fast timeout for printer {PrinterName} ({PrinterId})")]
    public static partial void FastTimeout(this ILogger logger, string printerName, Guid printerId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Error getting status for printer {PrinterName} ({PrinterId})")]
    public static partial void ErrorGettingStatus(this ILogger logger, Exception exception, string printerName, Guid printerId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Status timeout for printer {PrinterId}")]
    public static partial void StatusTimeout(this ILogger logger, Guid printerId);
}
