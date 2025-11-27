using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

// Assembly-level registration of custom test framework that will emit a timing summary
[assembly: TestFramework("Farm.Web.Api.Tests.TestInfrastructure.TimingReportingTestFramework", "Farm.Web.Api.Tests")]

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Custom xUnit test framework hook that, on disposal (after all tests complete),
/// reads the per-test timing CSV (emitted by <see cref="TestTimingAttribute"/>)
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
        string? env = Environment.GetEnvironmentVariable("PF_TIMING_AUTO");
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
            string? summaryEnabled = Environment.GetEnvironmentVariable("PF_TIMING_SUMMARY");
            string? timingEnabled = Environment.GetEnvironmentVariable("PF_TIMING");
            if (summaryEnabled == "0" || summaryEnabled == "false" || timingEnabled == "0" || timingEnabled == "false")
            {
                return;
            }
            string baseDir = AppContext.BaseDirectory;
            string csvPath = Path.Combine(baseDir, "test-timings.csv");
            if (!File.Exists(csvPath))
            {
                _ = _sink.OnMessage(new DiagnosticMessage($"[TIMING-SUMMARY] No timing CSV found at {csvPath}; skipping."));
                return;
            }
            List<string> allLines = File.ReadAllLines(csvPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            int lastRunIndex = allLines.FindLastIndex(l => l.StartsWith("RUN,"));
            string runId = "<unknown>";
            string runStarted = "<unknown>";
            if (lastRunIndex >= 0)
            {
                string[] runParts = allLines[lastRunIndex].Split(',');
                if (runParts.Length >= 3)
                {
                    runId = runParts[1];
                    runStarted = runParts[2];
                }
            }
            List<string> lines = allLines
                .SkipWhile((l, idx) => idx <= lastRunIndex || l.StartsWith("TimestampUtc,"))
                .Where(l => !l.StartsWith("RUN,"))
                .Where(l => !l.StartsWith("TimestampUtc,"))
                .ToList();
            if (lines.Count == 0)
            {
                _ = _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] Timing CSV empty; skipping."));
                return;
            }
            List<TimingEntry> entries = new List<TimingEntry>(lines.Count);
            foreach (string? line in lines)
            {
                string[] parts = line.Split(',');
                if (parts.Length < 5)
                {
                    continue;
                }
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double durMs))
                {
                    continue;
                }
                entries.Add(new TimingEntry(parts[2], parts[3], parts[4], durMs));
            }
            if (entries.Count == 0)
            {
                _ = _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] No valid entries parsed; skipping."));
                return;
            }
            List<TimingEntry> sorted = entries.OrderBy(e => e.DurationMs).ToList();
            double Percentile(double p)
            {
                if (sorted.Count == 1)
                {
                    return sorted[0].DurationMs;
                }
                double rank = (p / 100d) * (sorted.Count - 1);
                int lowIdx = (int)Math.Floor(rank);
                int highIdx = (int)Math.Ceiling(rank);
                if (lowIdx == highIdx)
                {
                    return sorted[lowIdx].DurationMs;
                }
                double frac = rank - lowIdx;
                return sorted[lowIdx].DurationMs + (sorted[highIdx].DurationMs - sorted[lowIdx].DurationMs) * frac;
            }
            double p50 = Percentile(50);
            double p90 = Percentile(90);
            double p95 = Percentile(95);
            double p99 = Percentile(99);
            double max = sorted[^1].DurationMs;
            double min = sorted[0].DurationMs;
            double mean = entries.Average(e => e.DurationMs);
            List<TimingEntry> hotspots = entries.OrderByDescending(e => e.DurationMs).Take(10).ToList();
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
                List<double> arr = g.OrderBy(e => e.DurationMs).Select(e => e.DurationMs).ToList();
                if (arr.Count == 0)
                {
                    return 0;
                }
                if (arr.Count == 1)
                {
                    return arr[0];
                }
                double rank = (p / 100d) * (arr.Count - 1);
                int low = (int)Math.Floor(rank);
                int high = (int)Math.Ceiling(rank);
                if (low == high)
                {
                    return arr[low];
                }
                double frac = rank - low;
                return arr[low] + (arr[high] - arr[low]) * frac;
            }
            List<string> summaryLines = new List<string>
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
            string summaryPath = Path.Combine(baseDir, "test-timings-summary.txt");
            File.WriteAllLines(summaryPath, summaryLines);
            foreach (string l in summaryLines)
            {
                _ = _sink.OnMessage(new DiagnosticMessage("[TIMING-SUMMARY] " + l));
            }
        }
        catch (Exception ex)
        {
            _ = _sink.OnMessage(new DiagnosticMessage($"[TIMING-SUMMARY] Failed to generate summary: {ex.Message}"));
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
        AutoTimingMessageSink timingSink = new AutoTimingMessageSink(executionMessageSink);
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
                if (_sw.TryRemove(tf.Test.DisplayName, out Stopwatch? watch))
                {
                    watch.Stop();
                    try
                    {
                        MethodInfo methodInfo = tf.Test.TestCase.TestMethod.Method.ToRuntimeMethod();
                        Type classType = tf.Test.TestCase.TestMethod.TestClass.Class.ToRuntimeType();
                        // Consider both method-level and class-level attributes. If either is present, skip auto logging.
                        bool hasAttr = (methodInfo?.IsDefined(typeof(TestTimingAttribute), false) ?? false)
                                      || (classType?.IsDefined(typeof(TestTimingAttribute), false) ?? false);
                        if (!hasAttr)
                        {
                            string cls = tf.Test.TestCase.TestMethod.TestClass.Class.ToRuntimeType()?.FullName ?? tf.Test.TestCase.TestMethod.TestClass.Class.Name;
                            string method = tf.Test.TestCase.TestMethod.Method.Name;
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
