namespace Farm.Modules.Gcode.Services.Gcode;

public enum PrinterFileForceRefreshStatus
{
    Completed,
    NotIdle,
    Busy,
    Incomplete,
    Superseded,
}

/// <summary>Outcome of an operator-requested scan; only <see cref="PrinterFileForceRefreshStatus.Completed"/> proves a full listing was published.</summary>
public sealed record PrinterFileForceRefreshResult(
    PrinterFileForceRefreshStatus Status, string ReasonCode, int FileCount = 0);
