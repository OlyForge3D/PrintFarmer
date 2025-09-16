using System.Text;

namespace Farm.Web.Api.Services.Telemetry;

public interface IConsoleRedirectionService
{
    void RedirectConsoleOutput();
    void RestoreConsoleOutput();
    void WriteLine(string message);
    void WriteError(string message);
}

public class ConsoleRedirectionService : IConsoleRedirectionService, IDisposable
{
    private readonly IUnifiedLoggingService _unifiedLogger;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private UnifiedConsoleWriter? _consoleWriter;
    private UnifiedConsoleWriter? _errorWriter;
    private bool _disposed = false;

    public ConsoleRedirectionService(IUnifiedLoggingService unifiedLogger)
    {
        _unifiedLogger = unifiedLogger;
        _originalOut = Console.Out;
        _originalError = Console.Error;
    }

    public void RedirectConsoleOutput()
    {
        _consoleWriter = new UnifiedConsoleWriter(_unifiedLogger, LogLevel.Information, "Console");
        _errorWriter = new UnifiedConsoleWriter(_unifiedLogger, LogLevel.Error, "Console.Error");

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
        _unifiedLogger.LogWithContext(LogLevel.Information, "Console", message);
        _originalOut.WriteLine(message); // Also write to original console for debugging
    }

    public void WriteError(string message)
    {
        _unifiedLogger.LogWithContext(LogLevel.Error, "Console.Error", message);
        _originalError.WriteLine(message); // Also write to original console for debugging
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            RestoreConsoleOutput();
            _consoleWriter?.Dispose();
            _errorWriter?.Dispose();
            _disposed = true;
        }
    }
}

internal class UnifiedConsoleWriter : TextWriter
{
    private readonly IUnifiedLoggingService _unifiedLogger;
    private readonly LogLevel _logLevel;
    private readonly string _category;
    private readonly StringBuilder _buffer = new();

    public UnifiedConsoleWriter(IUnifiedLoggingService unifiedLogger, LogLevel logLevel, string category)
    {
        _unifiedLogger = unifiedLogger;
        _logLevel = logLevel;
        _category = category;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            FlushBuffer();
        }
        else if (value != '\r')
        {
            _buffer.Append(value);
        }
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _buffer.Append(value);
        }
        FlushBuffer();
    }

    private void FlushBuffer()
    {
        if (_buffer.Length > 0)
        {
            var message = _buffer.ToString();
            _buffer.Clear();

            _unifiedLogger.LogWithContext(_logLevel, _category, message);
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
    public static void WriteLine() => WriteLine("");
}
