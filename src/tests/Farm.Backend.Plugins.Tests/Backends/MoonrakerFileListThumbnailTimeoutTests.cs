// <copyright file="MoonrakerFileListThumbnailTimeoutTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Net;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Regression coverage for issue #2393: <c>MoonrakerClient.GetFileListWithMetadataAsync</c>
/// resolves each candidate file's thumbnail path with a bounded-concurrency second pass. If the
/// overall <see cref="BackendTimeoutSettings.CommandTimeout"/> elapses partway through that
/// second pass - e.g. a large (200+) file library where per-file thumbnail lookups can't all
/// complete in time - the file list itself must still be returned (with unresolved thumbnails
/// left null), not silently degraded to an empty list. An empty-list fallback here would
/// reintroduce the exact "silent empty list" symptom the issue exists to eliminate, just
/// triggered by "too many files to resolve thumbnails for in time" instead of "sequential
/// lookup took too long".
/// </summary>
public sealed class MoonrakerFileListThumbnailTimeoutTests
{
    private sealed class FileListWithHangingThumbnailsHandler : HttpMessageHandler
    {
        private readonly string[] _fileNames;

        public FileListWithHangingThumbnailsHandler(string[] fileNames)
        {
            _fileNames = fileNames;
        }

        public int ThumbnailRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            if (path.Contains("server/files/list", StringComparison.Ordinal))
            {
                string files = string.Join(
                    ',',
                    _fileNames.Select(name => $$"""{"path":"{{name}}","size":123,"modified":1700000000}"""));
                string json = $$"""{"result":[{{files}}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("server/files/thumbnails", StringComparison.Ordinal))
            {
                ThumbnailRequestCount++;

                // Simulate a printer/network slow enough that thumbnail lookups never finish
                // within the overall CommandTimeout budget - the only way this call returns is
                // via the caller's own linked cancellation token firing.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable: Task.Delay should have thrown on cancellation");
            }

            throw new InvalidOperationException($"Unexpected request to {request.RequestUri}");
        }
    }

    [Fact]
    public async Task GetFileListAsync_ThumbnailLookupTimesOutMidResolution_ReturnsFileListWithNullThumbnailsNotEmptyList()
    {
        string[] fileNames = ["a.gcode", "b.gcode", "c.gcode"];
        var handler = new FileListWithHangingThumbnailsHandler(fileNames);
        using HttpClient http = new(handler);
        BackendTimeoutSettings timeouts = new() { CommandTimeoutSeconds = 1 };
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, timeouts);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<PrinterFileInfo> files = await ((ISupportsFileList)client).GetFileListAsync(
            "http://moonraker/", credential: null, ct: CancellationToken.None);
        stopwatch.Stop();

        files.Should().HaveCount(fileNames.Length,
            "the file list must still be returned even though none of the thumbnail lookups " +
            "completed before the overall CommandTimeout fired");
        files.Select(f => f.Name).Should().BeEquivalentTo(fileNames, o => o.WithStrictOrdering());
        files.Should().OnlyContain(f => f.ThumbnailPath == null,
            "thumbnails that never resolved before the timeout must be left null, not fabricated");

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(timeouts.CommandTimeoutSeconds + 5),
            "the overall call must still be bounded by CommandTimeout, not hang indefinitely " +
            "waiting on the slow thumbnail lookups");
        handler.ThumbnailRequestCount.Should().BeGreaterThan(0,
            "the bounded-concurrency thumbnail pass must have actually started before timing out");
    }

    [Fact]
    public async Task GetFileListAsync_CallerCancelsWhileThumbnailsResolving_PropagatesCancellationInsteadOfReturningPartialList()
    {
        // Real caller cancellation (not the internal CommandTimeout) must still propagate rather
        // than being swallowed into a "partial success" result - only the internal timeout path
        // should degrade gracefully.
        string[] fileNames = ["a.gcode"];
        var handler = new FileListWithHangingThumbnailsHandler(fileNames);
        using HttpClient http = new(handler);
        BackendTimeoutSettings timeouts = new() { CommandTimeoutSeconds = 30 };
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, timeouts);

        using CancellationTokenSource callerCts = new();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(200));

        Func<Task> act = async () => await ((ISupportsFileList)client).GetFileListAsync(
            "http://moonraker/", credential: null, ct: callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller-initiated cancellation must propagate, not be reinterpreted as an internal timeout");
    }
}
