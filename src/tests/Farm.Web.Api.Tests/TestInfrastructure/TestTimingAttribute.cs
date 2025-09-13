using System.Diagnostics;
using System.Reflection;
using Xunit.Sdk;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Lightweight per-test timing instrumentation. Emits a console line and appends to test-timings.csv
/// unless PF_TIMING=0. Safe to apply broadly (sub-millisecond overhead typically).
/// CSV Header: TimestampUtc,DurationMs,Category,Class,Method
/// </summary>
public sealed class TestTimingAttribute : BeforeAfterTestAttribute
{
    private readonly string _category;

    [ThreadStatic]
    private static Stopwatch? _sw;

    public TestTimingAttribute(string category = "DbHeavy") => _category = category;

    public override void Before(MethodInfo methodUnderTest)
    {
        ArgumentNullException.ThrowIfNull(methodUnderTest);
        // Determine if this attribute instance is applied at the method level.
        // methodUnderTest.GetCustomAttributes(..., inherit:false) returns ONLY method-level attributes (not class-level ones).
        var methodLevelAttrs = methodUnderTest.GetCustomAttributes(typeof(TestTimingAttribute), inherit: false);
        bool isMethodLevel = methodLevelAttrs.Any(a => ReferenceEquals(a, this));

        // If this instance is NOT method-level (therefore class-level) AND there exists at least one method-level TestTimingAttribute,
        // we suppress class-level timing to avoid duplicate entries (the method-level attribute is considered more specific).
        if (!isMethodLevel && methodLevelAttrs.Length > 0)
        {
            _sw = null; // Explicitly ensure After() no-ops.
            return;
        }

        _sw = Stopwatch.StartNew();
    }

    public override void After(MethodInfo methodUnderTest)
    {
        ArgumentNullException.ThrowIfNull(methodUnderTest);
        var sw = _sw;
        if (sw is null)
        {
            return;
        }
        sw.Stop();
        var ms = sw.Elapsed.TotalMilliseconds;
        var cls = methodUnderTest.DeclaringType?.FullName ?? "<unknown>";
        var method = methodUnderTest.Name;
        TestTimingLog.Log(_category, cls, method, ms);
    }
}

/// <summary>
/// Shared helper for writing timing lines (console + CSV) with initialization & run segmentation.
/// Used by explicit <see cref="TestTimingAttribute"/> and the automatic timing sink.
/// </summary>
internal static class TestTimingLog
{
    private static readonly object _lock = new();
    private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "test-timings.csv");
    private static volatile bool _initialized;

    public static void Log(string category, string cls, string method, double ms)
    {
        Console.WriteLine($"[TIMING] {category} {cls}.{method} {ms:F2} ms");

        var enabled = Environment.GetEnvironmentVariable("PF_TIMING");
        if (enabled == "0" || enabled == "false")
        {
            return;
        }

        EnsureInitialized();
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{ms:F2},{category},{cls},{method}");
        lock (_lock)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }
            var reset = Environment.GetEnvironmentVariable("PF_TIMING_RESET");
            if (reset == "1" || string.Equals(reset, "true", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(_logPath))
                    {
                        File.Delete(_logPath);
                    }
                }
                catch
                {
                    // swallow – non-fatal
                }
            }
            if (!File.Exists(_logPath))
            {
                File.AppendAllText(_logPath, "RUN," + Guid.NewGuid() + "," + DateTime.UtcNow.ToString("O") + Environment.NewLine);
                File.AppendAllText(_logPath, "TimestampUtc,DurationMs,Category,Class,Method" + Environment.NewLine);
            }
            _initialized = true;
        }
    }
}
