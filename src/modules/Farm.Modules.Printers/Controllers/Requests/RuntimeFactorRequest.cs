namespace Farm.Modules.Printers.Controllers.Requests;

/// <summary>One independent runtime override in percent; 100 means a multiplier of one.</summary>
public sealed record RuntimeFactorRequest
{
    public double? Percent { get; init; }
}
