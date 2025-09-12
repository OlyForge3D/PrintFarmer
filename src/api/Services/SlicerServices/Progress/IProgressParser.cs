namespace Farm.Web.Api.Services.SlicerServices.Progress;

public enum SlicerProgressState
{
    Unknown,
    InProgress,
    Completed,
    Failed
}

public record ProgressUpdate(double Percentage, string? Message, SlicerProgressState State = SlicerProgressState.InProgress);

public interface IProgressParser
{
    /// Parse a single line of slicer stdout/stderr and return a ProgressUpdate when recognized, otherwise null.
    ProgressUpdate? Parse(string line);
}
