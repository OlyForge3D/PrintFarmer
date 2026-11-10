using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services;

public sealed class SpoolmanFilamentDeletionTests
{
    [Fact]
    public async Task PreviewFilamentDeleteAsync_ArchivedAndActiveSpools_ReturnsExactReferencesWithoutWrites()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.AddRange([(1, false), (2, true), (3, true)]);
        using var http = new HttpClient(handler);

        SpoolmanFilamentDeletePreview preview = await Service(http).PreviewFilamentDeleteAsync(10, CancellationToken.None);

        Assert.Equal(1, preview.ActiveSpoolCount);
        Assert.Equal([2, 3], preview.ArchivedSpoolIds);
        Assert.Empty(handler.Deletes);
        Assert.All(handler.Reads, uri => Assert.Equal("?filament.id=10&allow_archived=true", uri.Query));
    }

    [Fact]
    public async Task DeleteFilamentFromSpoolmanAsync_NoSpools_DeletesOnlyFilament()
    {
        using var handler = new SpoolmanHandler();
        using var http = new HttpClient(handler);

        await Service(http).DeleteFilamentFromSpoolmanAsync(10, CancellationToken.None);

        Assert.Equal(["/api/v1/filament/10"], handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentFromSpoolmanAsync_ActiveSpools_BlocksWithCountAndDeletesNothing()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.AddRange([(1, false), (2, false), (3, true)]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentFromSpoolmanAsync(10, CancellationToken.None));

        Assert.Contains("2 active spool(s)", error.Message);
        Assert.Empty(handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentFromSpoolmanAsync_ArchivedSpoolsWithoutConsent_BlocksWithCount()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.Add((1, true));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentFromSpoolmanAsync(10, CancellationToken.None));

        Assert.Contains("1 archived spool(s)", error.Message);
        Assert.Contains("confirm", error.Message);
        Assert.Empty(handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_ExactConsent_DeletesSpoolsThenFilament()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.AddRange([(1, true), (2, true)]);
        using var http = new HttpClient(handler);

        await Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [2, 1], CancellationToken.None);

        Assert.Equal(["/api/v1/spool/2", "/api/v1/spool/1", "/api/v1/filament/10"], handler.Deletes);
        Assert.Equal(6, handler.Reads.Count);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_DelayedDeletionVisibility_WaitsWithoutRepeatingDeletes()
    {
        using var handler = new SpoolmanHandler { StaleReadsAfterDelete = 2 };
        handler.Spools.AddRange([(1, true), (2, true)]);
        using var http = new HttpClient(handler);

        await Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None);

        Assert.Equal(["/api/v1/spool/1", "/api/v1/spool/2", "/api/v1/filament/10"], handler.Deletes);
        Assert.Equal(10, handler.Reads.Count);
        Assert.Empty(handler.Spools);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_DeletionNeverVisible_StopsWithPrecisePartialOutcome()
    {
        using var handler = new SpoolmanHandler { StaleReadsAfterDelete = int.MaxValue };
        handler.Spools.AddRange([(1, true), (2, true)]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None));

        Assert.Contains("within 5 seconds", error.Message);
        Assert.Contains("Confirmed deleted archived spool IDs: 1.", error.Message);
        Assert.Equal(["/api/v1/spool/1"], handler.Deletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_NewSpoolWhileWaiting_StopsImmediately(bool archived)
    {
        using var handler = new SpoolmanHandler { StaleReadsAfterDelete = 2 };
        handler.Spools.AddRange([(1, true), (2, true)]);
        handler.BeforeRead = read =>
        {
            if (read == 3)
            {
                handler.Spools.Add((3, archived));
            }
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None));

        Assert.Contains(archived ? "spool list changed" : "active spool", error.Message);
        Assert.Contains("Confirmed deleted archived spool IDs: 1.", error.Message);
        Assert.Equal(3, handler.Reads.Count);
        Assert.Equal(["/api/v1/spool/1"], handler.Deletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_RemainingSpoolChangesWhileWaiting_Stops(bool unarchived)
    {
        using var handler = new SpoolmanHandler { StaleReadsAfterDelete = 3 };
        handler.Spools.AddRange([(1, true), (2, true)]);
        handler.BeforeRead = read =>
        {
            if (read == 4)
            {
                if (unarchived)
                {
                    handler.Spools[1] = (2, false);
                }
                else
                {
                    handler.Spools.RemoveAll(s => s.Id == 2);
                }
            }
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None));

        Assert.Contains(unarchived ? "active spool" : "spool list changed", error.Message);
        Assert.Equal(4, handler.Reads.Count);
        Assert.Equal(["/api/v1/spool/1"], handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_CancelledVisibilityWait_ReportsPartialOutcomeWithoutContinuing()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new SpoolmanHandler { StaleReadsAfterDelete = 3 };
        handler.Spools.AddRange([(1, true), (2, true)]);
        handler.BeforeRead = read =>
        {
            if (read == 3)
            {
                cancellation.Cancel();
            }
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], cancellation.Token));

        Assert.Contains("Confirmed deleted archived spool IDs: 1.", error.Message);
        Assert.DoesNotContain("within 5 seconds", error.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(["/api/v1/spool/1"], handler.Deletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_ChangedSetOrActiveSpool_DeletesNothing(bool active)
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.AddRange([(1, true), (2, !active)]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1], CancellationToken.None));

        Assert.Contains(active ? "active spool" : "changed", error.Message);
        Assert.Empty(handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_SpoolUnarchivedAfterFirstCheck_DeletesNothing()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.Add((1, true));
        handler.BeforeRead = read =>
        {
            if (read == 2)
            {
                handler.Spools[0] = (1, false);
            }
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1], CancellationToken.None));

        Assert.Contains("active spool", error.Message);
        Assert.Empty(handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_SecondSpoolFails_ReportsConfirmedPartialDeletion()
    {
        using var handler = new SpoolmanHandler { FailingDelete = "/api/v1/spool/2" };
        handler.Spools.AddRange([(1, true), (2, true)]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None));

        Assert.Contains("Confirmed deleted archived spool IDs: 1.", error.Message);
        Assert.Contains("Spool locked by another process", error.Message);
        Assert.DoesNotContain("/api/v1/filament/10", handler.Deletes);
        Assert.Equal([(2, true)], handler.Spools);
    }

    [Fact]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_NewActiveSpoolDuringDeletion_StopsAndReportsPartialDeletion()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.AddRange([(1, true), (2, true)]);
        handler.BeforeRead = read =>
        {
            if (read == 3)
            {
                handler.Spools.Add((3, false));
            }
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).DeleteFilamentWithArchivedSpoolsAsync(10, [1, 2], CancellationToken.None));

        Assert.Contains("Confirmed deleted archived spool IDs: 1.", error.Message);
        Assert.Contains("1 active spool(s)", error.Message);
        Assert.Equal(["/api/v1/spool/1"], handler.Deletes);
    }

    [Theory]
    [InlineData("[1,1]")]
    [InlineData("[0]")]
    [InlineData("[-1]")]
    public async Task DeleteFilamentWithArchivedSpoolsAsync_InvalidConsent_RejectsBeforeReading(string json)
    {
        using var handler = new SpoolmanHandler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => Service(http).DeleteFilamentWithArchivedSpoolsAsync(
            10, JsonSerializer.Deserialize<int[]>(json)!, CancellationToken.None));

        Assert.Empty(handler.Reads);
        Assert.Empty(handler.Deletes);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("[]", "1")]
    [InlineData("[{\"id\":1,\"filament\":{\"id\":11},\"archived\":true}]", null)]
    public async Task PreviewFilamentDeleteAsync_UnverifiableResponse_FailsClosed(string body, string? total)
    {
        using var handler = new SpoolmanHandler { ReadBody = body, TotalCount = total };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(http).PreviewFilamentDeleteAsync(10, CancellationToken.None));

        Assert.Empty(handler.Deletes);
    }

    [Fact]
    public async Task DeleteFilamentFromSpoolmanAsync_UpstreamForbidden_PreservesActionableDetail()
    {
        using var handler = new SpoolmanHandler { FailingDelete = "/api/v1/filament/10" };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SpoolmanApiException>(
            () => Service(http).DeleteFilamentFromSpoolmanAsync(10, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("Spool locked by another process", error.Message);
        Assert.DoesNotContain("Response status code does not indicate success", error.Message);
    }

    [Fact]
    public async Task BulkDeleteFilamentsAsync_ArchivedSpools_ReturnsReasonWithoutCascading()
    {
        using var handler = new SpoolmanHandler();
        handler.Spools.Add((1, true));
        using var http = new HttpClient(handler);

        SpoolmanBulkUpdateResult result = await Service(http).BulkDeleteFilamentsAsync([10], CancellationToken.None);

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("Filament 10: This filament has 1 archived spool(s)", Assert.Single(result.Errors));
        Assert.Empty(handler.Deletes);
    }

    private static SpoolmanService Service(HttpClient http)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        return new SpoolmanService(http, settings.Object, NullLogger<SpoolmanService>.Instance,
            Farm.Testing.Shared.AppDbTestHelpers.PermissiveEgressGuard());
    }

    private sealed class SpoolmanHandler : HttpMessageHandler
    {
        public List<(int Id, bool Archived)> Spools { get; } = [];
        public List<Uri> Reads { get; } = [];
        public List<string> Deletes { get; } = [];
        public Action<int>? BeforeRead { get; set; }
        public string? FailingDelete { get; init; }
        public string? ReadBody { get; init; }
        public string? TotalCount { get; init; }
        public int StaleReadsAfterDelete { get; init; }
        private readonly Dictionary<int, int> _pendingDeletes = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get)
            {
                Reads.Add(uri);
                BeforeRead?.Invoke(Reads.Count);
                foreach (int id in _pendingDeletes.Keys.ToArray())
                {
                    if (_pendingDeletes[id] == 0)
                    {
                        Spools.RemoveAll(s => s.Id == id);
                        _pendingDeletes.Remove(id);
                    }
                    else
                    {
                        _pendingDeletes[id]--;
                    }
                }
                string json = ReadBody ?? JsonSerializer.Serialize(Spools.Select(s => new
                {
                    id = s.Id,
                    archived = s.Archived,
                    filament = new { id = 10 },
                }));
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (TotalCount is not null)
                {
                    response.Headers.Add("X-Total-Count", TotalCount);
                }

                return Task.FromResult(response);
            }

            Assert.Equal(HttpMethod.Delete, request.Method);
            Deletes.Add(uri.AbsolutePath);
            if (uri.AbsolutePath == FailingDelete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"Spool locked by another process\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (uri.AbsolutePath.StartsWith("/api/v1/spool/", StringComparison.Ordinal))
            {
                int id = int.Parse(uri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
                if (StaleReadsAfterDelete > 0)
                {
                    _pendingDeletes[id] = StaleReadsAfterDelete;
                }
                else
                {
                    Spools.RemoveAll(s => s.Id == id);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
