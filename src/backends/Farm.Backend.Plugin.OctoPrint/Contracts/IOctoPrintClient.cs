using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Contracts.Printers.OctoPrint
{
    /// <summary>
    /// Interface for OctoPrint client providing communication with OctoPrint 3D printer management servers.
    /// Supports printer status monitoring, job control, file management, temperature control, and system operations.
    /// </summary>
    public interface IOctoPrintClient : IBackendClient, ISupportsFileDownload, ISupportsFileList, ISupportsFileUpload, ISupportsFileDelete, ISupportsControlOperations
    {
        /// <summary>
        /// Tests the connection to an OctoPrint server.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>True if connection is successful; otherwise false.</returns>
        Task<bool> TestConnectionAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the current printer state including temperatures and flags.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>Printer state information, or null if unavailable.</returns>
        Task<OctoPrintPrinterState?> GetPrinterStateAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the current job status including progress and file information.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>Job status information, or null if unavailable.</returns>
        Task<OctoPrintJobStatus?> GetJobStatusAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Starts printing a file by name.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <param name="fileName">Name of the file to print.</param>
        /// <returns>True if successful; otherwise false.</returns>
        Task<bool> StartJobAsync(string baseUrl, PrinterCredential? credential, string fileName);

        /// <summary>
        /// Cancels the current print job.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>True if successful; otherwise false.</returns>
        Task<bool> CancelJobAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Pauses the current print job.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>True if successful; otherwise false.</returns>
        Task<bool> PauseJobAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Resumes a paused print job.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>True if successful; otherwise false.</returns>
        Task<bool> ResumeJobAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the camera stream URL from OctoPrint webcam configuration.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server.</param>
        /// <param name="credential">Printer credential for authentication.</param>
        /// <returns>Camera stream URL, or null if not configured.</returns>
        Task<string?> GetCameraStreamUrlAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the list of available gcode file names on the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<string[]> GetFileNameListAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the list of completed print jobs from OctoPrint history.
        /// Returns null if history is not available or API call fails.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="limit">Maximum number of history entries to return</param>
        /// <param name="start">Offset index for pagination</param>
        /// <param name="since">Filter to only return jobs started after this UTC timestamp</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, DateTime? since = null, PrinterCredential? credential = null, CancellationToken ct = default);

        /// <summary>
        /// Gets details for a specific print job from OctoPrint history.
        /// Returns null if the job is not found or API call fails.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="jobId">Unique identifier of the history job</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credential = null, CancellationToken ct = default);

        /// <summary>
        /// Gets aggregated print job statistics (totals) from OctoPrint history.
        /// Returns total print time, filament used, and job count for completed jobs.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

        /// <summary>
        /// Creates a PrinterDto from OctoPrint printer entity and status information.
        /// Encapsulates OctoPrint-specific DTO creation logic.
        /// </summary>
        /// <param name="printer">The printer database entity</param>
        /// <param name="printerStateJson">JSON response from OctoPrint /api/printer endpoint</param>
        /// <param name="jobStatusJson">JSON response from OctoPrint /api/job endpoint</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>A fully constructed PrinterDto with OctoPrint-specific data</returns>
        Task<PrinterDto> CreatePrinterDtoAsync(Printer printer, string printerStateJson, string jobStatusJson, PrinterCredential? credential, CancellationToken ct = default);

        /// <summary>
        /// Sends an arbitrary HttpRequestMessage using the underlying HttpClient.
        /// This exposes plugin and non-standard endpoints without requiring callers to
        /// reference a concrete implementation.
        /// </summary>
        /// <param name="request">The HTTP request message to send</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a gcode command to the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="gcode">The gcode command string to send</param>
        Task<bool> SendGcodeAsync(string baseUrl, PrinterCredential? credential, string gcode);

        /// <summary>
        /// Homes all axes using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<bool> SendHomeAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Homes XY axes using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        Task<bool> HomeXYAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

        /// <summary>
        /// Homes Z axis using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="ct">Cancellation token</param>
        Task<bool> HomeZAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);

        /// <summary>
        /// Sets target temperature for bed using native OctoPrint API endpoint /api/printer/bed.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="bedTemp">Target bed temperature in Celsius (0 to turn off)</param>
        Task<bool> SetBedTempAsync(string baseUrl, PrinterCredential? credential, double bedTemp);

        /// <summary>
        /// Sets target temperature for hotend (tool) using native OctoPrint API endpoint /api/printer/tool.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="hotendTemp">Target temperature in Celsius (0 to turn off)</param>
        /// <param name="tool">Tool index to set temperature for (default "tool0" for first hotend)</param>
        Task<bool> SetHotendTempAsync(string baseUrl, PrinterCredential? credential, double hotendTemp, string tool = "tool0");

        // PauseAsync and ResumeAsync are provided by ISupportsControlOperations base interface

        /// <summary>
        /// Cancels the current print job.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<bool> CancelPrintAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Jogs the printhead (moves axes incrementally without homing) using native OctoPrint API.
        /// Allows relative movement for bed leveling, nozzle positioning, etc.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="x">X axis movement (mm), optional</param>
        /// <param name="y">Y axis movement (mm), optional</param>
        /// <param name="z">Z axis movement (mm), optional</param>
        /// <param name="speed">Movement speed (mm/min), optional - not used by current OctoPrint API</param>
        Task<bool> JogAsync(string baseUrl, PrinterCredential? credential, double? x = null, double? y = null, double? z = null, double? speed = null);

        /// <summary>
        /// Connects the printer (initiates connection to physical device) using native OctoPrint API.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<bool> ConnectAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Disconnects the printer (closes connection to physical device) using native OctoPrint API.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<bool> DisconnectAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets the current connection state of the printer using native OctoPrint API.
        /// Returns JSON with connection information (current port, baudrate, printerProfile, state).
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        Task<string> GetConnectionStateAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets file details/metadata for a specific file on the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="path">File path (e.g., "folder/file.gcode" or just "file.gcode")</param>
        /// <returns>JSON string with file metadata (name, size, date, hash, etc.)</returns>
        Task<string> GetFileDetailsAsync(string baseUrl, PrinterCredential? credential, string path);

        /// <summary>
        /// Moves or renames a file or folder on the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="source">Source file/folder path (e.g., "old_name.gcode")</param>
        /// <param name="destination">Destination path (e.g., "new_folder/new_name.gcode")</param>
        /// <returns>Success status</returns>
        Task<bool> MoveFileAsync(string baseUrl, PrinterCredential? credential, string source, string destination);

        // DeleteFileAsync is inherited from ISupportsFileDelete with matching signature

        /// <summary>
        /// Creates a new folder on the printer's storage.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="path">Path where folder should be created (e.g., "folder")</param>
        /// <param name="folderName">Name of the new folder</param>
        /// <returns>Success status</returns>
        Task<bool> CreateFolderAsync(string baseUrl, PrinterCredential? credential, string path, string folderName);

        /// <summary>
        /// Uploads a gcode file to the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="fileContent">File content as byte array</param>
        /// <param name="fileName">Name of the file to upload</param>
        /// <param name="path">Optional destination folder (e.g., "folder" or null for root)</param>
        /// <param name="startPrint">Whether to start printing immediately after upload</param>
        /// <returns>Success status</returns>
        Task<bool> UploadFileAsync(string baseUrl, PrinterCredential? credential, byte[] fileContent, string fileName, string? path = null, bool startPrint = false);

        /// <summary>
        /// Gets OctoPrint server configuration/settings.
        /// Includes API version, data folder, temperature profiles, and other settings.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <returns>JSON string with server settings</returns>
        Task<string> GetSettingsAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Updates OctoPrint server settings.
        /// Allows configuration changes via API.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="settingsJson">JSON settings object to update</param>
        /// <returns>Success status</returns>
        Task<bool> UpdateSettingsAsync(string baseUrl, PrinterCredential? credential, string settingsJson);

        /// <summary>
        /// Restarts the OctoPrint server.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <returns>Success status</returns>
        Task<bool> RestartServerAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Gets detailed system information about the OctoPrint server.
        /// Includes operating system, Python version, OctoPrint version, and environment details.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <returns>JSON string with system information</returns>
        Task<string> GetSystemInfoAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Executes a system command on the OctoPrint host via the system endpoint.
        /// Requires system command plugin or appropriate permissions.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="commandId">System command ID to execute (e.g., "reboot", "shutdown")</param>
        /// <returns>Success status</returns>
        Task<bool> ExecuteSystemCommandAsync(string baseUrl, PrinterCredential? credential, string commandId);

        /// <summary>
        /// Gets detailed version information for OctoPrint server components.
        /// Includes OctoPrint version, OS, Python version, and plugin versions.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <returns>JSON string with detailed version information</returns>
        Task<string> GetVersionInfoAsync(string baseUrl, PrinterCredential? credential);

        /// <summary>
        /// Downloads the contents of a gcode file from the OctoPrint server.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
        /// <returns>File contents as byte array</returns>
        Task<byte[]> DownloadFileAsync(string baseUrl, PrinterCredential? credential, string filePath);

        /// <summary>
        /// Selects a file for printing without automatically starting the print job.
        /// Use this to prepare a file; call StartJobAsync to begin printing.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="credential">Printer credential for authentication</param>
        /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
        /// <returns>Success status</returns>
        Task<bool> LoadFileAsync(string baseUrl, PrinterCredential? credential, string filePath);

        // Add more OctoPrint API methods as needed
    }
}
