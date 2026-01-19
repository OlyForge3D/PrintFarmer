namespace Farm.Infrastructure;

/// <summary>
/// Configuration settings for SignalR logging and connection behavior
/// </summary>
/// <param name="LogLevel">The SignalR logging level (Debug, Information, Warning, Error, None)</param>
/// <param name="ConsoleLoggingEnabled">Whether SignalR console logging is enabled in the client</param>
public sealed record SignalRSettingsDto(
    string LogLevel,
    bool ConsoleLoggingEnabled)
{
    public SignalRSettingsDto() : this("Information", true)
    {
    }
}
