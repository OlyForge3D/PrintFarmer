using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using Xunit.Abstractions;
using Xunit.Sdk;

// Assembly-level registration of custom test framework that will emit a timing summary
[assembly: TestFramework("Farm.Web.Api.Tests.TestInfrastructure.TimingReportingTestFramework", "Farm.Web.Api.Tests")]

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Custom xUnit test framework hook that, on disposal (after all tests complete),
/// reads the per-test timing CSV (emitted by <see cref="Farm.Web.Api.Tests.TestTimingAttribute"/>)
/// and writes an aggregated summary (percentiles & top hotspots) to both console output
/// and a companion file "test-timings-summary.txt" in the same directory.
/// Enabled by default when timing is enabled; can be disabled via PF_TIMING_SUMMARY=0.
/// </summary>
public sealed class TimingReportingTestFramework : XunitTestFramework
{
    private readonly IMessageSink _sink;
    private readonly bool _auto;

    public TimingReportingTestFramework(IMessageSink messageSink) : base(messageSink)
    {
        _sink = messageSink;
        _auto = ShouldEnableAuto();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GenerateSummary();
    }

    private static bool ShouldEnableAuto()
    {
        var env = Environment.GetEnvironmentVariable("PF_TIMING_AUTO");
        if (string.IsNullOrEmpty(env))
        {
            return false; // opt-in only
        }
        return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
    {
        if (!_auto)
        {
            return base.CreateExecutor(assemblyName);
        }
        return new AutoTimingFrameworkExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
    }

    private void GenerateSummary()
    {
        try
        {
            var summaryEnabled = Environment.GetEnvironmentVariable("PF_TIMING_SUMMARY");
            var timingEnabled = Environment.GetEnvironmentVariable("PF_TIMING");
            if (summaryEnabled == "0" || summaryEnabled == "false" || timingEnabled == "0" || timingEnabled == "false")
            {
                return;
            }
            var baseDir = AppContext.BaseDirectory;
            var csvPath = Path.Combine(baseDir, "test-timings.csv");
            if (!File.Exists(csvPath))
            {
                _sink.OnMessage(new DiagnosticMessage($"[TIMING-SUMMARY] No timing CSV found at {csvPath}; skipping."));
                return;
            }
            var allLines = File.ReadAllLines(csvPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            var lastRunIndex = allLines.FindLastIndex(l => l.StartsWith("RUN,"));
            string runId = "<unknown>";
            string runStarted = "<unknown>";
            if (lastRunIndex >= 0)
            {
                var runParts = allLines[lastRunIndex].Split(',');
                if (runParts.Length >= 3)
                {
                    runId = runParts[1];
                    runStarted = runParts[2];
                }
            }
            var lines = allLines
                .SkipWhile((l, idx) => idx <= lastRunIndex || l.StartsWith("TimestampUtc,"))
                .Where(l => !l.StartsWith("RUN,"))
                .Where(l => !l.StartsWith("TimestampUtc,"))
                .ToList();
            if (lines.Count == 0)
            {
                _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] Timing CSV empty; skipping."));
                return;
            }
            var entries = new List<TimingEntry>(lines.Count);
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 5)
                {
                    continue;
                }
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var durMs))
                {
                    continue;
                }
                entries.Add(new TimingEntry(parts[2], parts[3], parts[4], durMs));
            }
            if (entries.Count == 0)
            {
                _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] No valid entries parsed; skipping."));
                return;
            }
            var sorted = entries.OrderBy(e => e.DurationMs).ToList();
            double Percentile(double p)
            {
                if (sorted.Count == 1)
                {
                    return sorted[0].DurationMs;
                }
                var rank = (p / 100d) * (sorted.Count - 1);
                var lowIdx = (int)Math.Floor(rank);
                var highIdx = (int)Math.Ceiling(rank);
                if (lowIdx == highIdx)
                {
                    return sorted[lowIdx].DurationMs;
                }
                var frac = rank - lowIdx;
                return sorted[lowIdx].DurationMs + (sorted[highIdx].DurationMs - sorted[lowIdx].DurationMs) * frac;
            }
            var p50 = Percentile(50);
            var p90 = Percentile(90);
            var p95 = Percentile(95);
            var p99 = Percentile(99);
            var max = sorted[^1].DurationMs;
            var min = sorted[0].DurationMs;
            var mean = entries.Average(e => e.DurationMs);
            var hotspots = entries.OrderByDescending(e => e.DurationMs).Take(10).ToList();
            var byClass = entries
                .GroupBy(e => e.Class)
                .Select(g => new
                {
                    Class = g.Key,
                    Count = g.Count(),
                    TotalMs = g.Sum(x => x.DurationMs),
                    P90 = PercentileForGroup(g, 90),
                    Max = g.Max(x => x.DurationMs)
                })
                .OrderByDescending(x => x.P90)
                .Take(8)
                .ToList();
            static double PercentileForGroup(IGrouping<string, TimingEntry> g, double p)
            {
                var arr = g.OrderBy(e => e.DurationMs).Select(e => e.DurationMs).ToList();
                if (arr.Count == 0)
                {
                    return 0;
                }
                if (arr.Count == 1)
                {
                    return arr[0];
                }
                var rank = (p / 100d) * (arr.Count - 1);
                var low = (int)Math.Floor(rank);
                var high = (int)Math.Ceiling(rank);
                if (low == high)
                {
                    return arr[low];
                }
                var frac = rank - low;
                return arr[low] + (arr[high] - arr[low]) * frac;
            }
            var summaryLines = new List<string>
            {
                "==== Test Timing Summary ====",
                $"Run Id: {runId}",
                $"Run Started: {runStarted}",
                $"Entries This Run: {entries.Count}",
                string.Create(CultureInfo.InvariantCulture, $"Min: {min:F2} ms  P50: {p50:F2} ms  P90: {p90:F2} ms  P95: {p95:F2} ms  P99: {p99:F2} ms  Max: {max:F2} ms  Mean: {mean:F2} ms"),
                "",
                "Top 10 Slowest Executions:",
            };
            summaryLines.AddRange(hotspots.Select(h => string.Create(CultureInfo.InvariantCulture, $"  {h.DurationMs,8:F2} ms  {Truncate(h.Class + "." + h.Method, 110)}")));
            summaryLines.Add("");
            summaryLines.Add("Heaviest Classes (by P90):");
            foreach (var cls in byClass)
            {
                summaryLines.Add(string.Create(CultureInfo.InvariantCulture, $"  P90 {cls.P90,7:F2} ms  Max {cls.Max,7:F2} ms  Count {cls.Count,3}  {Truncate(cls.Class, 90)}"));
            }
            var summaryPath = Path.Combine(baseDir, "test-timings-summary.txt");
            File.WriteAllLines(summaryPath, summaryLines);
            foreach (var l in summaryLines)
            {
                _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] " + l));
            }
        }
        catch (Exception ex)
        {
            _sink.OnMessage(new DiagnosticMessage($"[TIMING-SUMMARY] Failed to generate summary: {ex.Message}"));
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value[..(max - 3)] + "...";
    }

    private sealed record TimingEntry(string Category, string Class, string Method, double DurationMs);
}

internal sealed class AutoTimingFrameworkExecutor : XunitTestFrameworkExecutor
{
    public AutoTimingFrameworkExecutor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : base(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
    }

    protected override void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
    {
        var timingSink = new AutoTimingMessageSink(executionMessageSink);
        base.RunTestCases(testCases, timingSink, executionOptions);
    }
}

internal sealed class AutoTimingMessageSink : LongLivedMarshalByRefObject, IMessageSink
{
    private readonly IMessageSink _inner;
    private readonly ConcurrentDictionary<string, Stopwatch> _sw = new();

    public AutoTimingMessageSink(IMessageSink inner) => _inner = inner;

    public bool OnMessage(IMessageSinkMessage message)
    {
        switch (message)
        {
            case ITestStarting ts:
                _sw[ts.Test.DisplayName] = Stopwatch.StartNew();
                break;
            case ITestFinished tf:
                if (_sw.TryRemove(tf.Test.DisplayName, out var watch))
                {
                    watch.Stop();
                    try
                    {
                        var methodInfo = tf.Test.TestCase.TestMethod.Method.ToRuntimeMethod();
                        var classType = tf.Test.TestCase.TestMethod.TestClass.Class.ToRuntimeType();
                        // Consider both method-level and class-level attributes. If either is present, skip auto logging.
                        var hasAttr = (methodInfo?.IsDefined(typeof(TestTimingAttribute), false) ?? false)
                                      || (classType?.IsDefined(typeof(TestTimingAttribute), false) ?? false);
                        if (!hasAttr)
                        {
                            var cls = tf.Test.TestCase.TestMethod.TestClass.Class.ToRuntimeType()?.FullName ?? tf.Test.TestCase.TestMethod.TestClass.Class.Name;
                            var method = tf.Test.TestCase.TestMethod.Method.Name;
                            TestTimingLog.Log("Auto", cls, method, watch.Elapsed.TotalMilliseconds);
                        }
                    }
                    catch
                    {
                        // swallow errors
                    }
                }
                break;
        }
        return _inner.OnMessage(message);
    }
}
