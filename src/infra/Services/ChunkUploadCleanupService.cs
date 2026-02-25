using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure;

public class ChunkUploadCleanupService(ILogger<ChunkUploadCleanupService> logger, string webRootPath) : BackgroundService
{
    private readonly ILogger<ChunkUploadCleanupService> _logger = logger;
    private readonly string _webRootPath = webRootPath;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _ttl = TimeSpan.FromHours(2);

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
                _logger.LogDebug("Chunk cleanup sweep failed: {Message}", ex.Message);
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
            catch
            { /* ignore */
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Chunk cleanup removed {Removed} stale uploads", removed);
        }
    }
}
