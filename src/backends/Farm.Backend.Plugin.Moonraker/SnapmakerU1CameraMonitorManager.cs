using System.Collections.Concurrent;
using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.Moonraker;

public sealed class SnapmakerU1CameraMonitorManager(
    IMoonrakerJsonRpcClient jsonRpcClient,
    TimeSpan? startRateLimit = null,
    TimeSpan? idleStopDelay = null,
    TimeSpan? stopRetryBackoff = null,
    int maxStopRetries = 3) : ISnapmakerU1CameraMonitorManager
{
    private readonly IMoonrakerJsonRpcClient _jsonRpcClient = jsonRpcClient ?? throw new ArgumentNullException(nameof(jsonRpcClient));
    private readonly TimeSpan _startRateLimit = startRateLimit ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _idleStopDelay = idleStopDelay ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _stopRetryBackoff = stopRetryBackoff ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _stopMonitorTimeout = TimeSpan.FromSeconds(3);
    private readonly int _maxStopRetries = Math.Max(0, maxStopRetries);
    private readonly ConcurrentDictionary<string, MonitorState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> EnsureMonitorStartedAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct)
    {
        Uri baseUri = new(baseUrl);
        string key = GetMonitorKey(baseUri);
        MonitorState state = _states.GetOrAdd(key, _ => new MonitorState());

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            state.LastAccessUtc = now;
            state.Credential = credential;

            bool shouldStart = !state.IsRunning &&
                               (state.LastStartUtc is null ||
                                now - state.LastStartUtc.Value >= _startRateLimit);
            if (shouldStart)
            {
                // Hardware-inferred for stock U1 (#685): start_monitor wakes the monitor that backs monitor.jpg.
                await _jsonRpcClient.SendMethodAsync(baseUri, "camera.start_monitor", credential, ct).ConfigureAwait(false);
                state.LastStartUtc = now;
                state.IsRunning = true;
            }

            state.StopRetryCount = 0;
            ScheduleIdleStop(key, baseUri, state, credential);
            return true;
        }
        catch (MoonrakerJsonRpcException ex) when (ex.RequestSent)
        {
            state.LastStartUtc ??= DateTimeOffset.UtcNow;
            state.IsRunning = true;
            ScheduleIdleStop(key, baseUri, state, credential);
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void ScheduleIdleStop(string key, Uri baseUri, MonitorState state, PrinterCredential? credential)
    {
        state.IdleStopCts?.Cancel();
        CancellationTokenSource cts = new();
        state.IdleStopCts = cts;

        _ = StopAfterIdleAsync(key, baseUri, state, credential, cts.Token);
    }

    private async Task StopAfterIdleAsync(string key, Uri baseUri, MonitorState state, PrinterCredential? credential, CancellationToken ct)
    {
        try
        {
            await StopAfterDelayAsync(key, baseUri, state, credential, _idleStopDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task StopAfterDelayAsync(string key, Uri baseUri, MonitorState state, PrinterCredential? credential, TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!state.IsRunning)
            {
                return;
            }

            try
            {
                using CancellationTokenSource stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stopCts.CancelAfter(_stopMonitorTimeout);

                // Hardware-inferred for stock U1 (#685): stop_monitor releases the camera monitor.
                await _jsonRpcClient.SendMethodAsync(baseUri, "camera.stop_monitor", credential ?? state.Credential, stopCts.Token).ConfigureAwait(false);
                state.IsRunning = false;
                state.StopRetryCount = 0;
                _states.TryRemove(key, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch
            {
                RescheduleStopRetry(key, baseUri, state, credential);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void RescheduleStopRetry(string key, Uri baseUri, MonitorState state, PrinterCredential? credential)
    {
        if (state.StopRetryCount >= _maxStopRetries)
        {
            return;
        }

        state.StopRetryCount++;
        CancellationTokenSource cts = new();
        state.IdleStopCts = cts;
        _ = StopAfterDelayAsync(key, baseUri, state, credential, _stopRetryBackoff, cts.Token);
    }

    private static string GetMonitorKey(Uri baseUri)
    {
        return new UriBuilder(baseUri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.ToString().TrimEnd('/');
    }

    private sealed class MonitorState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset? LastStartUtc { get; set; }

        public DateTimeOffset LastAccessUtc { get; set; }

        public bool IsRunning { get; set; }

        public PrinterCredential? Credential { get; set; }

        public CancellationTokenSource? IdleStopCts { get; set; }

        public int StopRetryCount { get; set; }
    }
}
