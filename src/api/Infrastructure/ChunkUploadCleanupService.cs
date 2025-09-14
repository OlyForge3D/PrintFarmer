namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Periodically scans the gcode-library for stale chunk upload temp files (.part and .meta.json) and removes
/// those older than the configured TTL (default 2 hours) that are not currently active.
/// </summary>
public class ChunkUploadCleanupService : BackgroundService
{
    private readonly ILogger<ChunkUploadCleanupService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _ttl = TimeSpan.FromHours(2);

    public ChunkUploadCleanupService(ILogger<ChunkUploadCleanupService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunCleanup();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chunk cleanup sweep failed");
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunCleanup()
    {
        var root = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var libraryRoot = Path.Combine(root, "gcode-library");
        if (!Directory.Exists(libraryRoot))
        {
            return;
        }

        var now = DateTime.UtcNow;
        int removed = 0;
        foreach (var meta in Directory.EnumerateFiles(libraryRoot, "*.part.meta.json", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(meta);
                if (now - fi.LastWriteTimeUtc < _ttl)
                {
                    continue;
                }
                // Paired .part file
                var part = meta[..^(".meta.json".Length)];
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
                File.Delete(meta);
                removed++;
            }
            catch { /* ignore */ }
        }
        if (removed > 0)
        {
            _logger.LogInformation("Chunk cleanup removed {Count} stale uploads", removed);
        }
    }
}
