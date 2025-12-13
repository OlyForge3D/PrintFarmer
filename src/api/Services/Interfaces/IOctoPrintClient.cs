using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Printers;

namespace Farm.Web.Api.Services.Interfaces
{
    public interface IOctoPrintClient : IBackendClient
    {
        Task<bool> TestConnectionAsync(string baseUrl, string apiKey);
        Task<string> GetPrinterStateAsync(string baseUrl, string apiKey);
        Task<string> GetJobStatusAsync(string baseUrl, string apiKey);
        Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName);
        Task<bool> CancelJobAsync(string baseUrl, string apiKey);
        Task<string> GetCameraStreamUrlAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Gets the list of available gcode files on the printer.
        /// </summary>
        Task<string[]> GetFileListAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Gets the list of completed print jobs from OctoPrint history.
        /// Returns null if history is not available or API call fails.
        /// </summary>
        Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, string apiKey, int? limit = null, int? start = null);

        /// <summary>
        /// Gets details for a specific print job from OctoPrint history.
        /// Returns null if the job is not found or API call fails.
        /// </summary>
        Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string apiKey, string jobId);

        /// <summary>
        /// Creates a PrinterDto from OctoPrint printer entity and status information.
        /// Encapsulates OctoPrint-specific DTO creation logic.
        /// </summary>
        /// <param name="printer">The printer database entity</param>
        /// <param name="printerStateJson">JSON response from OctoPrint /api/printer endpoint</param>
        /// <param name="jobStatusJson">JSON response from OctoPrint /api/job endpoint</param>
        /// <param name="apiKey">API key for camera URL generation and plugin checks</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>A fully constructed PrinterDto with OctoPrint-specific data</returns>
        Task<PrinterDto> CreatePrinterDtoAsync(Printer printer, string printerStateJson, string jobStatusJson, string apiKey, CancellationToken ct = default);

        /// <summary>
        /// Sends an arbitrary HttpRequestMessage using the underlying HttpClient.
        /// This exposes plugin and non-standard endpoints without requiring callers to
        /// reference a concrete implementation.
        /// </summary>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a gcode command to the printer.
        /// </summary>
        Task<bool> SendGcodeAsync(string baseUrl, string apiKey, string gcode);

        /// <summary>
        /// Homes all axes using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        Task<bool> SendHomeAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Homes XY axes using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        Task<bool> HomeXYAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Homes Z axis using native OctoPrint /api/printer/printhead endpoint.
        /// </summary>
        Task<bool> HomeZAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Sets target temperature for bed using native OctoPrint API endpoint /api/printer/bed.
        /// </summary>
        Task<bool> SetBedTempAsync(string baseUrl, string apiKey, double bedTemp);

        /// <summary>
        /// Sets target temperature for hotend (tool) using native OctoPrint API endpoint /api/printer/tool.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="hotendTemp">Target temperature in Celsius (0 to turn off)</param>
        /// <param name="tool">Tool index to set temperature for (default "tool0" for first hotend)</param>
        Task<bool> SetHotendTempAsync(string baseUrl, string apiKey, double hotendTemp, string tool = "tool0");

        /// <summary>
        /// Pauses the current print job.
        /// </summary>
        Task<bool> PauseAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Resumes a paused print job.
        /// </summary>
        Task<bool> ResumeAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Cancels the current print job.
        /// </summary>
        Task<bool> CancelPrintAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Jogs the printhead (moves axes incrementally without homing) using native OctoPrint API.
        /// Allows relative movement for bed leveling, nozzle positioning, etc.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="x">X axis movement (mm), optional</param>
        /// <param name="y">Y axis movement (mm), optional</param>
        /// <param name="z">Z axis movement (mm), optional</param>
        /// <param name="speed">Movement speed (mm/min), optional - not used by current OctoPrint API</param>
        Task<bool> JogAsync(string baseUrl, string apiKey, double? x = null, double? y = null, double? z = null, double? speed = null);

        /// <summary>
        /// Connects the printer (initiates connection to physical device) using native OctoPrint API.
        /// </summary>
        Task<bool> ConnectAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Disconnects the printer (closes connection to physical device) using native OctoPrint API.
        /// </summary>
        Task<bool> DisconnectAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Gets the current connection state of the printer using native OctoPrint API.
        /// Returns JSON with connection information (current port, baudrate, printerProfile, state).
        /// </summary>
        Task<string> GetConnectionStateAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Gets file details/metadata for a specific file on the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="path">File path (e.g., "folder/file.gcode" or just "file.gcode")</param>
        /// <returns>JSON string with file metadata (name, size, date, hash, etc.)</returns>
        Task<string> GetFileDetailsAsync(string baseUrl, string apiKey, string path);

        /// <summary>
        /// Moves or renames a file or folder on the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="source">Source file/folder path (e.g., "old_name.gcode")</param>
        /// <param name="destination">Destination path (e.g., "new_folder/new_name.gcode")</param>
        /// <returns>Success status</returns>
        Task<bool> MoveFileAsync(string baseUrl, string apiKey, string source, string destination);

        /// <summary>
        /// Deletes a file or folder from the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="path">File/folder path to delete (e.g., "folder/file.gcode")</param>
        /// <returns>Success status</returns>
        Task<bool> DeleteFileAsync(string baseUrl, string apiKey, string path);

        /// <summary>
        /// Creates a new folder on the printer's storage.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="path">Path where folder should be created (e.g., "folder")</param>
        /// <param name="folderName">Name of the new folder</param>
        /// <returns>Success status</returns>
        Task<bool> CreateFolderAsync(string baseUrl, string apiKey, string path, string folderName);

        /// <summary>
        /// Uploads a gcode file to the printer.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="fileContent">File content as byte array</param>
        /// <param name="fileName">Name of the file to upload</param>
        /// <param name="path">Optional destination folder (e.g., "folder" or null for root)</param>
        /// <param name="startPrint">Whether to start printing immediately after upload</param>
        /// <returns>Success status</returns>
        Task<bool> UploadFileAsync(string baseUrl, string apiKey, byte[] fileContent, string fileName, string? path = null, bool startPrint = false);

        // Priority 3: Settings Management
        /// <summary>
        /// Gets OctoPrint server configuration/settings.
        /// Includes API version, data folder, temperature profiles, and other settings.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <returns>JSON string with server settings</returns>
        Task<string> GetSettingsAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Updates OctoPrint server settings.
        /// Allows configuration changes via API.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="settingsJson">JSON settings object to update</param>
        /// <returns>Success status</returns>
        Task<bool> UpdateSettingsAsync(string baseUrl, string apiKey, string settingsJson);

        // Priority 3: System Operations
        /// <summary>
        /// Restarts the OctoPrint server.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <returns>Success status</returns>
        Task<bool> RestartServerAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Gets detailed system information about the OctoPrint server.
        /// Includes operating system, Python version, OctoPrint version, and environment details.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <returns>JSON string with system information</returns>
        Task<string> GetSystemInfoAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Executes a system command on the OctoPrint host via the system endpoint.
        /// Requires system command plugin or appropriate permissions.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="commandId">System command ID to execute (e.g., "reboot", "shutdown")</param>
        /// <returns>Success status</returns>
        Task<bool> ExecuteSystemCommandAsync(string baseUrl, string apiKey, string commandId);

        // Priority 3: Server Info
        /// <summary>
        /// Gets detailed version information for OctoPrint server components.
        /// Includes OctoPrint version, OS, Python version, and plugin versions.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <returns>JSON string with detailed version information</returns>
        Task<string> GetVersionInfoAsync(string baseUrl, string apiKey);

        // Priority 2: File Operations
        /// <summary>
        /// Downloads the contents of a gcode file from the OctoPrint server.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
        /// <returns>File contents as byte array</returns>
        Task<byte[]> DownloadFileAsync(string baseUrl, string apiKey, string filePath);

        /// <summary>
        /// Selects a file for printing without automatically starting the print job.
        /// Use this to prepare a file; call StartJobAsync to begin printing.
        /// </summary>
        /// <param name="baseUrl">Base URL of OctoPrint server</param>
        /// <param name="apiKey">OctoPrint API key</param>
        /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
        /// <returns>Success status</returns>
        Task<bool> LoadFileAsync(string baseUrl, string apiKey, string filePath);

        // Add more OctoPrint API methods as needed
    }
}

