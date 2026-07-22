using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>
/// Collects lightweight host, storage, service, and database metrics for the system status page.
/// </summary>
public class SystemInfoService(
    AppDbContext db,
    IStoragePathService storagePathService,
    IBackgroundServiceMonitor backgroundServiceMonitor,
    IMemoryCache cache,
    ILogger<SystemInfoService> logger) : ISystemInfoService
{
    private static readonly TimeSpan CpuSampleDuration = TimeSpan.FromMilliseconds(150);
    private const string CacheKey = "SystemInfo:Snapshot";

    private readonly AppDbContext _db = db;
    private readonly IStoragePathService _storagePathService = storagePathService;
    private readonly IBackgroundServiceMonitor _backgroundServiceMonitor = backgroundServiceMonitor;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<SystemInfoService> _logger = logger;

    /// <summary>
    /// Returns the current system information snapshot, served from a 10-second cache to avoid
    /// redundant CPU sampling and directory scans when multiple tabs poll simultaneously.
    /// </summary>
    public async Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10);
            return await CollectSystemInfoAsync(cancellationToken);
        }) ?? await CollectSystemInfoAsync(cancellationToken);
    }

    /// <summary>
    /// Collects the current system information snapshot.
    /// </summary>
    private async Task<SystemInfoDto> CollectSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        string appVersion = GetApplicationVersion();
        string storageDirectory = ResolveStorageDirectory();

        Task<double> cpuTask = GetCpuUsagePercentAsync(cancellationToken);
        Task<(long UsedBytes, long TotalBytes)> memoryTask = Task.FromResult(GetMemorySnapshot());
        Task<long> archiveBytesTask = Task.FromResult(GetDirectorySizeSafe(storageDirectory));

        await Task.WhenAll(cpuTask, memoryTask, archiveBytesTask);

        long archiveBytes = await archiveBytesTask;
        (long memoryUsedBytes, long memoryTotalBytes) = await memoryTask;
        long databaseBytes = await GetDatabaseSizeBytesAsync(cancellationToken);
        string databaseVersion = await GetDatabaseVersionAsync(cancellationToken);
        int printerCount = await _db.Printers.CountAsync(cancellationToken);
        int archiveCount = await _db.GcodeFiles.CountAsync(cancellationToken);
        (long diskUsedBytes, long diskTotalBytes) = GetDiskSnapshot(storageDirectory);

        return new SystemInfoDto
        {
            App = new SystemAppInfoDto
            {
                Version = appVersion,
                Uptime = FormatUptime(GetProcessUptime()),
                Hostname = GetHostname(),
            },
            Cpu = new SystemCpuInfoDto
            {
                Cores = Environment.ProcessorCount,
                UsagePercent = await cpuTask,
            },
            Memory = new SystemMemoryInfoDto
            {
                UsedBytes = memoryUsedBytes,
                TotalBytes = memoryTotalBytes,
            },
            Disk = new SystemDiskInfoDto
            {
                UsedBytes = diskUsedBytes,
                TotalBytes = diskTotalBytes,
                ArchiveBytes = archiveBytes,
                DatabaseBytes = databaseBytes,
            },
            Services = GetServices(appVersion),
            Database = new SystemDatabaseInfoDto
            {
                Engine = NormalizeDatabaseEngine(_db.Database.ProviderName),
                Version = databaseVersion,
                PrinterCount = printerCount,
                ArchiveCount = archiveCount,
            },
        };
    }

    // Reuses the existing informational version logic so the dashboard matches /api/system/version.
    private static string GetApplicationVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(SystemInfoService).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    // Uses the gcode storage root because archiveBytes is derived from that tree.
    private string ResolveStorageDirectory()
    {
        string storageDirectory = _storagePathService.GetGcodeStorageDirectory();
        return string.IsNullOrWhiteSpace(storageDirectory)
            ? AppContext.BaseDirectory
            : storageDirectory;
    }

    // Hostname is a display value, so fall back to Environment.MachineName if DNS lookup fails.
    private static string GetHostname()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    // Process uptime is safer than host uptime because it behaves consistently in containers and tests.
    private static TimeSpan GetProcessUptime()
    {
        using Process process = Process.GetCurrentProcess();
        return DateTime.UtcNow - process.StartTime.ToUniversalTime();
    }

    // Returns a compact operator-facing duration string instead of raw ticks or ISO8601.
    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
        }

        if (uptime.TotalMinutes >= 1)
        {
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }

        return $"{Math.Max(0, uptime.Seconds)}s";
    }

    // Samples host CPU usage on Linux (/proc/stat) and Windows (GetSystemTimes).
    private static async Task<double> GetCpuUsagePercentAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!TryReadLinuxCpuSample(out LinuxCpuSample first))
            {
                return 0;
            }

            await Task.Delay(CpuSampleDuration, cancellationToken);

            if (!TryReadLinuxCpuSample(out LinuxCpuSample second))
            {
                return 0;
            }

            return CalculateCpuUsage(first.TotalTicks, first.IdleTicks, second.TotalTicks, second.IdleTicks);
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryReadWindowsCpuSample(out WindowsCpuSample first))
            {
                return 0;
            }

            await Task.Delay(CpuSampleDuration, cancellationToken);

            if (!TryReadWindowsCpuSample(out WindowsCpuSample second))
            {
                return 0;
            }

            return CalculateCpuUsage(first.TotalTicks, first.IdleTicks, second.TotalTicks, second.IdleTicks);
        }

        return 0;
    }

    // Host memory comes from OS-specific sources, with process RSS as the last-resort fallback.
    private static (long UsedBytes, long TotalBytes) GetMemorySnapshot()
    {
        if (OperatingSystem.IsLinux() && TryReadLinuxMemorySnapshot(out long usedBytes, out long totalBytes))
        {
            return (usedBytes, totalBytes);
        }

        if (OperatingSystem.IsWindows() && TryReadWindowsMemorySnapshot(out usedBytes, out totalBytes))
        {
            return (usedBytes, totalBytes);
        }

        using Process process = Process.GetCurrentProcess();
        return (process.WorkingSet64, 0);
    }

    // Disk metrics are scoped to the drive that backs PrintFarmer storage so the numbers match archiveBytes.
    private static (long UsedBytes, long TotalBytes) GetDiskSnapshot(string storageDirectory)
    {
        try
        {
            string fullPath = Path.GetFullPath(storageDirectory);
            string? rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return (0, 0);
            }

            DriveInfo driveInfo = new(rootPath);
            if (!driveInfo.IsReady)
            {
                return (0, 0);
            }

            long totalBytes = driveInfo.TotalSize;
            long usedBytes = Math.Max(0, totalBytes - driveInfo.AvailableFreeSpace);
            return (usedBytes, totalBytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    // The G-code library is the archived file store surfaced by the system status UI.
    private long GetDirectorySizeSafe(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        long totalBytes = 0;
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(directoryPath);

        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDirectory))
                {
                    try
                    {
                        totalBytes += new FileInfo(filePath).Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Skipping file size calculation for {FilePath}", filePath);
                    }
                }

                foreach (string childDirectory in Directory.EnumerateDirectories(currentDirectory))
                {
                    pendingDirectories.Push(childDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping directory size calculation for {DirectoryPath}", currentDirectory);
            }
        }

        return totalBytes;
    }

    // Returns the same API version for in-process background services because they ship from the same assembly.
    private List<SystemServiceInfoDto> GetServices(string appVersion)
    {
        List<SystemServiceInfoDto> services =
        [
            new SystemServiceInfoDto
            {
                Name = "Backend API",
                Version = appVersion,
                Health = SystemServiceHealth.Healthy,
            }
        ];

        IReadOnlyList<BackgroundServiceStatus> backgroundServices = _backgroundServiceMonitor
            .GetAllStatuses()
            .OrderBy(status => status.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        services.AddRange(backgroundServices.Select(status => new SystemServiceInfoDto
        {
            Name = status.DisplayName,
            Version = appVersion,
            Health = MapServiceHealth(status),
        }));

        return services;
    }

    // Background services degrade when disabled or idle, and become critical when they report explicit errors.
    // Uses a failure-rate threshold so a single historical failure doesn't permanently mark a healthy service as Degraded.
    private static SystemServiceHealth MapServiceHealth(BackgroundServiceStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            return SystemServiceHealth.Critical;
        }

        if (!status.IsEnabled || !status.IsRunning)
        {
            return SystemServiceHealth.Degraded;
        }

        if (status.FailedRuns > 0)
        {
            long total = status.FailedRuns + status.SuccessfulRuns;

            // Degrade only if the service has never succeeded, or its failure rate exceeds 10%.
            bool neverSucceeded = status.SuccessfulRuns == 0;
            bool highFailureRate = total > 0 && status.FailedRuns * 100 / total > 10;
            if (neverSucceeded || highFailureRate)
            {
                return SystemServiceHealth.Degraded;
            }
        }

        return SystemServiceHealth.Healthy;
    }

    // Uses provider-specific queries where available, falling back to the SQLite file path logic already used in Program.cs.
    private async Task<long> GetDatabaseSizeBytesAsync(CancellationToken cancellationToken)
    {
        string providerName = _db.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                long fileSize = GetSqliteFileSizeBytes();
                if (fileSize > 0)
                {
                    return fileSize;
                }

                return await ExecuteInt64ScalarAsync("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();", cancellationToken);
            }

            if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || providerName.Contains("Postgre", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteInt64ScalarAsync("SELECT pg_database_size(current_database());", cancellationToken);
            }

            if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteInt64ScalarAsync("SELECT COALESCE(SUM(size), 0) * 8192 FROM sys.database_files;", cancellationToken);
            }

            if (providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteInt64ScalarAsync("SELECT COALESCE(SUM(data_length + index_length), 0) FROM information_schema.tables WHERE table_schema = DATABASE();", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve database size");
        }

        return 0;
    }

    // Database version is intentionally lightweight because this endpoint is polled from the UI.
    private async Task<string> GetDatabaseVersionAsync(CancellationToken cancellationToken)
    {
        string providerName = _db.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteStringScalarAsync("SELECT sqlite_version();", cancellationToken);
            }

            if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || providerName.Contains("Postgre", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteStringScalarAsync("SHOW server_version;", cancellationToken);
            }

            if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteStringScalarAsync("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));", cancellationToken);
            }

            if (providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteStringScalarAsync("SELECT VERSION();", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve database version");
        }

        return "Unknown";
    }

    // Normalizes EF provider names into the operator-facing engine labels used in the issue spec.
    private static string NormalizeDatabaseEngine(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return "Unknown";
        }

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return "SQLite";
        }

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || providerName.Contains("Postgre", StringComparison.OrdinalIgnoreCase))
        {
            return "PostgreSQL";
        }

        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return "SQL Server";
        }

        if (providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            return "MySQL";
        }

        return providerName;
    }

    // Mirrors the existing debug endpoint logic so SQLite file paths resolve the same way in production and tests.
    private long GetSqliteFileSizeBytes()
    {
        try
        {
            string connectionString = _db.Database.GetConnectionString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return 0;
            }

            SqliteConnectionStringBuilder builder = new(connectionString);
            string dataSource = builder.DataSource;
            if (string.IsNullOrWhiteSpace(dataSource)
                || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
                || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!Path.IsPathRooted(dataSource))
            {
                dataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));
            }

            return File.Exists(dataSource) ? new FileInfo(dataSource).Length : 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve SQLite file size");
            return 0;
        }
    }

    // Executes a provider-specific scalar query without taking ownership of the shared EF connection.
    private async Task<string> ExecuteStringScalarAsync(string sql, CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync(sql, cancellationToken);
        return value?.ToString() ?? "Unknown";
    }

    // Converts numeric scalar results to Int64 for disk/database size reporting.
    private async Task<long> ExecuteInt64ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync(sql, cancellationToken);
        if (value is null or DBNull)
        {
            return 0;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    // Connection open/close is balanced here so the method works whether EF already opened the connection or not.
    private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken)
    {
        DbConnection connection = _db.Database.GetDbConnection();
        bool shouldClose = connection.State == ConnectionState.Closed;

        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    // Linux CPU sample parser reads only the aggregate "cpu" row, which is enough for total host utilization.
    private static bool TryReadLinuxCpuSample(out LinuxCpuSample sample)
    {
        sample = default;

        try
        {
            string? cpuLine = File.ReadLines("/proc/stat").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(cpuLine))
            {
                return false;
            }

            string[] parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !string.Equals(parts[0], "cpu", StringComparison.Ordinal))
            {
                return false;
            }

            ulong totalTicks = 0;
            for (int index = 1; index < parts.Length; index++)
            {
                if (ulong.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
                {
                    totalTicks += value;
                }
            }

            ulong idleTicks = 0;
            if (parts.Length > 4 && ulong.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong idle))
            {
                idleTicks += idle;
            }

            if (parts.Length > 5 && ulong.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong iowait))
            {
                idleTicks += iowait;
            }

            sample = new LinuxCpuSample(totalTicks, idleTicks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Linux memory sampling uses MemAvailable because it better reflects reclaimable cache than MemFree alone.
    private static bool TryReadLinuxMemorySnapshot(out long usedBytes, out long totalBytes)
    {
        usedBytes = 0;
        totalBytes = 0;

        try
        {
            Dictionary<string, long> values = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new
                {
                    Key = parts[0],
                    Value = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                })
                .Where(entry => long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .ToDictionary(
                    entry => entry.Key,
                    entry => long.Parse(entry.Value!, CultureInfo.InvariantCulture),
                    StringComparer.OrdinalIgnoreCase);

            if (!values.TryGetValue("MemTotal", out long totalKb))
            {
                return false;
            }

            long availableKb = values.TryGetValue("MemAvailable", out long memAvailable)
                ? memAvailable
                : values.TryGetValue("MemFree", out long memFree) ? memFree : 0;

            totalBytes = totalKb * 1024;
            usedBytes = Math.Max(0, (totalKb - availableKb) * 1024);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Windows CPU sampling uses GetSystemTimes so it works without performance counters.
    private static bool TryReadWindowsCpuSample(out WindowsCpuSample sample)
    {
        sample = default;

        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return false;
        }

        ulong idleTicks = ToUInt64(idleTime);
        ulong kernelTicks = ToUInt64(kernelTime);
        ulong userTicks = ToUInt64(userTime);
        ulong totalTicks = kernelTicks + userTicks;

        sample = new WindowsCpuSample(totalTicks, idleTicks);
        return true;
    }

    // Windows memory sampling uses GlobalMemoryStatusEx to expose host-wide usage instead of process-only RSS.
    private static bool TryReadWindowsMemorySnapshot(out long usedBytes, out long totalBytes)
    {
        usedBytes = 0;
        totalBytes = 0;

        MemoryStatusEx memoryStatus = new() { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            return false;
        }

        totalBytes = (long)memoryStatus.TotalPhys;
        usedBytes = Math.Max(0, (long)(memoryStatus.TotalPhys - memoryStatus.AvailPhys));
        return true;
    }

    // Converts two cumulative CPU samples into a bounded percentage.
    private static double CalculateCpuUsage(ulong firstTotalTicks, ulong firstIdleTicks, ulong secondTotalTicks, ulong secondIdleTicks)
    {
        ulong totalDelta = secondTotalTicks - firstTotalTicks;
        if (totalDelta == 0)
        {
            return 0;
        }

        ulong idleDelta = secondIdleTicks - firstIdleTicks;
        double usage = 100d * (1d - ((double)idleDelta / totalDelta));
        return Math.Round(Math.Clamp(usage, 0d, 100d), 1);
    }

    private static ulong ToUInt64(FileTime fileTime)
    {
        return ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
    }

    private readonly record struct LinuxCpuSample(ulong TotalTicks, ulong IdleTicks);

    private readonly record struct WindowsCpuSample(ulong TotalTicks, ulong IdleTicks);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
