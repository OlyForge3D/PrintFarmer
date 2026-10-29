using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Wire contract for <c>DELETE /api/3d-models</c> (issue #3579): request body <c>{ ids }</c>, one typed
/// outcome per distinct ID, unchanged top-level counts, and source-specific authorization. Runs over
/// SQLite; it proves behaviour and request counts, not production provider performance.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Model3DBulkDeleteEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly FailingModelDeleteInterceptor _interceptor = new();
    private readonly BulkDeleteWebApplicationFactory _factory;
    private string _modelsPath = string.Empty;

    public Model3DBulkDeleteEndpointTests()
    {
        _factory = new BulkDeleteWebApplicationFactory(_interceptor);
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        _modelsPath = scope.ServiceProvider.GetRequiredService<IStoragePathService>().GetModelUploadDirectory();
        _ = Directory.CreateDirectory(_modelsPath);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(_modelsPath))
        {
            File.SetUnixFileMode(_modelsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _factory.Dispose();
    }

    [Fact]
    public async Task BulkDelete_MixedSelection_ReturnsCountsAndOneTypedOutcomePerDistinctId()
    {
        Model3D deletable = await SeedModelAsync(withThumbnail: true);
        Model3D invalid = await SeedModelAsync(isValid: false);
        Guid missing = Guid.NewGuid();
        using HttpClient client = await _factory.CreateAdminClientAsync();

        using HttpResponseMessage response = await SendDeleteAsync(
            client, new { ids = new[] { deletable.Id, missing, invalid.Id, deletable.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("deleted", "errors", "total", "results");
        root.GetProperty("deleted").GetInt32().Should().Be(1);
        root.GetProperty("errors").GetInt32().Should().Be(2);
        root.GetProperty("total").GetInt32().Should().Be(3, "duplicate IDs are collapsed");
        ReadResults(root).Should().Equal(
            (deletable.Id, "Deleted", (string?)null),
            (missing, "NotFound", null),
            (invalid.Id, "NotFound", null));
        root.GetProperty("results")[0].TryGetProperty("reason", out _).Should().BeFalse("null reasons are omitted");

        File.Exists(Path.Join(_modelsPath, deletable.FileName)).Should().BeFalse();
        File.Exists(Path.Join(_modelsPath, deletable.ThumbnailFileName!)).Should().BeFalse();
        File.Exists(Path.Join(_modelsPath, invalid.FileName)).Should().BeTrue();
        (await ModelExistsAsync(deletable.Id)).Should().BeFalse();
        (await ModelExistsAsync(invalid.Id)).Should().BeTrue("invalid models stay out of reach, matching single delete");
    }

    [Fact]
    public async Task BulkDelete_LegacyFileIdsBody_IsRejectedWithoutDeleting()
    {
        Model3D model = await SeedModelAsync();
        using HttpClient client = await _factory.CreateAdminClientAsync();

        using HttpResponseMessage response = await SendDeleteAsync(client, new { fileIds = new[] { model.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ModelExistsAsync(model.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_RecordRemovalFails_ReportsFilesRemovedRecordRetainedAndContinues()
    {
        Model3D failing = await SeedModelAsync();
        Model3D succeeding = await SeedModelAsync();
        _interceptor.FailDeleteFor = failing.Id;
        using HttpClient client = await _factory.CreateAdminClientAsync();

        using HttpResponseMessage response = await SendDeleteAsync(client, new { ids = new[] { failing.Id, succeeding.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("deleted").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("errors").GetInt32().Should().Be(1);
        ReadResults(body.RootElement).Should().Equal(
            (failing.Id, "Failed", (string?)"FilesRemovedRecordRetained"),
            (succeeding.Id, "Deleted", null));
        (await ModelExistsAsync(failing.Id)).Should().BeTrue("the failed record removal was not committed");
        File.Exists(Path.Join(_modelsPath, failing.FileName)).Should().BeFalse("model files are removed before the record");
        (await ModelExistsAsync(succeeding.Id)).Should().BeFalse("the failed item is not replayed by the next item's save");
    }

    [Fact]
    public async Task BulkDelete_StorageFailure_ReportsStorageFailedAndKeepsRecord()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // Directory write permissions are not enforced for these principals.
        }

        Model3D model = await SeedModelAsync();
        using HttpClient client = await _factory.CreateAdminClientAsync();
        File.SetUnixFileMode(_modelsPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using HttpResponseMessage response = await SendDeleteAsync(client, new { ids = new[] { model.Id } });

        File.SetUnixFileMode(_modelsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ReadResults(body.RootElement).Should().Equal((model.Id, "Failed", (string?)"StorageFailed"));
        (await ModelExistsAsync(model.Id)).Should().BeTrue();
        File.Exists(Path.Join(_modelsPath, model.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_CustomRoleWithSlicerEnginesAdmin_IsAuthorized()
    {
        Model3D model = await SeedModelAsync();
        using HttpClient client = await _factory.CreateOperatorClientAsync("slicer_engines", "admin");

        using HttpResponseMessage response = await SendDeleteAsync(client, new { ids = new[] { model.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ModelExistsAsync(model.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task BulkDelete_WithoutSlicerEnginesAdmin_IsForbiddenAndDeletesNothing()
    {
        Model3D model = await SeedModelAsync();
        using HttpClient client = await _factory.CreateOperatorClientAsync("gcode_library", "admin");

        using HttpResponseMessage response = await SendDeleteAsync(client, new { ids = new[] { model.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ModelExistsAsync(model.Id)).Should().BeTrue();
        File.Exists(Path.Join(_modelsPath, model.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_Anonymous_IsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await SendDeleteAsync(client, new { ids = new[] { Guid.NewGuid() } });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/3d-models")
        {
            Content = JsonContent.Create(body),
        });

    private static List<(Guid Id, string? Status, string? Reason)> ReadResults(JsonElement root) =>
        root.GetProperty("results").EnumerateArray()
            .Select(r => (
                r.GetProperty("id").GetGuid(),
                r.GetProperty("status").GetString(),
                r.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : null))
            .ToList();

    private async Task<bool> ModelExistsAsync(Guid id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return db.Models3D.Any(m => m.Id == id);
    }

    private async Task<Model3D> SeedModelAsync(bool withThumbnail = false, bool isValid = true)
    {
        Guid id = Guid.NewGuid();
        string fileName = $"{id:N}.stl";
        string? thumbnail = withThumbnail ? $"{id:N}_thumb.png" : null;
        await File.WriteAllTextAsync(Path.Join(_modelsPath, fileName), "solid test");
        if (thumbnail is not null)
        {
            await File.WriteAllBytesAsync(Path.Join(_modelsPath, thumbnail), [1, 2, 3]);
        }

        var model = new Model3D
        {
            Id = id,
            Name = fileName,
            FileName = fileName,
            FilePath = _modelsPath,
            ThumbnailFileName = thumbnail,
            FileSizeBytes = 10,
            FileHash = id.ToString("N"),
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            UploadedByUserId = Guid.NewGuid(),
            IsValid = isValid,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = db.Models3D.Add(model);
        _ = await db.SaveChangesAsync();
        return model;
    }

    private sealed class BulkDeleteWebApplicationFactory(FailingModelDeleteInterceptor interceptor) : CustomWebApplicationFactory
    {
        protected override DbCommandInterceptor? CommandInterceptor => interceptor;
    }

    /// <summary>Fails the SQL that removes one chosen model record, after its files were deleted.</summary>
    private sealed class FailingModelDeleteInterceptor : DbCommandInterceptor
    {
        public Guid? FailDeleteFor { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfTargeted(DbCommand command)
        {
            if (FailDeleteFor is Guid target &&
                command.CommandText.Contains("DELETE FROM \"Models3D\"", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid g ? g == target : string.Equals(p.Value?.ToString(), target.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Simulated model record delete failure");
            }
        }
    }
}
