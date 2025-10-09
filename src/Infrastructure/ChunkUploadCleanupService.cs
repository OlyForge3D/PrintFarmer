using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Farm.Infrastructure;

public class ChunkUploadCleanupService : BackgroundService
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _webRootPath;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _ttl = TimeSpan.FromHours(2);

    public ChunkUploadCleanupService(IUnifiedLoggingService logger, string webRootPath)
    {
        _logger = logger;
        _webRootPath = webRootPath;
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
                _logger.LogDebug($"Chunk cleanup sweep failed: {ex.Message}");
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunCleanup()
    {
        string root = _webRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string libraryRoot = Path.Combine(root, "gcode-library");
        if (!Directory.Exists(libraryRoot))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        int removed = 0;
        foreach (string meta in Directory.EnumerateFiles(libraryRoot, "*.part.meta.json", SearchOption.AllDirectories))
        {
            try
            {
                FileInfo fi = new(meta);
                if (now - fi.LastWriteTimeUtc < _ttl)
                {
                    continue;
                }
                // Paired .part file
                string part = meta.Substring(0, meta.Length - ".meta.json".Length);
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
            _logger.LogInformation($"Chunk cleanup removed {removed} stale uploads");
        }
    }
}
