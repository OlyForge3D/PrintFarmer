using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services
{
    // Base response wrapper for Moonraker API responses
    public class MoonrakerResponse<T>
    {
        [JsonPropertyName("result")]
        public T Result { get; set; } = default!;
    }

    // JSON-RPC 2.0 models
    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public object? Params { get; set; }

        [JsonPropertyName("id")]
        public object? Id { get; set; }
    }

    public class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("result")]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }

        [JsonPropertyName("id")]
        public object? Id { get; set; }
    }

    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    // Server Administration Models
    public class ServerInfo
    {
        [JsonPropertyName("klippy_connected")]
        public bool KlippyConnected { get; set; }

        [JsonPropertyName("klippy_state")]
        public string KlippyState { get; set; } = string.Empty;

        [JsonPropertyName("components")]
        public string[] Components { get; set; } = Array.Empty<string>();

        [JsonPropertyName("failed_components")]
        public string[] FailedComponents { get; set; } = Array.Empty<string>();

        [JsonPropertyName("registered_directories")]
        public string[] RegisteredDirectories { get; set; } = Array.Empty<string>();

        [JsonPropertyName("warnings")]
        public string[] Warnings { get; set; } = Array.Empty<string>();

        [JsonPropertyName("websocket_count")]
        public int WebsocketCount { get; set; }

        [JsonPropertyName("moonraker_version")]
        public string MoonrakerVersion { get; set; } = string.Empty;

        [JsonPropertyName("api_version")]
        public int[] ApiVersion { get; set; } = Array.Empty<int>();

        [JsonPropertyName("api_version_string")]
        public string ApiVersionString { get; set; } = string.Empty;
    }

    public class ServerConfig
    {
        [JsonPropertyName("config")]
        public Dictionary<string, object> Config { get; set; } = new();

        [JsonPropertyName("orig")]
        public Dictionary<string, object> Orig { get; set; } = new();

        [JsonPropertyName("files")]
        public ConfigFile[] Files { get; set; } = Array.Empty<ConfigFile>();
    }

    public class ConfigFile
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("sections")]
        public string[] Sections { get; set; } = Array.Empty<string>();
    }

    public class TemperatureData
    {
        [JsonPropertyName("temperatures")]
        public Dictionary<string, double[][]> Temperatures { get; set; } = new();

        [JsonPropertyName("targets")]
        public Dictionary<string, double[][]> Targets { get; set; } = new();
    }

    public class GCodeStoreResponse
    {
        [JsonPropertyName("gcode_store")]
        public GCodeStoreEntry[] GCodeStore { get; set; } = Array.Empty<GCodeStoreEntry>();
    }

    public class GCodeStoreEntry
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public double Time { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }

    public class LogRolloverRequest
    {
        [JsonPropertyName("application")]
        public string? Application { get; set; }
    }

    public class LogRolloverResponse
    {
        [JsonPropertyName("rolled_over")]
        public string[] RolledOver { get; set; } = Array.Empty<string>();

        [JsonPropertyName("failed")]
        public Dictionary<string, string> Failed { get; set; } = new();
    }

    // Printer Administration Models
    public class MoonrakerPrinterInfo
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("state_message")]
        public string StateMessage { get; set; } = string.Empty;

        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = string.Empty;

        [JsonPropertyName("software_version")]
        public string SoftwareVersion { get; set; } = string.Empty;

        [JsonPropertyName("cpu_info")]
        public string CpuInfo { get; set; } = string.Empty;

        [JsonPropertyName("klipper_path")]
        public string KlipperPath { get; set; } = string.Empty;

        [JsonPropertyName("python_path")]
        public string PythonPath { get; set; } = string.Empty;

        [JsonPropertyName("log_file")]
        public string LogFile { get; set; } = string.Empty;

        [JsonPropertyName("config_file")]
        public string ConfigFile { get; set; } = string.Empty;
    }

    // Printer Status Models
    public class ObjectListResponse
    {
        [JsonPropertyName("objects")]
        public string[] Objects { get; set; } = Array.Empty<string>();
    }

    public class ObjectQueryResponse
    {
        [JsonPropertyName("status")]
        public Dictionary<string, object> Status { get; set; } = new();

        [JsonPropertyName("eventtime")]
        public double EventTime { get; set; }
    }

    public class ObjectSubscriptionRequest
    {
        [JsonPropertyName("objects")]
        public Dictionary<string, string[]?> Objects { get; set; } = new();
    }

    // GCode API Models
    public class GCodeRequest
    {
        [JsonPropertyName("script")]
        public string Script { get; set; } = string.Empty;
    }

    // Print Management Models
    public class PrintStartRequest
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
    }

    // Machine Request Models
    public class SystemInfo
    {
        [JsonPropertyName("system_info")]
        public SystemInfoData SystemInfoData { get; set; } = new();
    }

    public class SystemInfoData
    {
        [JsonPropertyName("cpu_info")]
        public CpuInfo CpuInfo { get; set; } = new();

        [JsonPropertyName("sd_info")]
        public SdInfo SdInfo { get; set; } = new();

        [JsonPropertyName("distribution")]
        public DistributionInfo Distribution { get; set; } = new();

        [JsonPropertyName("available_services")]
        public string[] AvailableServices { get; set; } = Array.Empty<string>();

        [JsonPropertyName("instance_ids")]
        public Dictionary<string, string> InstanceIds { get; set; } = new();

        [JsonPropertyName("service_state")]
        public Dictionary<string, ServiceState> ServiceStates { get; set; } = new();

        [JsonPropertyName("virtualization")]
        public VirtualizationInfo Virtualization { get; set; } = new();

        [JsonPropertyName("python")]
        public PythonInfo Python { get; set; } = new();

        [JsonPropertyName("network")]
        public Dictionary<string, NetworkInterface> Network { get; set; } = new();

        [JsonPropertyName("canbus")]
        public Dictionary<string, CanbusInterface> Canbus { get; set; } = new();
    }

    public class CpuInfo
    {
        [JsonPropertyName("cpu_count")]
        public int CpuCount { get; set; }

        [JsonPropertyName("bits")]
        public string Bits { get; set; } = string.Empty;

        [JsonPropertyName("processor")]
        public string Processor { get; set; } = string.Empty;

        [JsonPropertyName("cpu_desc")]
        public string CpuDesc { get; set; } = string.Empty;

        [JsonPropertyName("serial_number")]
        public string SerialNumber { get; set; } = string.Empty;

        [JsonPropertyName("hardware_desc")]
        public string HardwareDesc { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("total_memory")]
        public long TotalMemory { get; set; }

        [JsonPropertyName("memory_units")]
        public string MemoryUnits { get; set; } = string.Empty;
    }

    public class SdInfo
    {
        [JsonPropertyName("manufacturer_id")]
        public string ManufacturerId { get; set; } = string.Empty;

        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; } = string.Empty;

        [JsonPropertyName("oem_id")]
        public string OemId { get; set; } = string.Empty;

        [JsonPropertyName("product_name")]
        public string ProductName { get; set; } = string.Empty;

        [JsonPropertyName("product_revision")]
        public string ProductRevision { get; set; } = string.Empty;

        [JsonPropertyName("serial_number")]
        public string SerialNumber { get; set; } = string.Empty;

        [JsonPropertyName("manufacturer_date")]
        public string ManufacturerDate { get; set; } = string.Empty;

        [JsonPropertyName("capacity")]
        public string Capacity { get; set; } = string.Empty;

        [JsonPropertyName("total_bytes")]
        public long TotalBytes { get; set; }
    }

    public class DistributionInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("version_parts")]
        public VersionParts VersionParts { get; set; } = new();

        [JsonPropertyName("like")]
        public string Like { get; set; } = string.Empty;

        [JsonPropertyName("codename")]
        public string Codename { get; set; } = string.Empty;
    }

    public class VersionParts
    {
        [JsonPropertyName("major")]
        public string Major { get; set; } = string.Empty;

        [JsonPropertyName("minor")]
        public string Minor { get; set; } = string.Empty;

        [JsonPropertyName("build_number")]
        public string BuildNumber { get; set; } = string.Empty;
    }

    public class ServiceState
    {
        [JsonPropertyName("active_state")]
        public string ActiveState { get; set; } = string.Empty;

        [JsonPropertyName("sub_state")]
        public string SubState { get; set; } = string.Empty;
    }

    public class VirtualizationInfo
    {
        [JsonPropertyName("virt_type")]
        public string VirtType { get; set; } = string.Empty;

        [JsonPropertyName("virt_identifier")]
        public string VirtIdentifier { get; set; } = string.Empty;
    }

    public class PythonInfo
    {
        [JsonPropertyName("version")]
        public object[] Version { get; set; } = Array.Empty<object>();

        [JsonPropertyName("version_string")]
        public string VersionString { get; set; } = string.Empty;
    }

    public class NetworkInterface
    {
        [JsonPropertyName("mac_address")]
        public string MacAddress { get; set; } = string.Empty;

        [JsonPropertyName("ip_addresses")]
        public IpAddress[] IpAddresses { get; set; } = Array.Empty<IpAddress>();
    }

    public class IpAddress
    {
        [JsonPropertyName("family")]
        public string Family { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("is_link_local")]
        public bool IsLinkLocal { get; set; }
    }

    public class CanbusInterface
    {
        [JsonPropertyName("tx_queue_len")]
        public int TxQueueLen { get; set; }

        [JsonPropertyName("bitrate")]
        public int Bitrate { get; set; }

        [JsonPropertyName("driver")]
        public string Driver { get; set; } = string.Empty;
    }

    public class ServiceRequest
    {
        [JsonPropertyName("service")]
        public string Service { get; set; } = string.Empty;
    }

    public class ProcessStats
    {
        [JsonPropertyName("moonraker_stats")]
        public ProcessStat[] MoonrakerStats { get; set; } = Array.Empty<ProcessStat>();

        [JsonPropertyName("throttled_state")]
        public ThrottledState? ThrottledState { get; set; }

        [JsonPropertyName("cpu_temp")]
        public double? CpuTemp { get; set; }

        [JsonPropertyName("network")]
        public Dictionary<string, NetworkStats> Network { get; set; } = new();

        [JsonPropertyName("system_cpu_usage")]
        public Dictionary<string, double> SystemCpuUsage { get; set; } = new();

        [JsonPropertyName("system_uptime")]
        public double SystemUptime { get; set; }

        [JsonPropertyName("websocket_connections")]
        public int WebsocketConnections { get; set; }
    }

    public class ProcessStat
    {
        [JsonPropertyName("time")]
        public double Time { get; set; }

        [JsonPropertyName("cpu_usage")]
        public double CpuUsage { get; set; }

        [JsonPropertyName("memory")]
        public int Memory { get; set; }

        [JsonPropertyName("mem_units")]
        public string MemUnits { get; set; } = string.Empty;
    }

    public class ThrottledState
    {
        [JsonPropertyName("bits")]
        public int Bits { get; set; }

        [JsonPropertyName("flags")]
        public string[] Flags { get; set; } = Array.Empty<string>();
    }

    public class NetworkStats
    {
        [JsonPropertyName("rx_bytes")]
        public long RxBytes { get; set; }

        [JsonPropertyName("tx_bytes")]
        public long TxBytes { get; set; }

        [JsonPropertyName("bandwidth")]
        public double Bandwidth { get; set; }
    }

    public class SudoInfo
    {
        [JsonPropertyName("sudo_access")]
        public bool? SudoAccess { get; set; }

        [JsonPropertyName("linux_user")]
        public string LinuxUser { get; set; } = string.Empty;

        [JsonPropertyName("sudo_requested")]
        public bool SudoRequested { get; set; }

        [JsonPropertyName("request_messages")]
        public string[] RequestMessages { get; set; } = Array.Empty<string>();
    }

    public class SudoPasswordRequest
    {
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public class SudoPasswordResponse
    {
        [JsonPropertyName("sudo_responses")]
        public string[] SudoResponses { get; set; } = Array.Empty<string>();

        [JsonPropertyName("is_restarting")]
        public bool IsRestarting { get; set; }
    }

    public class UsbDevicesResponse
    {
        [JsonPropertyName("usb_devices")]
        public UsbDevice[] UsbDevices { get; set; } = Array.Empty<UsbDevice>();
    }

    public class UsbDevice
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = string.Empty;

        [JsonPropertyName("vendor_id")]
        public string VendorId { get; set; } = string.Empty;

        [JsonPropertyName("product_id")]
        public string ProductId { get; set; } = string.Empty;

        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; } = string.Empty;

        [JsonPropertyName("product")]
        public string Product { get; set; } = string.Empty;
    }

    public class SerialDevicesResponse
    {
        [JsonPropertyName("serial_devices")]
        public SerialDevice[] SerialDevices { get; set; } = Array.Empty<SerialDevice>();
    }

    public class SerialDevice
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = string.Empty;

        [JsonPropertyName("device_type")]
        public string DeviceType { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("by_id")]
        public string? ById { get; set; }

        [JsonPropertyName("by_path")]
        public string? ByPath { get; set; }
    }

    public class VideoDevicesResponse
    {
        [JsonPropertyName("video_devices")]
        public VideoDevice[] VideoDevices { get; set; } = Array.Empty<VideoDevice>();
    }

    public class VideoDevice
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("driver")]
        public string Driver { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("capabilities")]
        public string[] Capabilities { get; set; } = Array.Empty<string>();

        [JsonPropertyName("formats")]
        public VideoFormat[] Formats { get; set; } = Array.Empty<VideoFormat>();
    }

    public class VideoFormat
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("resolutions")]
        public string[] Resolutions { get; set; } = Array.Empty<string>();
    }

    public class CanbusResponse
    {
        [JsonPropertyName("can_uuids")]
        public CanUuid[] CanUuids { get; set; } = Array.Empty<CanUuid>();
    }

    public class CanUuid
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("application")]
        public string Application { get; set; } = string.Empty;
    }

    // File Operation Models
    public class FileRoot
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("permissions")]
        public string Permissions { get; set; } = string.Empty;
    }

    public class MoonrakerFileInfo
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("modified")]
        public double Modified { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("permissions")]
        public string? Permissions { get; set; }
    }

    public class DirectoryInfo
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("modified")]
        public double Modified { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("permissions")]
        public string? Permissions { get; set; }

        [JsonPropertyName("dirs")]
        public DirectoryInfo[] Dirs { get; set; } = Array.Empty<DirectoryInfo>();

        [JsonPropertyName("files")]
        public MoonrakerFileInfo[] Files { get; set; } = Array.Empty<MoonrakerFileInfo>();

        [JsonPropertyName("disk_usage")]
        public DiskUsage? DiskUsage { get; set; }
    }

    public class DiskUsage
    {
        [JsonPropertyName("used")]
        public long Used { get; set; }

        [JsonPropertyName("free")]
        public long Free { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }
    }

    public class DirectoryCreateRequest
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    public class DirectoryCreateResponse
    {
        [JsonPropertyName("item")]
        public DirectoryInfo Item { get; set; } = new();

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class FileMoveRequest
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("dest")]
        public string Dest { get; set; } = string.Empty;
    }

    public class FileCopyRequest
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("dest")]
        public string Dest { get; set; } = string.Empty;
    }

    public class GCodeMetadata
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("modified")]
        public double Modified { get; set; }

        [JsonPropertyName("slicer")]
        public string? Slicer { get; set; }

        [JsonPropertyName("slicer_version")]
        public string? SlicerVersion { get; set; }

        [JsonPropertyName("layer_height")]
        public double? LayerHeight { get; set; }

        [JsonPropertyName("first_layer_height")]
        public double? FirstLayerHeight { get; set; }

        [JsonPropertyName("object_height")]
        public double? ObjectHeight { get; set; }

        [JsonPropertyName("filament_total")]
        public double? FilamentTotal { get; set; }

        [JsonPropertyName("filament_weight_total")]
        public double? FilamentWeightTotal { get; set; }

        [JsonPropertyName("estimated_time")]
        public int? EstimatedTime { get; set; }

        [JsonPropertyName("thumbnails")]
        public ThumbnailInfo[] Thumbnails { get; set; } = Array.Empty<ThumbnailInfo>();

        [JsonPropertyName("first_layer_bed_temp")]
        public double? FirstLayerBedTemp { get; set; }

        [JsonPropertyName("first_layer_extr_temp")]
        public double? FirstLayerExtrTemp { get; set; }

        [JsonPropertyName("gcode_start_byte")]
        public long? GcodeStartByte { get; set; }

        [JsonPropertyName("gcode_end_byte")]
        public long? GcodeEndByte { get; set; }
    }

    public class ThumbnailInfo
    {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("relative_path")]
        public string RelativePath { get; set; } = string.Empty;
    }

    public class MetadataScanRequest
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
    }

    public class FileUploadResponse
    {
        [JsonPropertyName("item")]
        public MoonrakerFileInfo Item { get; set; } = new();

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("upload_info")]
        public UploadInfo? UploadInfo { get; set; }
    }

    public class UploadInfo
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("time_uploaded")]
        public double TimeUploaded { get; set; }
    }

    // Authorization Models
    public class LoginRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = "moonraker";
    }

    public class LoginResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    public class LogoutResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class UserInfo
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("created_on")]
        public double CreatedOn { get; set; }
    }

    public class CreateUserRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public class CreateUserResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class DeleteUserRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
    }

    public class DeleteUserResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class UserListResponse
    {
        [JsonPropertyName("users")]
        public UserInfo[] Users { get; set; } = Array.Empty<UserInfo>();
    }

    public class PasswordResetRequest
    {
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("new_password")]
        public string NewPassword { get; set; } = string.Empty;
    }

    public class PasswordResetResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class RefreshTokenRequest
    {
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class RefreshTokenResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    public class AuthInfo
    {
        [JsonPropertyName("default_source")]
        public string DefaultSource { get; set; } = string.Empty;

        [JsonPropertyName("available_sources")]
        public string[] AvailableSources { get; set; } = Array.Empty<string>();
    }

    // Database Models
    public class DatabaseListResponse
    {
        [JsonPropertyName("namespaces")]
        public string[] Namespaces { get; set; } = Array.Empty<string>();

        [JsonPropertyName("backups")]
        public string[] Backups { get; set; } = Array.Empty<string>();
    }

    public class DatabaseItemRequest
    {
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public object Value { get; set; } = new();
    }

    public class DatabaseItemResponse
    {
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public object? Value { get; set; }
    }

    // Job Queue Models
    public class JobQueueStatus
    {
        [JsonPropertyName("queued_jobs")]
        public QueuedJob[] QueuedJobs { get; set; } = Array.Empty<QueuedJob>();

        [JsonPropertyName("queue_state")]
        public string QueueState { get; set; } = string.Empty;
    }

    public class QueuedJob
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("job_id")]
        public string JobId { get; set; } = string.Empty;

        [JsonPropertyName("time_added")]
        public double TimeAdded { get; set; }

        [JsonPropertyName("time_in_queue")]
        public double TimeInQueue { get; set; }
    }

    public class EnqueueJobRequest
    {
        [JsonPropertyName("filenames")]
        public string[] Filenames { get; set; } = Array.Empty<string>();

        [JsonPropertyName("reset")]
        public bool Reset { get; set; }
    }

    // Announcement Models
    public class AnnouncementListResponse
    {
        [JsonPropertyName("entries")]
        public AnnouncementEntry[] Entries { get; set; } = Array.Empty<AnnouncementEntry>();

        [JsonPropertyName("feeds")]
        public string[] Feeds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("modified")]
        public bool? Modified { get; set; }
    }

    public class AnnouncementEntry
    {
        [JsonPropertyName("entry_id")]
        public string EntryId { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("priority")]
        public string Priority { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public long Date { get; set; }

        [JsonPropertyName("dismissed")]
        public bool Dismissed { get; set; }

        [JsonPropertyName("date_dismissed")]
        public long? DateDismissed { get; set; }

        [JsonPropertyName("dismiss_wake")]
        public long? DismissWake { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("feed")]
        public string Feed { get; set; } = string.Empty;
    }

    public class AnnouncementDismissRequest
    {
        [JsonPropertyName("entry_id")]
        public string EntryId { get; set; } = string.Empty;

        [JsonPropertyName("wake_time")]
        public long? WakeTime { get; set; }
    }

    public class AnnouncementDismissResponse
    {
        [JsonPropertyName("entry_id")]
        public string EntryId { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class AnnouncementFeedsResponse
    {
        [JsonPropertyName("feeds")]
        public string[] Feeds { get; set; } = Array.Empty<string>();
    }

    public class AnnouncementFeedRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class AnnouncementFeedResponse
    {
        [JsonPropertyName("feed")]
        public string Feed { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    // Webcam Models
    public class WebcamListResponse
    {
        [JsonPropertyName("webcams")]
        public WebcamInfo[] Webcams { get; set; } = Array.Empty<WebcamInfo>();
    }

    public class WebcamInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("service")]
        public string Service { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        [JsonPropertyName("target_fps")]
        public int TargetFps { get; set; }

        [JsonPropertyName("target_fps_idle")]
        public int TargetFpsIdle { get; set; }

        [JsonPropertyName("stream_url")]
        public string StreamUrl { get; set; } = string.Empty;

        [JsonPropertyName("snapshot_url")]
        public string SnapshotUrl { get; set; } = string.Empty;

        [JsonPropertyName("flip_horizontal")]
        public bool FlipHorizontal { get; set; }

        [JsonPropertyName("flip_vertical")]
        public bool FlipVertical { get; set; }

        [JsonPropertyName("rotation")]
        public int Rotation { get; set; }

        [JsonPropertyName("aspect_ratio")]
        public string AspectRatio { get; set; } = string.Empty;

        [JsonPropertyName("extra_data")]
        public Dictionary<string, object> ExtraData { get; set; } = new();

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;
    }

    public class WebcamRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("service")]
        public string? Service { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        [JsonPropertyName("target_fps")]
        public int? TargetFps { get; set; }

        [JsonPropertyName("target_fps_idle")]
        public int? TargetFpsIdle { get; set; }

        [JsonPropertyName("stream_url")]
        public string? StreamUrl { get; set; }

        [JsonPropertyName("snapshot_url")]
        public string? SnapshotUrl { get; set; }

        [JsonPropertyName("flip_horizontal")]
        public bool? FlipHorizontal { get; set; }

        [JsonPropertyName("flip_vertical")]
        public bool? FlipVertical { get; set; }

        [JsonPropertyName("rotation")]
        public int? Rotation { get; set; }

        [JsonPropertyName("aspect_ratio")]
        public string? AspectRatio { get; set; }
    }

    public class WebcamResponse
    {
        [JsonPropertyName("webcam")]
        public WebcamInfo Webcam { get; set; } = new();

        [JsonPropertyName("action")]
        public string? Action { get; set; }
    }

    public class WebcamTestResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_url")]
        public string StreamUrl { get; set; } = string.Empty;

        [JsonPropertyName("snapshot_url")]
        public string SnapshotUrl { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    // Update Manager Models
    public class UpdateStatus
    {
        [JsonPropertyName("busy")]
        public bool Busy { get; set; }

        [JsonPropertyName("github_rate_limit")]
        public int GithubRateLimit { get; set; }

        [JsonPropertyName("github_requests_remaining")]
        public int GithubRequestsRemaining { get; set; }

        [JsonPropertyName("github_limit_reset_time")]
        public long GithubLimitResetTime { get; set; }

        [JsonPropertyName("version_info")]
        public Dictionary<string, MoonrakerUpdateInfo> VersionInfo { get; set; } = new();
    }

    public class MoonrakerUpdateInfo
    {
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("debug_enabled")]
        public bool DebugEnabled { get; set; }

        [JsonPropertyName("is_valid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("configured_type")]
        public string ConfiguredType { get; set; } = string.Empty;

        [JsonPropertyName("detected_type")]
        public string DetectedType { get; set; } = string.Empty;

        [JsonPropertyName("remote_alias")]
        public string RemoteAlias { get; set; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; set; } = string.Empty;

        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        [JsonPropertyName("repo_name")]
        public string RepoName { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("remote_version")]
        public string RemoteVersion { get; set; } = string.Empty;

        [JsonPropertyName("rollback_version")]
        public string RollbackVersion { get; set; } = string.Empty;

        [JsonPropertyName("current_hash")]
        public string CurrentHash { get; set; } = string.Empty;

        [JsonPropertyName("remote_hash")]
        public string RemoteHash { get; set; } = string.Empty;

        [JsonPropertyName("is_dirty")]
        public bool IsDirty { get; set; }

        [JsonPropertyName("detached")]
        public bool Detached { get; set; }

        [JsonPropertyName("commits_behind")]
        public GitCommit[] CommitsBehind { get; set; } = Array.Empty<GitCommit>();

        [JsonPropertyName("git_messages")]
        public string[] GitMessages { get; set; } = Array.Empty<string>();

        [JsonPropertyName("full_version_string")]
        public string FullVersionString { get; set; } = string.Empty;

        [JsonPropertyName("pristine")]
        public bool Pristine { get; set; }

        [JsonPropertyName("corrupt")]
        public bool Corrupt { get; set; }

        [JsonPropertyName("info_tags")]
        public string[] InfoTags { get; set; } = Array.Empty<string>();

        [JsonPropertyName("recovery_url")]
        public string RecoveryUrl { get; set; } = string.Empty;

        [JsonPropertyName("remote_url")]
        public string RemoteUrl { get; set; } = string.Empty;

        [JsonPropertyName("warnings")]
        public string[] Warnings { get; set; } = Array.Empty<string>();

        [JsonPropertyName("anomalies")]
        public string[] Anomalies { get; set; } = Array.Empty<string>();
    }

    public class GitCommit
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }

    public class UpdateRefreshRequest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    public class UpdateClientRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class RecoverRepoRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("hard")]
        public bool Hard { get; set; }
    }

    public class RollbackRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    // Power Device Models
    public class PowerDeviceListResponse
    {
        [JsonPropertyName("devices")]
        public PowerDevice[] Devices { get; set; } = Array.Empty<PowerDevice>();
    }

    public class PowerDevice
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("locked_while_printing")]
        public bool LockedWhilePrinting { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }

    public class PowerDeviceRequest
    {
        [JsonPropertyName("device")]
        public string? Device { get; set; }

        [JsonPropertyName("devices")]
        public string[]? Devices { get; set; }
    }

    // History Models
    public class HistoryListResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("jobs")]
        public HistoryJob[] Jobs { get; set; } = Array.Empty<HistoryJob>();
    }

    public class HistoryJob
    {
        [JsonPropertyName("job_id")]
        public string JobId { get; set; } = string.Empty;

        [JsonPropertyName("exists")]
        public bool Exists { get; set; }

        [JsonPropertyName("end_time")]
        public double? EndTime { get; set; }

        [JsonPropertyName("filament_used")]
        public double FilamentUsed { get; set; }

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new();

        [JsonPropertyName("print_duration")]
        public double PrintDuration { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("start_time")]
        public double StartTime { get; set; }

        [JsonPropertyName("total_duration")]
        public double TotalDuration { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("auxiliary_data")]
        public AuxiliaryData[]? AuxiliaryData { get; set; }
    }

    public class AuxiliaryData
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public object Value { get; set; } = new();

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("units")]
        public string? Units { get; set; }
    }

    public class HistoryTotals
    {
        [JsonPropertyName("job_totals")]
        public JobTotals JobTotals { get; set; } = new();

        [JsonPropertyName("auxiliary_totals")]
        public AuxiliaryTotals[]? AuxiliaryTotals { get; set; }
    }

    public class JobTotals
    {
    [JsonPropertyName("total_jobs")]
    public double TotalJobs { get; set; }

        [JsonPropertyName("total_time")]
        public double TotalTime { get; set; }

        [JsonPropertyName("total_print_time")]
        public double TotalPrintTime { get; set; }

        [JsonPropertyName("total_filament_used")]
        public double TotalFilamentUsed { get; set; }

        [JsonPropertyName("longest_job")]
        public double LongestJob { get; set; }

        [JsonPropertyName("longest_print")]
        public double LongestPrint { get; set; }
    }

    public class AuxiliaryTotals
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("field")]
        public string Field { get; set; } = string.Empty;

        [JsonPropertyName("maximum")]
        public double Maximum { get; set; }

        [JsonPropertyName("total")]
        public double Total { get; set; }
    }

    // MQTT Models
    public class MqttPublishRequest
    {
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public object? Payload { get; set; }

        [JsonPropertyName("qos")]
        public int Qos { get; set; } = 0;

        [JsonPropertyName("retain")]
        public bool Retain { get; set; } = false;

        [JsonPropertyName("timeout")]
        public double Timeout { get; set; } = 5.0;
    }

    public class MqttPublishResponse
    {
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public string Payload { get; set; } = string.Empty;
    }

    public class MqttSubscribeRequest
    {
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = string.Empty;

        [JsonPropertyName("qos")]
        public int Qos { get; set; } = 0;

        [JsonPropertyName("timeout")]
        public double? Timeout { get; set; }
    }

    public class MqttSubscribeResponse
    {
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public string Payload { get; set; } = string.Empty;
    }

    // Spoolman Models
    public class SpoolmanStatus
    {
        [JsonPropertyName("spoolman_connected")]
        public bool SpoolmanConnected { get; set; }

        [JsonPropertyName("pending_reports")]
        public PendingReport[] PendingReports { get; set; } = Array.Empty<PendingReport>();

        [JsonPropertyName("spool_id")]
        public int? SpoolId { get; set; }
    }

    public class PendingReport
    {
        [JsonPropertyName("spool_id")]
        public int SpoolId { get; set; }

        [JsonPropertyName("filament_used")]
        public double FilamentUsed { get; set; }
    }

    public class SpoolmanSpoolIdRequest
    {
        [JsonPropertyName("spool_id")]
        public int? SpoolId { get; set; }
    }

    public class SpoolmanSpoolIdResponse
    {
        [JsonPropertyName("spool_id")]
        public int? SpoolId { get; set; }
    }

    public class SpoolmanProxyRequest
    {
        [JsonPropertyName("request_method")]
        public string RequestMethod { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("body")]
        public object? Body { get; set; }

        [JsonPropertyName("use_v2_response")]
        public bool UseV2Response { get; set; } = false;
    }

    // Extension Models
    public class ExtensionListResponse
    {
        [JsonPropertyName("agents")]
        public AgentInfo[] Agents { get; set; } = Array.Empty<AgentInfo>();
    }

    public class AgentInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    public class ExtensionCallRequest
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public Dictionary<string, object>? Params { get; set; }
    }

    // OctoPrint API Emulation Models
    public class OctoPrintVersionResponse
    {
        [JsonPropertyName("server")]
        public string Server { get; set; } = string.Empty;

        [JsonPropertyName("api")]
        public string Api { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class OctoPrintServerResponse
    {
        [JsonPropertyName("server")]
        public string Server { get; set; } = string.Empty;

        [JsonPropertyName("safemode")]
        public string Safemode { get; set; } = string.Empty;
    }

    public class OctoPrintLoginResponse
    {
        [JsonPropertyName("_is_external_client")]
        public bool IsExternalClient { get; set; }

        [JsonPropertyName("_login_mechanism")]
        public string LoginMechanism { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("user")]
        public bool User { get; set; }

        [JsonPropertyName("admin")]
        public bool Admin { get; set; }

        [JsonPropertyName("apikey")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("permissions")]
        public string[] Permissions { get; set; } = Array.Empty<string>();

        [JsonPropertyName("groups")]
        public string[] Groups { get; set; } = Array.Empty<string>();
    }

    public class OctoPrintSettingsResponse
    {
        [JsonPropertyName("plugins")]
        public Dictionary<string, object> Plugins { get; set; } = new();

        [JsonPropertyName("feature")]
        public OctoPrintFeature Feature { get; set; } = new();

        [JsonPropertyName("webcam")]
        public OctoPrintWebcam Webcam { get; set; } = new();
    }

    public class OctoPrintFeature
    {
        [JsonPropertyName("sdSupport")]
        public bool SdSupport { get; set; }

        [JsonPropertyName("temperatureGraph")]
        public bool TemperatureGraph { get; set; }
    }

    public class OctoPrintWebcam
    {
        [JsonPropertyName("flipH")]
        public bool FlipH { get; set; }

        [JsonPropertyName("flipV")]
        public bool FlipV { get; set; }

        [JsonPropertyName("rotate90")]
        public bool Rotate90 { get; set; }

        [JsonPropertyName("streamUrl")]
        public string StreamUrl { get; set; } = string.Empty;

        [JsonPropertyName("webcamEnabled")]
        public bool WebcamEnabled { get; set; }
    }

    public class OctoPrintJobResponse
    {
        [JsonPropertyName("job")]
        public OctoPrintJob Job { get; set; } = new();

        [JsonPropertyName("progress")]
        public OctoPrintProgress Progress { get; set; } = new();

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;
    }

    public class OctoPrintJob
    {
        [JsonPropertyName("file")]
        public OctoPrintFile File { get; set; } = new();

        [JsonPropertyName("estimatedPrintTime")]
        public double? EstimatedPrintTime { get; set; }

        [JsonPropertyName("filament")]
        public OctoPrintFilament Filament { get; set; } = new();

        [JsonPropertyName("user")]
        public string? User { get; set; }
    }

    public class OctoPrintFile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    public class OctoPrintFilament
    {
        [JsonPropertyName("length")]
        public double? Length { get; set; }
    }

    public class OctoPrintProgress
    {
        [JsonPropertyName("completion")]
        public double? Completion { get; set; }

        [JsonPropertyName("filepos")]
        public long? Filepos { get; set; }

        [JsonPropertyName("printTime")]
        public double? PrintTime { get; set; }

        [JsonPropertyName("printTimeLeft")]
        public double? PrintTimeLeft { get; set; }

        [JsonPropertyName("printTimeOrigin")]
        public string? PrintTimeOrigin { get; set; }
    }

    public class OctoPrintPrinterResponse
    {
        [JsonPropertyName("temperature")]
        public Dictionary<string, OctoPrintTemperature> Temperature { get; set; } = new();

        [JsonPropertyName("state")]
        public OctoPrintState State { get; set; } = new();
    }

    public class OctoPrintTemperature
    {
        [JsonPropertyName("actual")]
        public double Actual { get; set; }

        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        [JsonPropertyName("target")]
        public double Target { get; set; }
    }

    public class OctoPrintState
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("flags")]
        public OctoPrintFlags Flags { get; set; } = new();
    }

    public class OctoPrintFlags
    {
        [JsonPropertyName("operational")]
        public bool Operational { get; set; }

        [JsonPropertyName("paused")]
        public bool Paused { get; set; }

        [JsonPropertyName("printing")]
        public bool Printing { get; set; }

        [JsonPropertyName("cancelling")]
        public bool Cancelling { get; set; }

        [JsonPropertyName("pausing")]
        public bool Pausing { get; set; }

        [JsonPropertyName("error")]
        public bool Error { get; set; }

        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("closedOrError")]
        public bool ClosedOrError { get; set; }
    }

    public class OctoPrintCommandRequest
    {
        [JsonPropertyName("commands")]
        public string[] Commands { get; set; } = Array.Empty<string>();
    }

    public class OctoPrintProfilesResponse
    {
        [JsonPropertyName("profiles")]
        public Dictionary<string, OctoPrintProfile> Profiles { get; set; } = new();
    }

    public class OctoPrintProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("color")]
        public string Color { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("default")]
        public bool Default { get; set; }

        [JsonPropertyName("current")]
        public bool Current { get; set; }

        [JsonPropertyName("heatedBed")]
        public bool HeatedBed { get; set; }

        [JsonPropertyName("heatedChamber")]
        public bool HeatedChamber { get; set; }
    }
}
