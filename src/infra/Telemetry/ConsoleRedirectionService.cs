using System.Text;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// Service for redirecting console output to the unified logging system.
/// Captures stdout and stderr and routes them through structured logging.
/// </summary>
public interface IConsoleRedirectionService
{
    /// <summary>
    /// Redirects Console.Out and Console.Error to the unified logging system.
    /// </summary>
    void RedirectConsoleOutput();

    /// <summary>
    /// Restores the original console output streams.
    /// </summary>
    void RestoreConsoleOutput();

    /// <summary>
    /// Writes a line to both the unified logger and original console output.
    /// </summary>
    /// <param name="message">The message to write.</param>
    void WriteLine(string message);

    /// <summary>
    /// Writes an error message to both the unified logger and original console error.
    /// </summary>
    /// <param name="message">The error message to write.</param>
    void WriteError(string message);
}

public class ConsoleRedirectionService(ILogger<ConsoleRedirectionService> unifiedLogger) : IConsoleRedirectionService, IDisposable
{
    private readonly ILogger<ConsoleRedirectionService> _unifiedLogger = unifiedLogger;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Do not dispose system-owned Console.Out")]
    private readonly TextWriter _originalOut = Console.Out;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Do not dispose system-owned Console.Error")]
    private readonly TextWriter _originalError = Console.Error;
    private UnifiedConsoleWriter? _consoleWriter;
    private UnifiedConsoleWriter? _errorWriter;
    private bool _disposed = false;

    public void RedirectConsoleOutput()
    {
        _consoleWriter = new UnifiedConsoleWriter(_unifiedLogger, LogLevel.Information);
        _errorWriter = new UnifiedConsoleWriter(_unifiedLogger, LogLevel.Error);

        Console.SetOut(_consoleWriter);
        Console.SetError(_errorWriter);
    }

    public void RestoreConsoleOutput()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    public void WriteLine(string message)
    {
        _unifiedLogger.LogInformation("{Message}", message);
        _originalOut.WriteLine(message); // Also write to original console for debugging
    }

    public void WriteError(string message)
    {
        _unifiedLogger.LogError("{Message}", message);
        _originalError.WriteLine(message); // Also write to original console for debugging
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            RestoreConsoleOutput();
            _consoleWriter?.Dispose();
            _errorWriter?.Dispose();

            // Do NOT dispose _originalOut or _originalError (system resources)
            _disposed = true;
        }
    }
}

internal class UnifiedConsoleWriter(ILogger<ConsoleRedirectionService> unifiedLogger, LogLevel logLevel) : TextWriter
{
    private readonly ILogger<ConsoleRedirectionService> _unifiedLogger = unifiedLogger;
    private readonly LogLevel _logLevel = logLevel;
    private readonly StringBuilder _buffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            FlushBuffer();
        }
        else if (value != '\r')
        {
            _ = _buffer.Append(value);
        }
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = _buffer.Append(value);
        }

        FlushBuffer();
    }

    private void FlushBuffer()
    {
        if (_buffer.Length > 0)
        {
            string message = _buffer.ToString();
            _ = _buffer.Clear();

            _unifiedLogger.Log(_logLevel, "{Message}", message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FlushBuffer();
        }

        base.Dispose(disposing);
    }
}

// Static helper class for easy console replacement
public static class UnifiedConsole
{
    private static IConsoleRedirectionService? _redirectionService;

    public static void Initialize(IConsoleRedirectionService redirectionService)
    {
        _redirectionService = redirectionService;
    }

    public static void WriteLine(string message)
    {
        if (_redirectionService != null)
        {
            _redirectionService.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    public static void WriteError(string message)
    {
        if (_redirectionService != null)
        {
            _redirectionService.WriteError(message);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }    // Migration helpers to replace Console.WriteLine calls

    public static void Write(string message) => WriteLine(message);

    public static void Write(object value) => WriteLine(value?.ToString() ?? "null");

    public static void WriteLine(object value) => WriteLine(value?.ToString() ?? "null");

    public static void WriteLine() => WriteLine(string.Empty);
}
