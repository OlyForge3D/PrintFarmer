namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Settings for job dispatch retry configuration with exponential backoff.
/// </summary>
public class JobDispatchRetrySettings
{
    /// <summary>Gets or sets the maximum number of retry attempts.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Gets or sets the base delay in milliseconds between retry attempts.</summary>
    public int BaseDelayMs { get; set; } = 250;

    /// <summary>Gets or sets the multiplier for exponential backoff.</summary>
    public double Multiplier { get; set; } = 2.0;
}
