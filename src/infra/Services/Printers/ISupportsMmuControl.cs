using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Semantic MMU commands; implementing this transport does not prove a printer has configured MMU macros.</summary>
public interface ISupportsMmuControl
{
    /// <summary>Executes a typed MMU action using the plugin's allowlisted protocol commands.</summary>
    Task<bool> ExecuteMmuAsync(string baseUrl, MmuControlRequest request, PrinterCredential? credential, CancellationToken ct = default);
}

/// <summary>MMU operations; the plugin determines valid action/protocol combinations.</summary>
public enum MmuControlAction
{
    ChangeTool,
    SelectTool,
    Home,
    Recover,
    Load,
    Eject,
    Unload,
}

/// <summary>Explicit MMU protocol selection, never a raw command or macro name.</summary>
public enum MmuControlProtocol
{
    HappyHare,
    Qidibox,
    Afc,
}

/// <summary>A semantic MMU operation, with only bounded tool/gate identifiers or a validated lane name.</summary>
public sealed record MmuControlRequest(
    MmuControlAction Action,
    MmuControlProtocol Protocol = MmuControlProtocol.HappyHare,
    int? Tool = null,
    int? GateIndex = null,
    string? LaneName = null);
