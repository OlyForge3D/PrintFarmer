using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Farm.Testing.Shared;

/// <summary>
/// Shared base for the per-module <c>CustomWebApplicationFactory</c> test fixtures. Factors out
/// only the boilerplate that was byte-for-byte identical across <c>Farm.Web.Api.Tests</c> and
/// <c>Farm.Slicer.Module.Tests</c>: the named in-memory SQLite keep-alive connection, the
/// isolated temp-directory pair for model/gcode storage, and their cleanup on dispose.
/// </summary>
/// <remarks>
/// Deliberately shallow (see issue #2032): DbContext registration/wiring, config overrides,
/// logging-provider setup, and any other <c>ConfigureWebHost</c> logic stays local to each
/// project's own <c>CustomWebApplicationFactory</c> — those bodies differ enough (and are tuned
/// closely enough to each host's own concurrency/registration hazards) that generalizing them
/// here would add risk without meaningfully reducing duplication.
/// </remarks>
public abstract class HostFixture<TEntryPoint> : WebApplicationFactory<TEntryPoint>, IAsyncDisposable
    where TEntryPoint : class
{
    private readonly string _modelStoragePath;
    private readonly string _gcodeStoragePath;
    private readonly SqliteConnection _keepAliveConnection;

    /// <summary>
    /// Connection string of this fixture's isolated in-memory SQLite database.
    /// </summary>
    protected string ConnectionString { get; }

    /// <summary>
    /// Isolated temp directory for uploaded model files.
    /// </summary>
    protected string ModelStoragePath => _modelStoragePath;

    /// <summary>
    /// Isolated temp directory for generated gcode files.
    /// </summary>
    protected string GcodeStoragePath => _gcodeStoragePath;

    /// <summary>
    /// Creates the named shared in-memory SQLite database and isolated temp storage
    /// directories for this fixture instance.
    /// </summary>
    /// <param name="databaseName">
    /// Unique per-instance database name (subclasses are responsible for including their own
    /// prefix/counter, e.g. <c>$"farm_test_{id}"</c>, so names never collide across fixtures).
    /// </param>
    /// <param name="extraConnectionStringOptions">
    /// Additional SQLite connection-string options appended verbatim (e.g.
    /// <c>"Default Timeout=30;Pooling=False"</c>). Omit for fixtures that don't need them.
    /// </param>
    protected HostFixture(string databaseName, string? extraConnectionStringOptions = null)
    {
        string extras = string.IsNullOrEmpty(extraConnectionStringOptions) ? string.Empty : $";{extraConnectionStringOptions}";
        _keepAliveConnection = new SqliteConnection($"Data Source=file:{databaseName}?mode=memory&cache=shared{extras}");
        _keepAliveConnection.Open();
        ConnectionString = _keepAliveConnection.ConnectionString;

        string tempDir = Path.Join(Path.GetTempPath(), $"{databaseName}_{Guid.NewGuid()}");
        _modelStoragePath = Path.Join(tempDir, "models");
        _gcodeStoragePath = Path.Join(tempDir, "gcode");

        Directory.CreateDirectory(_modelStoragePath);
        Directory.CreateDirectory(_gcodeStoragePath);
    }

    /// <summary>
    /// Cleans up the isolated temp storage directory and closes the keep-alive connection.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            string? tempDir = Path.GetDirectoryName(_modelStoragePath);
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors (files might be locked)
        }

        try
        {
            _keepAliveConnection.Close();
            _keepAliveConnection.Dispose();
        }
        catch
        {
            // Ignore connection cleanup errors
        }

        await base.DisposeAsync();
    }
}
