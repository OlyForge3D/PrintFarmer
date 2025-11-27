using Farm.Web.Api.Services.SlicerServices.Process;
using Farm.Web.Api.Services.SlicerServices.Progress;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests.SlicerServices;

public class SlicerProgressMonitorTests
{
    private class TestNotifier : ISlicerProgressNotifier
    {
        public List<SlicingProgressUpdate> Updates { get; } = new List<SlicingProgressUpdate>();
        public Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default)
        {
            lock (Updates)
            { Updates.Add(update); }
            return Task.CompletedTask;
        }
        public Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class TestProcessHandle : IProcessHandle
    {
        private readonly System.IO.MemoryStream _ms;
        private readonly System.IO.StreamReader _sr;
        private readonly int _exitDelayMs;
        private bool _exited;
        public bool Killed { get; private set; }

        public TestProcessHandle(IEnumerable<string> lines, int exitDelayMs = 100)
        {
            _exitDelayMs = exitDelayMs;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + '\n');
            _ms = new System.IO.MemoryStream(bytes);
            _sr = new System.IO.StreamReader(_ms);
            _exited = false;
        }

        public System.IO.StreamReader StandardOutput => _sr;
        public System.IO.StreamReader StandardError => new System.IO.StreamReader(new System.IO.MemoryStream());
        public bool HasExited => _exited;
        public int ExitCode { get; private set; } = 0;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_exitDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // propagate
                throw;
            }
            finally
            {
                _exited = true;
            }
            return ExitCode;
        }

        public void Kill()
        {
            Killed = true;
            _exited = true;
            ExitCode = -1;
        }
    }

    [Fact]
    public async Task MonitorAsync_PrusaParser_EmitsProgressAndCompletion()
    {
        string[] lines = new[] { "Progress: 10%", "Layer 50/100", "Exported gcode to out.gcode" };
        TestProcessHandle handle = new TestProcessHandle(lines, exitDelayMs: 200);
        TestNotifier notifier = new TestNotifier();
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await SlicerProgressMonitor.MonitorAsync(Guid.NewGuid(), handle, notifier, new PrusaProgressParser(), null, cts.Token);

        notifier.Updates.Should().NotBeEmpty();
        notifier.Updates.Any(u => u.Progress == 10).Should().BeTrue();
        notifier.Updates.Any(u => u.Progress >= 49 && u.Progress <= 51).Should().BeTrue();
        notifier.Updates.Any(u => u.Progress == 100).Should().BeTrue();
    }

    [Fact]
    public async Task MonitorAsync_OrcaParser_ParsesPercentLines()
    {
        string[] lines = new[] { "[info] Exporting: 30%", "Saving G-code...", "Saving G-code: 100%" };
        TestProcessHandle handle = new TestProcessHandle(lines, exitDelayMs: 200);
        TestNotifier notifier = new TestNotifier();
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await SlicerProgressMonitor.MonitorAsync(Guid.NewGuid(), handle, notifier, new OrcaProgressParser(), null, cts.Token);

        notifier.Updates.Should().NotBeEmpty();
        notifier.Updates.Any(u => u.Progress == 30).Should().BeTrue();
        notifier.Updates.Any(u => u.Progress == 100).Should().BeTrue();
    }

    [Fact]
    public async Task MonitorAsync_ParserFailure_InvokesCallbackAndKillsProcess()
    {
        string[] lines = new[] { "ERROR: export failed due to permission" };
        TestProcessHandle handle = new TestProcessHandle(lines, exitDelayMs: 50);
        TestNotifier notifier = new TestNotifier();
        bool called = false;
        Func<Guid, string, CancellationToken, Task> onFailure = (jobId, msg, ct) =>
        {
            called = true;
            return Task.CompletedTask;
        };

        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await SlicerProgressMonitor.MonitorAsync(Guid.NewGuid(), handle, notifier, new PrusaProgressParser(), null, cts.Token, null, onFailure);

        called.Should().BeTrue();
        handle.Killed.Should().BeTrue();
    }
}
