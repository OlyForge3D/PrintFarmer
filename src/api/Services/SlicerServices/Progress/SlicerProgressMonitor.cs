using Farm.Web.Api.Services.SlicerServices.Process;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices.Progress;

public static class SlicerProgressMonitor
{
    public static async Task MonitorAsync(Guid jobId, IProcessHandle processHandle, ISlicerProgressNotifier notifier, IProgressParser parser, ILogger? logger, CancellationToken ct, Func<Guid, CancellationToken, Task>? onParserCompleted = null, Func<Guid, string, CancellationToken, Task>? onParserFailure = null)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(notifier);

        try
        {
            var start = DateTime.UtcNow;
            var stdout = processHandle.StandardOutput; // do not dispose injected stream
            while (!processHandle.HasExited && !ct.IsCancellationRequested)
            {
                if (!stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        ProgressUpdate? parsed = null;
                        try
                        {
                            parsed = parser.Parse(line);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Progress parser threw while handling a line for job {JobId}", jobId);
                        }

                        if (parsed != null)
                        {
                            var pct = (int)Math.Max(0, Math.Min(100, Math.Round(parsed.Percentage)));
                            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = pct, Status = SlicingJobStatus.Slicing, CurrentStep = parsed.Message }, ct);
                            if (parsed.State == SlicerProgressState.Completed)
                            {
                                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = 100, Status = SlicingJobStatus.Slicing, CurrentStep = parsed.Message }, ct);
                                try
                                {
                                    if (onParserCompleted != null)
                                    {
                                        await onParserCompleted(jobId, ct);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogDebug(ex, "onParserCompleted callback threw for job {JobId}", jobId);
                                }
                            }
                            else if (parsed.State == SlicerProgressState.Failed)
                            {
                                // Notify the worker/controller that parser reported a failure so higher-level logic can mark job failed.
                                try
                                {
                                    if (onParserFailure != null)
                                    {
                                        await onParserFailure(jobId, parsed.Message ?? "Parser reported failure", ct);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogDebug(ex, "onParserFailure callback threw for job {JobId}", jobId);
                                }

                                // Ask the underlying process to terminate as an early stop
                                try
                                {
                                    processHandle.Kill();
                                }
                                catch { }
                            }
                            continue;
                        }
                    }
                }

                var elapsed = DateTime.UtcNow - start;
                var estimated = Math.Min(95, 10 + (int)Math.Min(85, elapsed.TotalSeconds / 1.5));
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = estimated, Status = SlicingJobStatus.Slicing, CurrentStep = "Slicing in progress..." }, ct);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error monitoring slicer process for job {JobId}", jobId);
        }
    }
}
