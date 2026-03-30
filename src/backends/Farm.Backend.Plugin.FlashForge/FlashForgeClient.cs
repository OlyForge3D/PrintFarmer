using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// FlashForge TCP client implementation using the proprietary serial protocol.
/// Communicates over raw TCP sockets using G-code-like commands (~Mxxx).
/// Protocol reference: OrcaSlicer Flashforge.cpp implementation.
/// </summary>
public sealed partial class FlashForgeClient : IFlashForgeClient,
    ISupportsUploadAndPrint
{
    private readonly ILogger<FlashForgeClient> _logger;
    private readonly BackendTimeoutSettings _timeouts;

    /// <summary>Buffer size for TCP read/write operations (4 KB).</summary>
    private const int BufferSize = 4096;

    /// <summary>Response terminator indicating success (LF variant).</summary>
    private const string OkTerminatorLf = "ok\n";

    /// <summary>Response terminator indicating success (CR+LF variant).</summary>
    private const string OkTerminatorCrLf = "ok\r\n";

    public FlashForgeClient(ILogger<FlashForgeClient> logger, BackendTimeoutSettings timeouts)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);
            string response = await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);
            return response.Contains("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("FlashForge connection test failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TestConnectionAsync(Uri baseUrl, CancellationToken ct = default)
        => TestConnectionAsync(baseUrl.ToString(), ct);

    /// <inheritdoc />
    public async Task<string> SendCommandAsync(string host, int port, string command, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        client.ReceiveTimeout = _timeouts.CommandTimeoutSeconds * 1000;
        client.SendTimeout = _timeouts.CommandTimeoutSeconds * 1000;

        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();

        // Send command with newline terminator
        byte[] commandBytes = Encoding.ASCII.GetBytes(command + "\n");
        await stream.WriteAsync(commandBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Read response until "ok\n" terminator or timeout
        var responseBuilder = new StringBuilder();
        byte[] buffer = new byte[BufferSize];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeouts.CommandTimeoutSeconds));

        while (!timeoutCts.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break; // Connection closed
            }

            responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            string currentResponse = responseBuilder.ToString();

            if (currentResponse.Contains(OkTerminatorCrLf, StringComparison.Ordinal)
                || currentResponse.Contains(OkTerminatorLf, StringComparison.Ordinal))
            {
                break;
            }
        }

        return responseBuilder.ToString();
    }

    #region ISupportsStatus

    /// <inheritdoc />
    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);

            // Handshake first
            string handshake = await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);
            if (!handshake.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                return new PrinterStatus(false, null);
            }

            // Get machine status
            string statusResponse = await SendCommandAsync(host, port, "~M119", ct).ConfigureAwait(false);
            string? state = ParseMachineStatus(statusResponse);

            return new PrinterStatus(true, state);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("FlashForge status check failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return new PrinterStatus(false, null);
        }
    }

    #endregion

    #region ISupportsCompositeStatus

    /// <inheritdoc />
    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);

            // Handshake
            await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);

            // Get status, temps, progress in sequence (TCP serial protocol requires sequential commands)
            string statusResponse = await SendCommandAsync(host, port, "~M119", ct).ConfigureAwait(false);
            string tempResponse = await SendCommandAsync(host, port, "~M105", ct).ConfigureAwait(false);
            string progressResponse = await SendCommandAsync(host, port, "~M27", ct).ConfigureAwait(false);

            string? state = ParseMachineStatus(statusResponse);
            (double? hotendTemp, double? hotendTarget, double? bedTemp, double? bedTarget) = ParseTemperatures(tempResponse);
            (double? progress, string? jobName) = ParseProgress(progressResponse);

            bool isOnline = true;
            bool isPrinting = string.Equals(state, "Printing", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(state, "BUILDING_FROM_SD", StringComparison.OrdinalIgnoreCase);

            return new PrinterCompositeStatus(
                IsOnline: isOnline,
                State: state ?? "Idle",
                Progress: isPrinting ? progress : null,
                JobName: isPrinting ? jobName : null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                HotendTemp: hotendTemp,
                BedTemp: bedTemp,
                HotendTarget: hotendTarget,
                BedTarget: bedTarget);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("FlashForge composite status failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return new PrinterCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    #endregion

    #region ISupportsPrinterInformation

    /// <inheritdoc />
    public async Task<StandardPrinterInfo> GetPrinterInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);

            await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);
            string infoResponse = await SendCommandAsync(host, port, "~M115", ct).ConfigureAwait(false);

            return ParseDeviceInfo(infoResponse);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("FlashForge device info failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return new StandardPrinterInfo { Name = "FlashForge", Firmware = "Unknown", Model = "Unknown" };
        }
    }

    #endregion

    #region ISupportsFileUpload

    /// <inheritdoc />
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);
            string remotePath = $"0:/user/{fileName}";

            // Read file content into memory to get size
            using var memoryStream = new MemoryStream();
            await fileContent.CopyToAsync(memoryStream, ct).ConfigureAwait(false);
            byte[] fileBytes = memoryStream.ToArray();

            using var client = new TcpClient();
            client.ReceiveTimeout = _timeouts.FileUploadTimeoutSeconds * 1000;
            client.SendTimeout = _timeouts.FileUploadTimeoutSeconds * 1000;

            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            // Handshake
            await SendAndReadAsync(stream, "~M601 S1\n", ct).ConfigureAwait(false);

            // Begin upload: ~M28 <fileSize> 0:/user/<filename>
            string beginCommand = $"~M28 {fileBytes.Length} {remotePath}\n";
            string beginResponse = await SendAndReadAsync(stream, beginCommand, ct).ConfigureAwait(false);
            if (!beginResponse.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("FlashForge upload begin rejected: {BeginResponse}", beginResponse);
                return false;
            }

            // Send file data in chunks
            int offset = 0;
            while (offset < fileBytes.Length)
            {
                int chunkSize = Math.Min(BufferSize, fileBytes.Length - offset);
                await stream.WriteAsync(fileBytes.AsMemory(offset, chunkSize), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                offset += chunkSize;
            }

            // End upload
            string saveResponse = await SendAndReadAsync(stream, "~M29\n", ct).ConfigureAwait(false);
            if (!saveResponse.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("FlashForge upload save failed: {SaveResponse}", saveResponse);
                return false;
            }

            _logger.LogInformation("FlashForge: Uploaded {FileName} ({FileBytesLength} bytes) to {Host}:{Port}", fileName, fileBytes.Length, host, port);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "FlashForge upload failed for {BaseUrl}/{FileName}", baseUrl, fileName);
            return false;
        }
    }

    #endregion

    #region ISupportsStartPrint

    /// <inheritdoc />
    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);

            // Ensure path has 0:/user/ prefix
            string remotePath = fileName.StartsWith("0:/", StringComparison.Ordinal) ? fileName : $"0:/user/{fileName}";

            await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);
            string response = await SendCommandAsync(host, port, $"~M23 {remotePath}", ct).ConfigureAwait(false);

            if (response.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("FlashForge: Started print {RemotePath} on {Host}:{Port}", remotePath, host, port);
                return true;
            }

            _logger.LogWarning("FlashForge start print rejected for {RemotePath}: {Response}", remotePath, response);
            return false;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlashForge start print failed for {BaseUrl}/{FileName}", baseUrl, fileName);
            return false;
        }
    }

    #endregion

    #region ISupportsUploadAndPrint

    /// <summary>
    /// Uploads a G-code file and starts printing it on a FlashForge printer.
    /// FlashForge uses TCP protocol for both operations with the 0:/user/ path prefix.
    /// </summary>
    public async Task<UploadAndPrintResult> UploadAndStartPrintAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, IProgress<UploadAndPrintStage>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(UploadAndPrintStage.Uploading);

        bool uploaded = await UploadGcodeAsync(baseUrl, fileName, fileContent, credential, ct);
        if (!uploaded)
        {
            _logger.LogWarning("FlashForge: UploadAndStartPrint upload failed for {FileName}", fileName);
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, $"Failed to upload {fileName} to printer");
        }

        _logger.LogInformation("FlashForge: UploadAndStartPrint upload succeeded for {FileName}, starting print", fileName);
        progress?.Report(UploadAndPrintStage.StartingPrint);

        bool started = await StartPrintAsync(baseUrl, fileName, credential, ct);
        if (!started)
        {
            _logger.LogWarning("FlashForge: UploadAndStartPrint start print failed for {FileName} after successful upload", fileName);
            progress?.Report(UploadAndPrintStage.Failed);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.StartingPrint, $"Failed to start print of {fileName} after successful upload");
        }

        progress?.Report(UploadAndPrintStage.Completed);
        return UploadAndPrintResult.Ok();
    }

    #endregion

    #region ISupportsControlOperations

    /// <inheritdoc />
    public async Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendSimpleCommandAsync(baseUrl, "~M25", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendSimpleCommandAsync(baseUrl, "~M24", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await SendSimpleCommandAsync(baseUrl, "~M26", ct).ConfigureAwait(false);
    }

    #endregion

    #region ISupportsTemperatureControl

    /// <inheritdoc />
    public async Task<bool> SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);
            await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);

            bool success = true;

            if (hotendTemp.HasValue)
            {
                string response = await SendCommandAsync(host, port,
                    $"~M104 S{hotendTemp.Value.ToString("F0", CultureInfo.InvariantCulture)} T0", ct).ConfigureAwait(false);
                success &= response.Contains("ok", StringComparison.OrdinalIgnoreCase);
            }

            if (bedTemp.HasValue)
            {
                string response = await SendCommandAsync(host, port,
                    $"~M140 S{bedTemp.Value.ToString("F0", CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
                success &= response.Contains("ok", StringComparison.OrdinalIgnoreCase);
            }

            return success;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlashForge set temperatures failed for {BaseUrl}", baseUrl);
            return false;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sends a simple command with handshake and returns whether it succeeded.
    /// </summary>
    private async Task<bool> SendSimpleCommandAsync(string baseUrl, string command, CancellationToken ct)
    {
        try
        {
            (string host, int port) = ParseHostPort(baseUrl);
            await SendCommandAsync(host, port, "~M601 S1", ct).ConfigureAwait(false);
            string response = await SendCommandAsync(host, port, command, ct).ConfigureAwait(false);
            return response.Contains("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("FlashForge command {Command} failed for {BaseUrl}: {Message}", command, baseUrl, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends data on an existing stream and reads the response.
    /// Used for multi-step operations like file upload where the connection must stay open.
    /// </summary>
    private static async Task<string> SendAndReadAsync(NetworkStream stream, string data, CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(data);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        byte[] buffer = new byte[BufferSize];
        var responseBuilder = new StringBuilder();

        // Read until we see "ok" terminator or timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeoutCts.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            string current = responseBuilder.ToString();
            if (current.Contains(OkTerminatorCrLf, StringComparison.Ordinal)
                || current.Contains(OkTerminatorLf, StringComparison.Ordinal))
            {
                break;
            }
        }

        return responseBuilder.ToString();
    }

    /// <summary>
    /// Parses host and port from a base URL string.
    /// Accepts formats: "http://host:port", "host:port", "host" (uses default port).
    /// </summary>
    internal static (string Host, int Port) ParseHostPort(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        // Strip scheme if present
        string stripped = baseUrl;
        if (stripped.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped["http://".Length..];
        }
        else if (stripped.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped["https://".Length..];
        }

        // Remove trailing path
        int pathIndex = stripped.IndexOf('/', StringComparison.Ordinal);
        if (pathIndex >= 0)
        {
            stripped = stripped[..pathIndex];
        }

        // Split host:port
        int colonIndex = stripped.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(stripped[(colonIndex + 1)..], CultureInfo.InvariantCulture, out int port))
        {
            return (stripped[..colonIndex], port);
        }

        return (stripped, IFlashForgeClient.DefaultPort);
    }

    /// <summary>
    /// Parses the MachineStatus field from ~M119 response.
    /// Example response: "CMD M119 Received.\nEndstop: ...\nMachineStatus: READY\nMoveMode: READY\nok\n"
    /// </summary>
    internal static string? ParseMachineStatus(string response)
    {
        Match match = MachineStatusRegex().Match(response);
        if (match.Success)
        {
            string status = match.Groups[1].Value.Trim();
            return status switch
            {
                "READY" => "Idle",
                "BUILDING_FROM_SD" => "Printing",
                "PAUSED" => "Paused",
                "BUILDING_COMPLETED" => "Complete",
                _ => status
            };
        }

        return null;
    }

    /// <summary>
    /// Parses temperature values from ~M105 response.
    /// Example: "CMD M105 Received.\nT0:205 /210 B:60 /65\nok\n"
    /// </summary>
    internal static (double? HotendTemp, double? HotendTarget, double? BedTemp, double? BedTarget) ParseTemperatures(string response)
    {
        double? hotendTemp = null, hotendTarget = null, bedTemp = null, bedTarget = null;

        Match hotendMatch = HotendTempRegex().Match(response);
        if (hotendMatch.Success)
        {
            if (double.TryParse(hotendMatch.Groups[1].Value, CultureInfo.InvariantCulture, out double ht))
            {
                hotendTemp = ht;
            }

            if (double.TryParse(hotendMatch.Groups[2].Value, CultureInfo.InvariantCulture, out double htTarget))
            {
                hotendTarget = htTarget;
            }
        }

        Match bedMatch = BedTempRegex().Match(response);
        if (bedMatch.Success)
        {
            if (double.TryParse(bedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out double bt))
            {
                bedTemp = bt;
            }

            if (double.TryParse(bedMatch.Groups[2].Value, CultureInfo.InvariantCulture, out double btTarget))
            {
                bedTarget = btTarget;
            }
        }

        return (hotendTemp, hotendTarget, bedTemp, bedTarget);
    }

    /// <summary>
    /// Parses print progress from ~M27 response.
    /// Example: "CMD M27 Received.\nSD printing byte 1234/5678\nok\n"
    /// </summary>
    internal static (double? Progress, string? JobName) ParseProgress(string response)
    {
        Match match = ProgressRegex().Match(response);
        if (match.Success)
        {
            if (long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out long current) &&
                long.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out long total) &&
                total > 0)
            {
                double progress = (double)current / total * 100.0;
                return (Math.Round(progress, 1), null);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Parses device information from ~M115 response.
    /// Example: "CMD M115 Received.\nMachine Type: Adventurer 5X\nMachine Name: AD5X\nFirmware: v2.7.9\n..."
    /// </summary>
    internal static StandardPrinterInfo ParseDeviceInfo(string response)
    {
        var info = new StandardPrinterInfo { Name = "FlashForge", Firmware = "Unknown", Model = "Unknown" };

        Match machineType = MachineTypeRegex().Match(response);
        if (machineType.Success)
        {
            info.Model = machineType.Groups[1].Value.Trim();
        }

        Match machineName = MachineNameRegex().Match(response);
        if (machineName.Success)
        {
            info.Name = machineName.Groups[1].Value.Trim();
        }

        Match firmware = FirmwareRegex().Match(response);
        if (firmware.Success)
        {
            info.Firmware = firmware.Groups[1].Value.Trim();
        }

        return info;
    }

    [GeneratedRegex(@"MachineStatus:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineStatusRegex();

    [GeneratedRegex(@"T0:\s*([\d.]+)\s*/\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HotendTempRegex();

    [GeneratedRegex(@"B:\s*([\d.]+)\s*/\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BedTempRegex();

    [GeneratedRegex(@"SD printing byte\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"Machine Type:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineTypeRegex();

    [GeneratedRegex(@"Machine Name:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineNameRegex();

    [GeneratedRegex(@"Firmware:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareRegex();

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        // No persistent connections to dispose - each command opens a new TCP connection
    }
}
