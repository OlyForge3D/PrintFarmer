// DTO for a discovered file in a harvest operation
// DTO for importing selected discovered files
export interface ImportSelectedGcodeFilesDto {
  harvestOperationId: string;
  fileIds: string[];
}

// Result DTO for G-code harvest import
export interface GcodeHarvestResultDto {
  operationId: string;
  success: boolean;
  message: string;
  discoveredFiles: number;
  importedFiles: number;
  errors?: string[];
  importedFileIds: string[];
  skippedFileIds: string[];
  failedFileIds: string[];
  errorDetails?: Record<string, string>;
}
export interface DiscoveredGcodeFileDto {
  id: string;
  harvestOperationId: string;
  printerPath: string;
  fileName: string;
  fileSizeBytes: number;
  modifiedAt?: string;
  fileHash?: string;
  isSelected?: boolean;
  alreadyInLibrary: boolean;
  existingLibraryFileId?: string;
  processingFailed: boolean;
  errorMessage?: string;
  thumbnailUrl?: string;
  extractedSlicerName?: string;
  extractedSlicerVersion?: string;
  extractedPrintTime?: number;
  extractedFilamentLength?: number;
  extractedNozzleDiameter?: number;
  extractedMaterial?: string;
  extractedLayerHeight?: string;
  extractedInfill?: string;
  // Computed/UI fields (not from backend)
  status?: HarvestFileStatus;
  error?: string;
  filePath?: string; // Alias for printerPath
}

export enum HarvestFileStatus {
  Pending = 'Pending',
  InProgress = 'InProgress',
  Complete = 'Complete',
  Failed = 'Failed',
  Cancelled = 'Cancelled',
  Skipped = 'Skipped',
}
// PrintJobStatusDto for Moonraker print job status
export interface PrintJobStatusDto {
  state: string;
  progress?: number;
  jobName?: string;
  thumbnailUrl?: string;
  error?: string;
}

/**
 * Per-toolhead filament usage record for a print job.
 * Tracks which spool/filament was used by each toolhead during a print.
 */
export interface PrintJobToolheadUsage {
  id: string;
  printJobId: string;
  toolheadIndex: number;
  spoolmanSpoolId?: number;
  filamentUsageGrams?: number;
  filamentName?: string;
  filamentColor?: string;
  materialCostUsd?: number;
}

// Mirror existing shared models from Farm.Web.Shared

// ============== Printer Base Interfaces ==============
// These interfaces provide a consistent foundation for all printer DTOs.
// Use composition (extends) to build specific DTOs from these building blocks.

/**
 * Core printer identity - the minimum fields to identify a printer.
 * Every printer DTO that represents a specific printer should have these.
 */
export interface PrinterIdentity {
  /** Unique identifier for the printer */
  id: string;
  /** Display name for the printer */
  name: string;
  /** Backend type (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge) */
  backend: PrinterBackend;
}

/**
 * Authentication credentials for printer connections.
 * All printer DTOs that handle connection/authentication should extend or include these fields.
 */
export interface PrinterCredentials {
  /** API key for backends that use key-based auth (Moonraker, OctoPrint) */
  apiKey?: string;
  /** Username for HTTP Digest authentication (primarily for PrusaLink). Defaults to "maker" if not specified. */
  username?: string;
  /** Password for HTTP Digest authentication. Required for PrusaLink. */
  password?: string;
}

/**
 * Connection/network details for reaching a printer.
 * Used by DTOs that need to establish connections.
 */
export interface PrinterConnection {
  /** Backend API URL (e.g., "http://192.168.1.100:7125") */
  backendUrl?: string;
  /** Frontend web interface URL (e.g., "http://192.168.1.100") */
  frontendUrl?: string;
  /** Alternative property name for serverUrl (some DTOs use this) */
  serverUrl?: string;
  /** Original URL before hostname resolution */
  originalServerUrl?: string;
  /** Resolved IP address */
  ipAddress?: string;
  /** Backend API port (e.g., 7125 for Moonraker) */
  backendPort?: number;
  /** Frontend web interface port */
  frontendPort?: number;
}

/**
 * Descriptive metadata about a printer.
 */
export interface PrinterMetadata {
  /** User notes/description */
  notes?: string;
  /** Whether Obico AI failure detection is enabled for this printer. */
  obicoEnabled?: boolean;
  /** True when the printer's linked catalog model has been updated since the last template sync. */
  hasCatalogUpdate?: boolean;
  /** Manufacturer ID (foreign key) */
  manufacturerId?: string;
  /** Manufacturer name (e.g., "Prusa", "Creality") */
  manufacturerName?: string;
  /** Model ID (foreign key) */
  modelId?: string;
  /** Model name (e.g., "MK4", "Ender 3") */
  modelName?: string;
  /** Calibrated Z-offset in mm. Negative values move the nozzle closer to the bed. */
  zOffsetMm?: number;
  /** ISO datetime of last Z-offset calibration. */
  lastZOffsetCalibrationAt?: string;
}

/**
 * Camera URL information for a printer.
 */
export interface PrinterCameraInfo {
  /** Live camera stream URL (MJPEG, etc.) */
  cameraStreamUrl?: string;
  /** Single frame snapshot URL */
  cameraSnapshotUrl?: string;
}

/**
 * Live status information for a printer.
 */
export interface PrinterLiveStatus {
  /** Whether the printer is currently online/reachable */
  isOnline: boolean;
  /** Current printer state (e.g., "Idle", "Printing", "Paused") */
  state?: string;
}

/**
 * Temperature readings from printer sensors.
 */
export interface PrinterTemperatures {
  /** Current hotend/nozzle temperature */
  hotendTemp?: number;
  /** Current bed temperature */
  bedTemp?: number;
  /** Target hotend temperature */
  hotendTarget?: number;
  /** Target bed temperature */
  bedTarget?: number;
}

/**
 * Position coordinates for printer toolhead.
 */
export interface PrinterPosition {
  /** X axis position in mm */
  x?: number;
  /** Y axis position in mm */
  y?: number;
  /** Z axis position in mm */
  z?: number;
}

/**
 * Current print job information.
 */
export interface PrinterJobInfo {
  /** Print progress 0-100 */
  progress?: number;
  /** Name of the current job/file (may include path) */
  jobName?: string;
  /** File name only, without any directory path (e.g. "file.gcode" not ".cache/file.gcode") */
  fileName?: string;
  /** Thumbnail URL for the current job */
  thumbnailUrl?: string;
  /** Active spool identifier persisted in the PrintFarmer database */
  currentSpoolId?: number;
  /** Active spool/filament information */
  spoolInfo?: PrinterSpoolInfo;
  /** Estimated UTC timestamp when the current print will complete */
  estimatedCompletionTimeUtc?: string;
  /** Seconds remaining for the current print (from backend status) */
  printTimeLeftSeconds?: number;
}

/**
 * Operational state flags for a printer.
 */
export interface PrinterOperationalState {
  /** Whether printer is in maintenance mode */
  inMaintenance?: boolean;
  /** Whether printer is enabled for operations */
  isEnabled?: boolean;
}

/**
 * Lightweight location summary for printer assignment.
 * Matches backend LocationSummaryDto.
 */
export interface LocationSummary {
  /** Location identifier */
  id: string;
  /** Location display name */
  name: string;
  /** Optional description */
  description?: string;
}

/**
 * Full location DTO matching backend LocationDto.
 * Contains all location properties including hierarchy info.
 */
export interface Location {
  id: string;
  name: string;
  description?: string;
  parentId?: string | null;
  path?: string;
  depth: number;
  sortOrder: number;
  printerCount: number;
  totalPrinterCount: number;
  createdAt: string;
  modifiedAt: string;
  isActive: boolean;
}

/**
 * Nested tree structure matching backend LocationTreeDto.
 * Used for hierarchical location display.
 */
export interface LocationTreeNode {
  id: string;
  name: string;
  description?: string;
  parentId?: string | null;
  path?: string;
  depth: number;
  sortOrder: number;
  printerCount: number;
  totalPrinterCount: number;
  children: LocationTreeNode[];
}

/**
 * Breadcrumb item matching backend LocationBreadcrumbDto.
 */
export interface LocationBreadcrumbItem {
  id: string;
  name: string;
  /** Client-only field for UI rendering; not returned by the API. */
  depth?: number;
}

/** Request DTO for creating a location. Matches backend CreateLocationDto. */
export interface CreateLocationRequest {
  name: string;
  description?: string;
  parentId?: string | null;
  sortOrder?: number;
}

/** Request DTO for updating a location. Matches backend UpdateLocationDto. */
export interface UpdateLocationRequest {
  name?: string;
  description?: string;
  parentId?: string | null;
  sortOrder?: number;
}

/**
 * Location subtree printer DTO matching backend LocationSubtreePrinterDto.
 * Used for location dashboard printer list with status information.
 */
export interface LocationSubtreePrinter {
  printerId: string;
  printerName: string;
  locationId: string;
  locationName: string;
  backendType: string;
  isOnline: boolean;
  currentState?: string | null;
  currentJobName?: string | null;
  progressPercent?: number | null;
}

/** Request DTO for moving a location. Matches backend MoveLocationDto. */
export interface MoveLocationRequest {
  newParentId?: string | null;
}

/**
 * Combined base interface for full printer DTOs.
 * Extends all common base interfaces for a complete printer representation.
 * Use this as the base for DTOs that need most/all printer information.
 */
export interface PrinterBase extends 
  PrinterIdentity,
  PrinterCredentials,
  PrinterConnection,
  PrinterMetadata,
  PrinterCameraInfo,
  PrinterOperationalState {
  /** Location assignment (farm location) */
  location?: LocationSummary;
}

/**
 * Full printer DTO with all status and configuration information.
 * This is the most complete printer representation returned by the API.
 */
export interface Printer extends 
  PrinterBase,
  PrinterLiveStatus,
  PrinterTemperatures,
  PrinterPosition,
  PrinterJobInfo {
  // Required override - backendUrl is required for Printer
  backendUrl: string;
  // Additional Printer-specific fields
  isReachable: boolean;
  motionType?: MotionType;
  homedAxes?: string;
}

export interface PrinterCameraUrls {
  id: string;
  name: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
}

export interface PrinterVersionInfo {
  printerId: string;
  backend: PrinterBackend;
  supported: boolean;
  firmwareVersion?: string | null;
  backendVersion?: string | null;
  apiVersion?: string | null;
  retrievedAtUtc: string;
  message?: string | null;
}

export interface PrinterBackendCapabilitiesDto {
  printerId: string;
  printerName: string;
  backend: PrinterBackend;
  supportsCamera: boolean;
  supportsFileDownload: boolean;
  supportsFileList: boolean;
  supportsFileUpload: boolean;
  supportsStartPrint: boolean;
  supportsControlOperations: boolean;
  supportsFileMetadata: boolean;
  supportsMovement: boolean;
  supportsTemperatureControl: boolean;
  supportsPrinterInformation: boolean;
  supportsHistory: boolean;
  supportsFilamentControl: boolean;
}

/**
 * Lightweight printer DTO optimized for fast list retrieval.
 * Contains essential display info without full configuration details.
 */
export interface PrinterFast extends 
  PrinterBase,
  PrinterLiveStatus,
  PrinterTemperatures,
  PrinterPosition {
  // Required override - backendUrl is required for PrinterFast
  backendUrl: string;
}

export enum PrinterBackend {
  Unknown = 0,
  Moonraker = 1,
  PrusaLink = 2,
  SDCP = 3,
  OctoPrint = 4,
  FlashForge = 5,
}

export enum MotionType {
  Cartesian = 0,
  CoreXY = 1,
  Delta = 2,
  Unknown = 99,
}

// String enum types for API responses (enums are serialized as strings)
export type PrinterBackendString =
  | "Moonraker"
  | "PrusaLink"
  | "SDCP"
  | "OctoPrint"
  | "FlashForge";
export type MotionTypeString = "Cartesian" | "CoreXY" | "Delta" | "Unknown";

export interface PrinterSpoolInfo {
  hasActiveSpool?: boolean;
  activeSpoolId?: number;
  spoolName?: string;
  material?: string;
  colorHex?: string;
  filamentName?: string;
  vendor?: string;
  remainingWeightG?: number;
  initialWeightG?: number;
  spoolInUse?: boolean;
  // Legacy properties (may still be used)
  id?: number;
  filament?: FilamentInfo;
  used_length?: number;
  location?: string;
  lot_nr?: string;
  first_used?: string;
  last_used?: string;
}

/**
 * Combined printer identity with capabilities snapshot.
 * Used for export/import operations.
 * 
 * Note: Uses standard field names (id, name, modelName) for consistency.
 * Nullable types (| null) are used instead of optional (?) for explicit JSON serialization
 * in export/import scenarios, which is why this doesn't extend the base interfaces directly.
 */
export interface PrinterWithCapabilitiesDto {
  // Identity (standard naming)
  id: string;
  name: string;
  backend?: PrinterBackend | null;
  
  // Metadata (standard naming)
  modelName: string;
  manufacturerName?: string | null;
  notes?: string | null;
  
  // Connection
  serverUrl?: string | null;
  backendPort?: number | null;
  frontendPort?: number | null;
  
  // Credentials
  apiKey?: string | null;
  username?: string | null;
  password?: string | null;
  
  // Capabilities (unique to export DTO)
  capabilities?: PrinterCapabilitiesExportDto | null;
}

export interface FilamentInfo {
  id?: number;
  vendor?: VendorInfo;
  material?: string;
  color_hex?: string;
  price?: number;
  density?: number;
  diameter?: number;
  weight?: number;
}

export interface VendorInfo {
  id?: number;
  name?: string;
}

/**
 * Basic printer info without live status.
 * Used for configuration/management scenarios where real-time data isn't needed.
 */
export interface PrinterBasic extends 
  PrinterIdentity,
  PrinterCredentials,
  PrinterMetadata {
  /** Server URL (uses serverUrl instead of backendUrl) */
  serverUrl: string;
  originalServerUrl?: string;
  ipAddress?: string;
  backendPort?: number;
  frontendPort?: number;
}

/**
 * Live status info for real-time updates.
 * Contains only dynamic/changing printer state, no configuration.
 */
export interface PrinterStatus extends 
  PrinterLiveStatus,
  PrinterCameraInfo,
  PrinterTemperatures,
  PrinterPosition,
  PrinterJobInfo {
  /** Printer ID for correlation */
  id: string;
}

/**
 * Real-time update payload for SignalR.
 * Extends PrinterStatus with additional fields sent during live updates.
 */
export interface PrinterStatusUpdate extends PrinterStatus {
  /** Axes that have been homed (e.g., "xyz") */
  homedAxes?: string;
  /** MMU/ERCF status if detected */
  mmuStatus?: MmuStatus;
}

// ── MMU (Multi-Material Unit) types ──

/** Gate/slot status values from Happy Hare */
export enum MmuGateStatus {
  /** Gate disabled */
  Disabled = -1,
  /** No filament detected */
  Empty = 0,
  /** Filament available */
  Available = 1,
  /** Status unknown */
  Unknown = 2,
}

/** Single gate/slot on the MMU */
export interface MmuGate {
  /** Gate index (0-based) */
  index: number;
  /** Gate status: -1=disabled, 0=empty, 1=available, 2=unknown */
  status: MmuGateStatus;
  /** Material type (e.g., "PLA", "PETG", "ASA") */
  material?: string;
  /** CSS color string for the filament */
  color?: string;
  /** Filament brand/name */
  filamentName?: string;
  /** Spoolman spool ID (-1 = none) */
  spoolId: number;
  /** Lane/slot name (e.g., "lane1" for AFC), absent for HappyHare */
  name?: string;
}

/** Overall MMU status for a printer */
export interface MmuStatus {
  /** Whether the MMU is enabled */
  enabled: boolean;
  /** Whether the MMU has been homed */
  isHomed: boolean;
  /** Currently selected tool index (-1=none, -2=unknown) */
  activeTool: number;
  /** Currently selected gate index */
  activeGate: number;
  /** Filament load state: "Loaded", "Unloaded", "Unknown" */
  filamentState?: string;
  /** Current action: "Idle", "Loading", "Unloading", "Forming Tip", etc. */
  action?: string;
  /** Total number of gates/slots */
  numGates: number;
  /** Whether the MMU has a bypass */
  hasBypass: boolean;
  /** Whether endless spool mode is active */
  endlessSpool: boolean;
  /** Whether clog detection is active */
  clogDetection: boolean;
  /** Per-gate slot information */
  gates: MmuGate[];
  /** MMU protocol type: "HappyHare", "Qidibox", or "AFC" */
  mmuType?: string;
}

// File information with thumbnail
export interface PrinterFileDto {
  fileName: string;
  thumbnailUrl?: string;
  modified?: number; // Unix timestamp in seconds (only for Moonraker)
  sizeBytes?: number; // File size in bytes (only for Moonraker)
}

// DTOs for API operations
export interface CreatePrinterDto {
  name: string;
  serverUrl: string;
  originalServerUrl?: string;
  notes?: string;
  manufacturerId?: string;
  modelId?: string;
  newManufacturerName?: string;
  newModelName?: string;
  dateAcquired?: Date;
  backend: PrinterBackend;
  apiKey?: string;
  /** Username for HTTP Digest authentication (primarily for PrusaLink). Defaults to "maker" if not specified. */
  username?: string;
  /** Password for HTTP Digest authentication. User must obtain this from the printer's web interface. */
  password?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  backendPort?: number;
  frontendPort?: number;
  /** Power consumption in watts. Overrides the model's default wattage. */
  wattage?: number;
  /** Per-printer machine hourly rate override for cost tracking. */
  machineHourlyRate?: number;
}

// Test connection request/response for verifying printer connectivity
export interface TestConnectionRequest {
  serverUrl: string;
  backend: PrinterBackend;
  apiKey?: string;
  /** Username for HTTP Digest authentication (primarily for PrusaLink). Defaults to "maker" if not specified. */
  username?: string;
  /** Password for HTTP Digest authentication. User must obtain this from the printer's web interface. */
  password?: string;
  backendPort?: number;
}

export interface TestConnectionResponse {
  success: boolean;
  message?: string;
}

// Bulk import result item returned by /printers/bulk
export interface BulkImportResultItem {
  index: number;
  name: string;
  status: "Pending" | "Success" | "Skipped" | "Failed";
  id?: string;
  reason?: string;
}

export interface BulkImportResponse {
  importedCount: number;
  skippedCount: number;
  failureCount: number; 
  results: BulkImportResultItem[];
}

export interface UpdatePrinterDto {
  name?: string;
  serverUrl?: string;
  originalServerUrl?: string;
  notes?: string;
  manufacturerId?: string;
  modelId?: string;
  newManufacturerName?: string;
  newModelName?: string;
  dateAcquired?: Date;
  backend: PrinterBackend;
  apiKey?: string;
  /** Username for HTTP Digest authentication (primarily for PrusaLink). Defaults to "maker" if not specified. */
  username?: string;
  /** Password for HTTP Digest authentication. User must obtain this from the printer's web interface. */
  password?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  /** Optional Obico ML monitoring opt-in. When true, the app auto-assigns a healthy server. */
  obicoEnabled?: boolean;
  // Printer capabilities
  nozzleDiameter?: number;
  supportedMaterials?: string[];
  maxBuildVolumeX?: number;
  maxBuildVolumeY?: number;
  maxBuildVolumeZ?: number;
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  multiMaterial?: boolean;
  maxHotendTemp?: number;
  maxBedTemp?: number;
  supportsAutoLeveling?: boolean;
  maxPrintSpeed?: number;
  backendPort?: number;
  frontendPort?: number;
  isEnabled?: boolean;
  // Cost tracking overrides
  /** Power consumption in watts. Overrides the model's default wattage. */
  wattage?: number;
  /** Per-printer machine hourly rate override for cost tracking. */
  machineHourlyRate?: number;
  // Z-offset calibration
  /** Calibrated Z-offset in mm. Negative values move the nozzle closer to the bed. */
  zOffsetMm?: number;
  // Toolheads - for updating individual toolhead settings
  toolheads?: UpdateToolheadDto[];
}

// Update payload for modifying toolhead settings
export interface UpdateToolheadDto {
  id: string;
  name?: string;
  index?: number;
  nozzleDiameter?: number;
  // Component model references
  hotendModelId?: string;
  extruderModelId?: string;
  toolheadModelDefId?: string;
  nozzleModelId?: string;
  supportedMaterials?: string[];
  isPrimary?: boolean;
}

export interface ManufacturerDto {
  id: string;
  name: string;
  url?: string;
  description?: string;
}

export interface SlicerModelAliasDto {
  id: string;
  printerModelId: string;
  slicerModelName: string;
  slicerType: string;
}

export interface UpdateModelAliasesRequest {
  orcaSlicerNames: string[];
  prusaSlicerNames: string[];
}

/**
 * Nozzle material type for toolheads
 */
export enum NozzleType {
  Brass = 0,
  HardenedSteel = 1,
  StainlessSteel = 2,
  TungstenCarbide = 3,
  Abrasive = 4,
  Unknown = 99
}

export const NozzleTypeLabels: Record<NozzleType, string> = {
  [NozzleType.Brass]: 'Brass',
  [NozzleType.HardenedSteel]: 'Hardened Steel',
  [NozzleType.StainlessSteel]: 'Stainless Steel',
  [NozzleType.TungstenCarbide]: 'Tungsten Carbide',
  [NozzleType.Abrasive]: 'Abrasive',
  [NozzleType.Unknown]: 'Unknown'
};

/**
 * String-keyed nozzle type labels for use with JSON string enum serialization.
 * Backend sends NozzleType as string ("Brass", "HardenedSteel", etc.)
 */
export const NozzleTypeStringLabels: Record<string, string> = {
  'Brass': 'Brass',
  'HardenedSteel': 'Hardened Steel',
  'StainlessSteel': 'Stainless Steel',
  'TungstenCarbide': 'Tungsten Carbide',
  'Abrasive': 'Abrasive',
  'Unknown': 'Unknown'
};

// ============== Toolhead Types ==============
/**
 * Distinguishes physical toolheads from MMU/AMS virtual gate slots.
 * Backend serializes enums as strings via JsonStringEnumConverter.
 */
export enum ToolheadType {
  Physical = 0,
  MmuGate = 1,
}

/**
 * String-keyed toolhead type labels for use with JSON string enum serialization.
 * Backend sends ToolheadType as string ("Physical", "MmuGate")
 */
export const ToolheadTypeLabels: Record<string, string> = {
  Physical: 'Physical Toolhead',
  MmuGate: 'MMU Gate',
};

// ============== Nozzle Interface Types ==============
/**
 * Defines the nozzle thread/interface type that determines compatibility between hotends and nozzles.
 * This is the physical interface standard - hotends and nozzles must match to be compatible.
 */
export enum NozzleInterfaceType {
  /** Unknown or unspecified nozzle interface */
  Unknown = 0,
  /** E3D V6 standard thread (M6 x 1.0) - most common. Used by V6, Dragon, Rapido, Mosquito, CHC, most budget hotends */
  V6 = 1,
  /** E3D Volcano extended length (M6 x 1.0, longer melt zone) - for high-flow applications */
  Volcano = 2,
  /** E3D Revo quick-change system - no threading, magnetic/snap-fit */
  Revo = 3,
  /** Prusa Nextruder interface - proprietary for MK4/MK3.9S/CORE One */
  Nextruder = 4,
  /** BIQU H2 interface - proprietary for H2 hotend system */
  H2 = 5,
  /** Microswiss FlowTech interface - proprietary across their FlowTech line */
  FlowTech = 6,
  /** Bambu Lab proprietary interface - for X1/P1/A1 series */
  BambuLab = 7,
  /** Proprietary interface unique to a specific manufacturer/model */
  Proprietary = 99
}

/** Labels for nozzle interface types */
export const NozzleInterfaceTypeLabels: Record<NozzleInterfaceType, string> = {
  [NozzleInterfaceType.Unknown]: 'Unknown',
  [NozzleInterfaceType.V6]: 'V6 (M6 Thread)',
  [NozzleInterfaceType.Volcano]: 'Volcano',
  [NozzleInterfaceType.Revo]: 'Revo (Quick-Change)',
  [NozzleInterfaceType.Nextruder]: 'Nextruder (Prusa)',
  [NozzleInterfaceType.H2]: 'H2 (BIQU)',
  [NozzleInterfaceType.FlowTech]: 'FlowTech (Microswiss)',
  [NozzleInterfaceType.BambuLab]: 'Bambu Lab',
  [NozzleInterfaceType.Proprietary]: 'Proprietary'
};

// ============== Component Model Definitions ==============
// These are database-backed entities that allow extensible component tracking.
// Instead of enums, we use ID/name pairs for hotends, extruders, toolheads, and nozzles.

/**
 * Hotend model definition (from database)
 */
export interface HotendModelDefinition {
  id: string;
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  maxTemp?: number;
  isHighFlow: boolean;
  /** Maximum volumetric flow rate in mm³/s */
  maxFlowRate?: number;
  /** Nozzle interface type determines which nozzles are compatible with this hotend */
  nozzleInterface: NozzleInterfaceType;
  description?: string;
  url?: string;
}

/**
 * Extruder model definition (from database)
 */
export interface ExtruderModelDefinition {
  id: string;
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  gearRatio?: string;
  isDirectDrive: boolean;
  description?: string;
  url?: string;
}

/**
 * Toolhead model definition (from database)
 */
export interface ToolheadModelDefinition {
  id: string;
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  description?: string;
  url?: string;
  /** Default hotend ID for this toolhead */
  defaultHotendId?: string;
  /** Default extruder ID for this toolhead */
  defaultExtruderId?: string;
  /** Default nozzle ID for this toolhead */
  defaultNozzleId?: string;
}

/**
 * Nozzle model definition (from database)
 */
export interface NozzleModelDefinition {
  id: string;
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  /** Nozzle diameter in mm (e.g., 0.4, 0.6, 0.8) */
  diameter?: number;
  maxTemp?: number;
  /** The material type of this nozzle */
  nozzleType: NozzleType | string;
  /** Whether this nozzle is hardened for abrasive filaments (computed from nozzleType) */
  isHardened: boolean;
  /** Nozzle interface type - must match hotend's interface to be compatible */
  nozzleInterface: NozzleInterfaceType;
  description?: string;
  url?: string;
}

// ============== Component Model CRUD DTOs ==============

/**
 * DTO for creating a new hotend model
 */
export interface CreateHotendModelDto {
  name: string;
  manufacturerId: string;
  maxTemp?: number;
  isHighFlow?: boolean;
  /** Nozzle interface type - defaults to V6 if not specified */
  nozzleInterface?: NozzleInterfaceType;
  description?: string;
  url?: string;
}

/**
 * DTO for updating an existing hotend model
 */
export interface UpdateHotendModelDto {
  name?: string;
  manufacturerId?: string;
  maxTemp?: number;
  isHighFlow?: boolean;
  nozzleInterface?: NozzleInterfaceType;
  description?: string;
  url?: string;
}

/**
 * DTO for creating a new extruder model
 */
export interface CreateExtruderModelDto {
  name: string;
  manufacturerId: string;
  gearRatio?: string;
  isDirectDrive?: boolean;
  description?: string;
  url?: string;
}

/**
 * DTO for updating an existing extruder model
 */
export interface UpdateExtruderModelDto {
  name?: string;
  manufacturerId?: string;
  gearRatio?: string;
  isDirectDrive?: boolean;
  description?: string;
  url?: string;
}

/**
 * DTO for creating a new toolhead model
 */
export interface CreateToolheadModelDto {
  name: string;
  manufacturerId: string;
  description?: string;
  url?: string;
}

/**
 * DTO for updating an existing toolhead model
 */
export interface UpdateToolheadModelDefDto {
  name?: string;
  manufacturerId?: string;
  description?: string;
  url?: string;
  /** Default hotend ID for this toolhead */
  defaultHotendId?: string | null;
  /** Default extruder ID for this toolhead */
  defaultExtruderId?: string | null;
  /** Default nozzle ID for this toolhead */
  defaultNozzleId?: string | null;
}

/**
 * DTO for creating a new nozzle model
 */
export interface CreateNozzleModelDto {
  name: string;
  manufacturerId: string;
  /** Nozzle diameter in mm - defaults to 0.4 if not specified */
  diameter?: number;
  maxTemp?: number;
  /** The material type of this nozzle - defaults to Brass if not specified */
  nozzleType?: NozzleType | string;
  /** Nozzle interface type - defaults to V6 if not specified */
  nozzleInterface?: NozzleInterfaceType;
  description?: string;
  url?: string;
}

/**
 * DTO for updating an existing nozzle model
 */
export interface UpdateNozzleModelDto {
  name?: string;
  manufacturerId?: string;
  /** Nozzle diameter in mm */
  diameter?: number;
  maxTemp?: number;
  /** The material type of this nozzle */
  nozzleType?: NozzleType | string;
  nozzleInterface?: NozzleInterfaceType;
  description?: string;
  url?: string;
}

// ============== Contextual Manufacturer Types ==============

/**
 * Context types for filtering manufacturers by what items they have
 */
export enum CatalogContext {
  Printers = 'Printers',
  Hotends = 'Hotends',
  Extruders = 'Extruders',
  Toolheads = 'Toolheads',
  Nozzles = 'Nozzles'
}

/**
 * Manufacturer with item count for a specific context
 */
export interface ManufacturerWithCount {
  id: string;
  name: string;
  itemCount: number;
}

/**
 * Response DTO grouping manufacturers by whether they have items in a context
 */
export interface ManufacturersByContext {
  withItems: ManufacturerWithCount[];
  withoutItems: ManufacturerWithCount[];
}

/**
 * Toolhead template for a printer model
 * Derived values: nozzleDiameter from NozzleModel, maxFlowRate/maxTemp from HotendModel
 */
export interface PrinterModelToolheadDto {
  id: string;
  name: string;
  index: number;
  nozzleDiameter?: number;     // Derived from NozzleModel.Diameter
  nozzleType?: NozzleType | string;  // Derived from NozzleModel.NozzleType
  maxFlowRate?: number;        // Derived from HotendModel.MaxFlowRate
  maxTemp?: number;            // Derived from HotendModel.MaxTemp
  // Component model references (IDs and resolved names from database)
  hotendModelId?: string;
  hotendModelName?: string;
  extruderModelId?: string;
  extruderModelName?: string;
  toolheadModelDefId?: string;
  toolheadModelDefName?: string;
  nozzleModelId?: string;
  nozzleModelName?: string;
  supportedMaterials?: string[];
  isPrimary: boolean;
}

export interface PrinterModelDto {
  id: string;
  name: string;
  manufacturerId: string;
  motionType?: MotionTypeString;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackendString;
  supportedFilamentTypes?: string[];

  // Capability properties (nozzle diameter and max hotend temp are now on toolheads)
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  hasCarbonFilter?: boolean;
  hasHepaFilter?: boolean;
  hasBowdenTube?: boolean;
  hasPtfeLiner?: boolean;
  hasLinearRails?: boolean;
  hasLeadScrews?: boolean;
  hasToolchanger?: boolean;
  hasFilamentCutter?: boolean;
  hasHeatedChamber?: boolean;
  multiMaterial?: boolean;
  supportsAutoLeveling?: boolean;
  maxBedTemp?: number;
  maxPrintSpeed?: number;
  /** Default power consumption in watts for this printer model. */
  defaultWattage?: number;
  /** Default machine hourly rate for cost calculations. */
  defaultHourlyRate?: number;

  // Toolhead templates for multi-toolhead printers
  toolheads?: PrinterModelToolheadDto[];
}

// Printer capabilities interface
export interface PrinterCapabilitiesExportDto {
  id: string;
  nozzleDiameter?: number;
  supportedMaterials?: string[];
  maxBuildVolumeX?: number;
  maxBuildVolumeY?: number;
  maxBuildVolumeZ?: number;
  hasHeatedBed: boolean;
  hasEnclosure: boolean;
  multiMaterial: boolean;
  supportsAutoLeveling: boolean;
  maxHotendTemp?: number;
  maxBedTemp?: number;
  currentMaterial?: string;
  currentSpoolId?: number;
  isAvailable: boolean;
  lastUpdated: Date;
}

export interface PrinterCapabilitiesDto {
  id: string;
  printerId: string;
  printerName: string;
  nozzleDiameter?: number;
  supportedMaterials?: string[];
  maxBuildVolumeX?: number;
  maxBuildVolumeY?: number;
  maxBuildVolumeZ?: number;
  hasHeatedBed: boolean;
  hasEnclosure: boolean;
  multiMaterial: boolean;
  maxHotendTemp?: number;
  maxBedTemp?: number;
  currentMaterial?: string;
  currentSpoolId?: number;
  isAvailable: boolean;
  supportsAutoLeveling: boolean;
  maxPrintSpeed?: number;
  lastUpdated: Date;
}

// Toolhead data for reading/editing
export interface ToolheadDto {
  id: string;
  name?: string;
  index: number;
  nozzleDiameter?: number;     // Derived from NozzleModel.Diameter
  nozzleType?: NozzleType | string;  // Derived from NozzleModel.NozzleType
  maxFlowRate?: number;        // Derived from HotendModel.MaxFlowRate
  maxTemp?: number;            // Derived from HotendModel.MaxTemp
  // Component model references (IDs and resolved names from database)
  hotendModelId?: string;
  hotendModelName?: string;
  extruderModelId?: string;
  extruderModelName?: string;
  toolheadModelDefId?: string;
  toolheadModelDefName?: string;
  nozzleModelId?: string;
  nozzleModelName?: string;
  supportedMaterials?: string[];
  isPrimary: boolean;
  lastUpdated?: Date;
  // Multi-toolhead filament tracking
  toolheadType?: ToolheadType | string;
  currentSpoolId?: number;
  currentMaterial?: string;
  currentFilamentColor?: string;
}

// Printer details for edit page
export interface PrinterDetails {
  id: string;
  name: string;
  serverUrl: string;
  notes?: string;
  manufacturerId?: string;
  manufacturerName?: string;
  modelId?: string;
  modelName?: string;
  modelMotionType?: MotionType;
  modelMaxX?: number;
  modelMaxY?: number;
  modelMaxZ?: number;
  dateAcquired?: Date;
  backend: PrinterBackend;
  apiKey?: string;
  username?: string;
  password?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  originalServerUrl?: string;
  ipAddress?: string;
  backendPort?: number | null;
  frontendPort?: number | null;
  /** Assigned Obico ML server ID (managed by backend, not user-facing). */
  obicoServerId?: string | null;
  /** Assigned Obico ML server name (for display). */
  obicoServerName?: string | null;
  /** Whether Obico AI failure detection is enabled for this printer. */
  obicoEnabled?: boolean;
  /** Power consumption in watts. Overrides the model's default wattage. */
  wattage?: number;
  /** Per-printer machine hourly rate override for cost tracking. */
  machineHourlyRate?: number;
  /** True when the printer's linked catalog model has been updated since the last template sync. */
  hasCatalogUpdate?: boolean;
  capabilities?: PrinterCapabilitiesDto;
  toolheads?: ToolheadDto[];
}

// Temperature targets
export interface TempTargets {
  hotend: number;
  bed: number;
}

// Dynamic filament presets
export interface FilamentPresets {
  [filamentType: string]: TempTargets;
}

// Filament type management
export interface FilamentType {
  id: string;
  name: string;
  defaultTemperatures: TempTargets;
}

// Health status response shapes (discriminated)
// Basic health (/healthz, /api/healthz)
export interface BasicHealthStatus {
  kind: "basic";
  status: string; // "ok"
}

// Detailed health (/health, /api/health) produced by ASP.NET Core health checks writer
// Property names are camelCased by System.Text.Json (see Program.HealthJsonOptions)
// Note: Backend returns numeric enum values (0=Unhealthy, 1=Degraded, 2=Healthy)
export interface DetailedHealthStatusEntry {
  status: string | number; // Backend sends enum as number, frontend converts to string
  duration: string;
  description?: string;
  data?: Record<string, unknown>;
}

export interface StartupStatus {
  phase: string;
  ready: boolean;
  failed: boolean;
  failureMessage?: string;
  failureStackTrace?: string;
  initStartedUtc?: string;
  initCompletedUtc?: string;
  initDurationMs?: number;
}

export interface DetailedHealthStatus {
  kind: "detailed";
  status: string; // Overall status
  totalChecksDuration: string; // Overall duration
  startup?: StartupStatus; // Startup initialization status
  results: Record<string, DetailedHealthStatusEntry>;
}

// Union used by hooks/components; runtime narrowing via 'kind'
export type HealthStatus = BasicHealthStatus | DetailedHealthStatus;

// Runtime type guard helpers
export function isDetailedHealthStatus(
  h: HealthStatus | undefined | null
): h is DetailedHealthStatus {
  if (!h || h.kind !== "detailed") return false;
  const candidate: unknown = (h as unknown as { results?: unknown }).results;
  return typeof candidate === "object" && candidate !== null;
}

export function isBasicHealthStatus(
  h: HealthStatus | undefined | null
): h is BasicHealthStatus {
  return !!h && h.kind === "basic";
}

export interface FilamentTypeDto {
  id: string;
  name: string;
  defaultTemperatures: TempTargets;
  /** True if the filament contains abrasive materials (e.g., carbon fiber, glass fiber) that require hardened nozzles. */
  isAbrasive: boolean;
  /** True if the filament requires an enclosure for optimal printing (e.g., ABS, ASA, Nylon). */
  needsEnclosure: boolean;
  /** Default price per kilogram in USD for cost estimation. */
  defaultPricePerKg?: number | null;
  /** Default material density in g/cm³ for weight-based cost calculation. */
  defaultDensity?: number | null;
}

export interface CreateFilamentTypeRequest {
  name: string;
  defaultTemperatures: TempTargets;
  /** True if the filament contains abrasive materials requiring hardened nozzles. */
  isAbrasive?: boolean;
  /** True if the filament requires an enclosure for optimal printing. */
  needsEnclosure?: boolean;
  /** Default price per kilogram in USD for cost estimation. */
  defaultPricePerKg?: number | null;
  /** Default material density in g/cm³ for weight-based cost calculation. */
  defaultDensity?: number | null;
}

export interface UpdateFilamentTypeRequest {
  name: string;
  defaultTemperatures: TempTargets;
  /** True if the filament contains abrasive materials requiring hardened nozzles. */
  isAbrasive?: boolean;
  /** True if the filament requires an enclosure for optimal printing. */
  needsEnclosure?: boolean;
  /** Default price per kilogram in USD for cost estimation. */
  defaultPricePerKg?: number | null;
  /** Default material density in g/cm³ for weight-based cost calculation. */
  defaultDensity?: number | null;
}

// ============ Material Cluster Types ============

export interface MaterialClusterMemberDto {
  filamentTypeId: string;
  filamentTypeName: string;
  addedAt: string;
}

export interface MaterialClusterDto {
  id: string;
  name: string;
  description?: string | null;
  createdAt: string;
  updatedAt: string;
  members: MaterialClusterMemberDto[];
}

export interface CreateMaterialClusterRequest {
  name: string;
  description?: string | null;
  filamentTypeIds?: string[];
}

export interface UpdateMaterialClusterRequest {
  name: string;
  description?: string | null;
}

export interface SpoolmanFilamentImportResult {
  importedCount: number;
  skippedCount: number;
  totalSpoolmanMaterials: number;
  importedNames: string[];
}

export interface SpoolmanDiscoveryResult {
  url: string;
  isAvailable: boolean;
  error?: string;
  version?: string;
  responseTime?: number; // in milliseconds
}

// CSV Import/Export
export interface FilamentCsvImportResult {
  createdCount: number;
  updatedCount: number;
  errorCount: number;
  totalRows: number;
  errors: string[];
}

// SpoolmanDB community database types
export interface SpoolmanDbFilamentEntry {
  id: string;
  manufacturer: string;
  name: string;
  material: string;
  density?: number | null;
  weight?: number | null;
  spoolWeight?: number | null;
  spoolType?: string | null;
  diameter?: number | null;
  colorHex?: string | null;
  colorHexes?: string[] | null;
  extruderTemp?: number | null;
  extruderTempRange?: number[] | null;
  bedTemp?: number | null;
  bedTempRange?: number[] | null;
  finish?: string | null;
  translucent?: boolean;
  glow?: boolean;
}

export interface SpoolmanDbMaterialEntry {
  material: string;
  density?: number | null;
  extruderTemp?: number | null;
  bedTemp?: number | null;
}

export interface SpoolmanDbImportRequest {
  filamentIds: string[];
}

export interface SpoolmanDbImportResult {
  createdCount: number;
  updatedCount: number;
  errorCount: number;
  errors: string[];
}

// ─── Open Filament Database types ────────────────────────────────────────

export interface OfdBrand {
  id: string;
  name: string;
  slug: string;
  origin?: string;
  materialCount: number;
  logoSlug?: string;
}

export interface OfdBrandDetail {
  id: string;
  name: string;
  slug: string;
  website?: string;
  origin?: string;
  materials: OfdMaterialSummary[];
}

export interface OfdMaterialSummary {
  id: string;
  material: string;
  slug: string;
  filamentCount: number;
}

export interface OfdFlattenedEntry {
  entryId: string;
  brandName: string;
  filamentName: string;
  material: string;
  colorName: string;
  colorHex?: string;
  density?: number;
  diameter: number;
  weight: number;
  minPrintTemp?: number;
  maxPrintTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  translucent: boolean;
  glow: boolean;
  matte: boolean;
}

export interface OfdImportRequest {
  entries: OfdFlattenedEntry[];
}

export interface OfdImportResult {
  createdCount: number;
  updatedCount: number;
  errorCount: number;
  errors: string[];
}

export interface UpdateModelRequest {
  name: string;
  motionType?: MotionTypeString;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackendString;
  supportedFilamentTypeIds?: string[];

  // Capability properties (nozzle diameter and max hotend temp are now on toolheads)
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  hasCarbonFilter?: boolean;
  hasHepaFilter?: boolean;
  hasBowdenTube?: boolean;
  hasPtfeLiner?: boolean;
  hasLinearRails?: boolean;
  hasLeadScrews?: boolean;
  hasToolchanger?: boolean;
  hasFilamentCutter?: boolean;
  hasHeatedChamber?: boolean;
  multiMaterial?: boolean;
  supportsAutoLeveling?: boolean;
  maxBedTemp?: number;
  maxPrintSpeed?: number;
  defaultWattage?: number;
  defaultHourlyRate?: number;

  // Toolhead templates
  toolheads?: PrinterModelToolheadDto[];
}

export interface CreateModelRequest {
  manufacturerId: string;
  name: string;
  motionType?: MotionTypeString;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackendString;
  supportedFilamentTypeIds?: string[];

  // Capability properties (nozzle diameter and max hotend temp are now on toolheads)
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  hasCarbonFilter?: boolean;
  hasHepaFilter?: boolean;
  hasBowdenTube?: boolean;
  hasPtfeLiner?: boolean;
  hasLinearRails?: boolean;
  hasLeadScrews?: boolean;
  hasToolchanger?: boolean;
  hasFilamentCutter?: boolean;
  hasHeatedChamber?: boolean;
  multiMaterial?: boolean;
  supportsAutoLeveling?: boolean;
  maxBedTemp?: number;
  maxPrintSpeed?: number;

  // Toolhead templates
  toolheads?: PrinterModelToolheadDto[];
}

// Resolve hostname/IP utility
export interface ResolveHostnameRequest {
  hostname: string;
}

export interface ResolveHostnameResponse {
  hostname: string;
  ipAddress?: string;
  isResolved: boolean;
  error?: string;
}

// G-code file DTOs
export enum GcodeSource {
  Upload = 0,
  Harvest = 1,
}

// Full G-code library file (domain model with metadata)
export interface GcodeLibraryFile {
  id: string;
  fileName: string;
  fileSize: number;
  uploadedAt: Date;
  thumbnailUrl?: string;
  source: GcodeSource;
  sourcePrinterId?: string;
  sourcePrinterName?: string;
  originalPrinterPath?: string;
  lastSeenOnPrinter?: Date;
  description?: string;
  tags?: string[];
  requiredNozzleDiameter?: number;
  requiredMaterial?: string;
  estimatedDuration?: number;
  estimatedFilamentLength?: number;
  estimatedFilamentWeight?: number;
  layerHeight?: number;
  firstLayerTemp?: number;
  bedTemp?: number;
  slicerName?: string;
  slicerVersion?: string;
  slicerSettings?: string;
}

// G-code harvest operations
export enum GcodeHarvestStatus {
  Running = "Running",
  Completed = "Completed",
  Failed = "Failed",
  Cancelled = "Cancelled",
}

export interface HarvestOptions {
  includeSubfolders: boolean;
  fileTypes: string[];
  minFileSize: number;
  maxFileAge?: number;
  duplicateHandling: "skip" | "overwrite" | "rename";
}

export interface GcodeHarvestOperation {
  id: string;
  printerId: string;
  printerName: string;
  status: GcodeHarvestStatus;
  filesFound: number;
  filesProcessed: number; // Now calculated by backend: FilesAdded + FilesSkipped + FilesErrored
  filesAdded: number;
  filesSkipped: number;
  filesErrored: number;
  duplicatesSkipped: number;
  totalSizeBytes: number;
  startedAt: string; // API returns ISO date string
  completedAt?: string; // API returns ISO date string
  error?: string;
  errorType?: string; // ConnectionError, AuthenticationError, FileSystemError, ValidationError, UnknownError
  errorPhase?: string; // Discovery, Download, Processing, Completion
  errorDetails?: string; // JSON with exception details
  failedResource?: string; // File path or URL that caused failure
  isRetryable?: boolean; // Whether this error can be retried
  errorOccurredAt?: string; // ISO date string of when error occurred
  options?: HarvestOptions;
  filesPaths?: string[];
}

export interface HarvestProgress {
  operationId: string;
  filesProcessed: number;
  filesFound: number;
  currentFile?: string;
  phase: "discovering" | "processing" | "completing";
}

// SignalR real-time harvest update envelope
export interface HarvestUpdateDto {
  operationId: string;
  status: GcodeHarvestStatus; // mapped enum status
  filesFound: number;
  filesProcessed: number;
  filesAdded: number;
  filesSkipped: number;
  filesErrored: number;
  duplicatesSkipped?: number;
  progressPercent?: number; // convenience precomputed value (0-100)
  currentFile?: string;
  phase?: "discovering" | "processing" | "completing";
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

// SignalR job queue event payloads
export interface JobQueueUpdateDto {
  jobs: JobQueuePrintJob[];
  total: number;
  updatedAt: string; // ISO timestamp of snapshot
  // Optional granular delta info (future extension)
  addedIds?: string[];
  updatedIds?: string[];
  removedIds?: string[];
}

export interface StartBulkHarvestRequest {
  printerIds: string[];
  options: HarvestOptions;
}

// Lightweight file browser entry for hierarchical navigation
export interface GcodeFile {
  id: string; // Unique ID for the gcode file
  path: string;
  fileName: string; // GUID-based filename for internal storage
  name: string; // Original filename uploaded by user (for display)
  fileSize: number; // File size in bytes
  uploadedAt: Date; // Upload timestamp
  isDirectory: boolean;
  thumbnailUrl?: string; // URL to thumbnail image
  tags?: Array<{ id: string; name: string; color?: string; description?: string }>; // Tags applied to this gcode file
  requiredMaterial?: string; // Material required for the print
  // Extracted metadata from G-code
  extractedSlicerName?: string;
  extractedSlicerVersion?: string;
  extractedPrintTime?: number;
  extractedFilamentLength?: number;
  extractedNozzleDiameter?: number;
  extractedMaterial?: string;
  extractedPrinterModel?: string;
  extractedPrinterModelName?: string; // Raw extracted printer model name (fallback if resolution failed)
  extractedLayerHeight?: number;
  extractedInfill?: number;
  extractedPerimeters?: number;
  extractedHotendTemp?: number;
  extractedBedTemp?: number;
  // Expanded metadata fields
  totalLayers?: number;
  firstLayerHeight?: number;
  supportEnabled?: boolean;
  toolChangesCount?: number;
  objectDimensionX?: number;
  objectDimensionY?: number;
  objectDimensionZ?: number;
  objectCount?: number;
  retractionLength?: number;
  retractionSpeed?: number;
  topSolidLayers?: number;
  bottomSolidLayers?: number;
  maxVolumetricSpeed?: number;
  ironingEnabled?: boolean;
  // Multi-toolhead filament tracking
  filamentPerExtruderWeightG?: number[];
  filamentPerExtruderLengthMm?: number[];
  extruderCount?: number;
}

export interface GetGcodeFilesRequest {
  path?: string;
  harvestId?: string;
  printerId?: string;
  sortBy?: "name" | "size" | "date";
  sortOrder?: "asc" | "desc";
  search?: string;
}

export interface GetGcodeFilesResponse {
  files: GcodeFile[];
  totalFiles: number;
  totalSize: number;
  page?: number;
  pageSize?: number;
  totalPages?: number;
  totalItems?: number;
  availablePrinterModels?: Array<{ id: string | null; name: string }>;
}

// 3D Model file entry (hierarchical browser)
export interface Model3DFile {
  path: string;
  name: string;
  size: number;
  modifiedAt: Date;
  isDirectory: boolean;
  thumbnailUrl?: string;
}

export interface Model3DListResponse {
  files: Model3DFile[];
  totalFiles: number;
  totalSize: number;
  page?: number;
  pageSize?: number;
  totalPages?: number;
  totalItems?: number;
}

export interface Model3DUploadResultDto {
  id: string;
  name: string;
  fileName: string;
  fileSize: number;
  fileType: string;
  uploadedAt: string;
  url: string;
}

// G-code library runtime settings
export interface GcodeUploadSettings {
  allowedExtensions: string[];
  dailyUploadLimitBytes: number;
  userUsedBytes: number;
}

// Job queue system
export enum JobQueueStatus {
  Pending = 0,
  InProgress = 1,
  Completed = 2,
  Failed = 3,
  Cancelled = 4,
}

export interface JobQueuePrintJob {
  id: string;
  printerId: string;
  printerName?: string;
  gcodeFileId: string;
  gcodeFileName: string;
  status: JobQueueStatus;
  priority: number;
  estimatedDuration?: number;
  queuedAt: Date;
  startedAt?: Date;
  completedAt?: Date;
  failureReason?: string;
  copies: number;
  completedCopies: number;
  remainingCopies: number;
  projectFileId?: string;
  createdAt: Date;
  updatedAt: Date;
  /** Per-toolhead filament usage tracking */
  toolheadUsages?: PrintJobToolheadUsage[];
}

/**
 * Queue overview DTO - provides printer availability and queue status
 * Used for displaying available printers when queueing a print job
 */
export interface QueueOverviewDto {
  printerId: string;
  printerName: string;
  printerModel: string;
  /** Slicer-specific model names that map to this printer's model (e.g., "COREONEL", "MK4IS") */
  modelAliases?: string[];
  isAvailable: boolean;
  queuedJobsCount: number;
  currentJobId?: string;
  currentJobName?: string;
  estimatedCompletionTime?: string;
  nozzleDiameter?: number;
  supportedMaterials?: string[];
}

// API response types
export interface ApiResponse<T> {
  data: T;
  success: boolean;
  message?: string;
}

export interface PagedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// Error response
export interface ApiError {
  message: string;
  details?: string;
  statusCode: number;
}

// Authentication types
export interface LoginRequest {
  // Accept either `username` (legacy) or `usernameOrEmail` (backend contract)
  username?: string;
  usernameOrEmail?: string;
  password: string;
  rememberMe?: boolean;
}

export interface RegisterRequest {
  username: string;
  email: string;
  password: string;
  firstName?: string;
  lastName?: string;
}

export interface AuthenticationResult {
  success: boolean;
  token?: string;
  expiresAt?: Date;
  user?: UserDto;
  error?: string;
}

export interface UserDto {
  id: string;
  username: string;
  email: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  emailConfirmed: boolean;
  lastLogin?: Date;
  createdAt: Date;
  roles: string[];
  permissions: string[];
}

export interface ForgotPasswordRequest {
  email: string;
}

export interface ForgotPasswordResponse {
  success: boolean;
  message: string;
}

export interface ResetPasswordRequest {
  token: string;
  email: string;
  newPassword: string;
  confirmPassword: string;
}

export interface ResetPasswordResponse {
  success: boolean;
  message: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
  confirmNewPassword: string;
}

export interface DiscoveredPrinterDto {
  ipAddress: string;
  backendPort?: number | null;
  frontendPort?: number | null;
  serverUrl: string;
  backend: PrinterBackend;
  name: string;
  manufacturer?: string;
  model?: string;
  firmware?: string;
  version?: string;
  cameraStreamUrl?: string | null;
  cameraSnapshotUrl?: string | null;
}

// Network discovery settings
export interface NetworkDiscoverySettingsDto {
  networkRanges: string[];
  timeoutMs: number;
  maxConcurrentScans: number;
  ports: number[];
  backends?: PrinterBackend[];
}

// Discovery streaming types
export enum DiscoveryStatus {
  Starting = "Starting",
  Scanning = "Scanning",
  Completed = "Completed",
  Cancelled = "Cancelled",
  Error = "Error",
}

export interface StartDiscoveryRequest {
  backends?: PrinterBackend[];
  autoRegister?: boolean;
}

export interface DiscoveryProgressDto {
  sessionId: string;
  currentNetwork: string;
  currentIp: string;
  totalIps: number;
  scannedIps: number;
  printersFound: number;
  printersExcluded: number;
  progressPercentage: number;
  status: DiscoveryStatus;
  message?: string;
  networkRanges?: string[];
  autoDetectedNetworks?: boolean;
}

export interface DiscoveryPrinterFoundDto {
  sessionId: string;
  printer: DiscoveredPrinterDto;
}

export interface DiscoveryCompletedDto {
  sessionId: string;
  totalPrintersFound: number;
  totalPrintersExcluded: number;
  duration: number; // milliseconds
  wasCancelled?: boolean;
  networkRanges?: string[];
  autoDetectedNetworks?: boolean;
}

// Dispatch upload progress (SignalR)
export interface DispatchUploadProgressDto {
  jobId: string;
  printerId: string;
  fileName: string;
  bytesSent: number;
  totalBytes: number;
  percentage: number;
  isCompleted: boolean;
  isFailed?: boolean;
  /** Current stage of the upload-and-print workflow. */
  stage?: string;
  /** Error message when isFailed is true. */
  errorMessage?: string;
}

// Printer control types
export interface MoveRequest {
  x?: number;
  y?: number;
  z?: number;
  f?: number;
}

export interface CommandResult {
  success: boolean;
  error?: string;
  message?: string;
}

/** Request payload for saving a calibrated Z-offset. */
export interface ZOffsetSaveRequest {
  /** The Z-offset value in millimeters. Negative values move the nozzle closer to the bed. */
  offsetMm: number;
  /** Whether to also send save commands to the printer firmware. */
  saveToFirmware?: boolean;
}

// Failure detail for an individual file during multi-upload.
export interface MultiUploadFailure {
  fileName: string;
  error: string;
}

// Response for multi-file upload endpoint.
export interface MultiUploadResponse {
  created: GcodeLibraryFile[];
  failed: MultiUploadFailure[];
  succeededCount: number;
  failedCount: number;
}

// Printer History Types
export interface HistoryListResponse {
  count: number;
  jobs: HistoryJob[];
}

export interface HistoryJob {
  jobId: string;
  exists: boolean;
  endTime?: number;
  filamentUsed: number;
  filename: string;
  metadata: Record<string, unknown>;
  printDuration: number;
  status: string;
  startTime: number;
  totalDuration: number;
  user: string;
  auxiliaryData?: AuxiliaryData[];
  thumbnailUrl?: string;
}

export interface AuxiliaryData {
  provider: string;
  name: string;
  value: unknown;
  description: string;
  units?: string;
}

export interface HistoryTotals {
  jobTotals: JobTotals;
  auxiliaryTotals?: AuxiliaryTotals[];
}

export interface JobTotals {
  totalJobs: number;
  totalPrintTime: number;
  totalFilament: number;
  longestJob: number;
  longestPrint: number;
}

export interface AuxiliaryTotals {
  provider: string;
  name: string;
  totalValue: number;
  description: string;
  units?: string;
}

// File Consistency Types
export enum FileHealthStatus {
  Unknown = 0,
  Healthy = 1,
  Missing = 2,
  Corrupted = 3,
  Inaccessible = 4,
}

export enum FileAuditType {
  Model3D = 0,
  GcodeFile = 1,
  OrphanedFiles = 2,
  FullAudit = 3,
}

export interface FileHealthSummaryDto {
  totalModel3DFiles: number;
  model3DHealthy: number;
  model3DMissing: number;
  model3DCorrupted: number;
  totalGcodeFiles: number;
  gcodeHealthy: number;
  gcodeMissing: number;
  gcodeCorrupted: number;
  lastHealthyAuditDate?: string;
  overallHealthPercentage: number;
}

export interface FileHealthAuditDto {
  auditId: string;
  auditDate: string;
  auditType: FileAuditType;
  filesChecked: number;
  validCount: number;
  missingCount: number;
  corruptedCount: number;
  orphanedCount: number;
  hasIssues: boolean;
  summaryMessage: string;
  missingFileIds?: string[];
  corruptedFileIds?: string[];
  orphanedPaths?: string[];
}

export interface FileIssuesSummaryDto {
  missingFiles: Array<{
    id: string;
    fileName: string;
    fileType: string;
    lastHealthCheckDate?: string;
  }>;
  corruptedFiles: Array<{
    id: string;
    fileName: string;
    fileType: string;
    lastVerificationResult?: string;
  }>;
  inaccessibleFiles: Array<{
    id: string;
    fileName: string;
    fileType: string;
    lastHealthCheckDate?: string;
  }>;
  totalIssues: number;
}

export interface FileHealthDetailDto {
  fileId: string;
  fileName: string;
  fileType: string; // "Model3D" or "GcodeFile"
  filePath?: string;
  fileSize?: number;
  currentHealthStatus: FileHealthStatus;
  lastHealthCheckDate?: string;
  lastVerificationResult?: string;
  verificationHistory: Array<{
    date: string;
    status: FileHealthStatus;
    details?: string;
  }>;
}

// GCode Upload Progress - emitted via SignalR during multi-file uploads
export interface GcodeUploadFailureSummary {
  fileName: string;
  error: string;
}

export interface GcodeUploadProgressDto {
  sessionId: string;
  totalFiles: number;
  processedCount: number;
  currentFileName?: string | null;
  successfulFiles?: string[] | null;
  failedFiles?: GcodeUploadFailureSummary[] | null;
  errorMessage?: string | null;
}

// ============= PRINT QUEUE TYPES =============

export interface QueuedPrintJobDto {
  id: string;
  name: string;
  gcodeFileId: string;
  fileName?: string; // Original G-code filename for display
  assignedPrinterId?: string;
  status: string;
  priority: number;
  queuePosition: number;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
  requiredCapabilities?: string[];
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  actualStartTimeUtc?: string;
  actualEndTimeUtc?: string;
  actualPrintTimeSeconds?: number;
  actualFilamentUsageGrams?: number;
  failureReason?: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  queuedAtUtc: string;
  wasSeededFromHistory?: boolean;
  notes?: string;
  tags?: string[];
  projectId?: string;
  projectName?: string;
  /** Spoolman filament ID */
  spoolmanFilamentId?: number;
  /** Filament name from Spoolman */
  filamentName?: string;
  /** Filament vendor from Spoolman */
  filamentVendor?: string;
  /** Filament color hex from Spoolman */
  filamentColor?: string;
  /** Number of copies to print */
  copies: number;
  /** Number of copies completed so far */
  completedCopies: number;
  /** Remaining copies (copies - completedCopies) */
  remainingCopies: number;
  /** Link to project file this job was created from */
  projectFileId?: string;
  /** Estimated cost of the print job */
  estimatedCost?: number;
  /** Actual cost of the print job */
  actualCost?: number;
  /** URL to the G-code file thumbnail image */
  thumbnailUrl?: string;
  /** Per-toolhead filament usage tracking */
  toolheadUsages?: PrintJobToolheadUsage[];
}

export interface QueueGcodeFileMetaDto {
  id: string;
  name: string; // Original filename for display
  fileName: string; // GUID-based filename on disk
  fileSizeBytes: number;
  materialType?: string;
  nozzleDiameter?: number;
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  createdAtUtc: string;
  thumbnailUrl?: string;
}

export interface QueuePrinterMetaDto {
  id: string;
  name: string;
  modelName: string;
  status: string;
  isOnline: boolean;
}

export interface QueuedPrintJobWithFileMetaDto {
  job: QueuedPrintJobDto;
  gcodeFile?: QueueGcodeFileMetaDto;
  assignedPrinter?: QueuePrinterMetaDto;
  estimatedStartTime?: string;
  estimatedCompletionTime?: string;
}

export interface QueueStatsDto {
  totalQueued: number;
  totalPrinting: number;
  totalPaused: number;
  averageWaitTimeMinutes: number;
  byModel: Record<string, QueuePrinterModelStatsDto>;
}

export interface QueuePrinterModelStatsDto {
  modelName: string;
  totalQueued: number;
  currentlyPrinting: number;
  oldestQueuedAtUtc?: string;
  averageQueueWaitMinutes: number;
}

export interface QueueHistoryPageDto {
  entries: QueueHistoryEntryDto[];
  totalCount: number;
  currentPage: number;
  pageSize: number;
  stats: QueueHistoryStatsDto;
}

export interface QueueHistoryStatsDto {
  totalCompleted: number;
  totalFailed: number;
  totalCancelled: number;
  successRate: number;
  averageDurationMinutes: number;
  totalPrintTimeMinutes: number;
}

export interface QueueHistoryEntryDto {
  id: string;
  jobName: string;
  printerName: string;
  status: string;
  completionPercentage: number;
  startedAtUtc: string;
  completedAtUtc?: string;
  actualPrintTimeSeconds: number;
  failureReason?: string;
  toolheadUsages?: PrintJobToolheadUsage[];
}

export interface TimelineEventDto {
  jobId: string;
  jobName: string;
  printerName: string;
  state: string;
  enteredAtUtc: string;
  exitedAtUtc?: string;
  durationSeconds?: number;
  estimatedDurationSeconds?: number;
  variancePercent?: number;
}

export interface StateTransitionDto {
  fromState: string;
  toState: string;
  transitionedAtUtc: string;
  durationInStateSeconds?: number;
  notes?: string;
}

export interface JobStateHistoryDto {
  jobId: string;
  jobName: string;
  transitions: StateTransitionDto[];
  totalDurationSeconds?: number;
  estimatedDurationSeconds?: number;
  variancePercent?: number;
}

export interface DurationStatsDto {
  printerId: string;
  printerName: string;
  totalJobs: number;
  averageEstimatedSeconds?: number;
  averageActualSeconds?: number;
  accuracyPercent?: number;
  variancePercent?: number;
  minActualSeconds?: number;
  maxActualSeconds?: number;
}

export interface DurationAnalyticsDto {
  totalJobs: number;
  averageEstimatedSeconds?: number;
  averageActualSeconds?: number;
  overallAccuracyPercent?: number;
  overallVariancePercent?: number;
  byPrinter: Record<string, DurationStatsDto>;
  topPerformers: DurationStatsDto[];
  needsAttention: DurationStatsDto[];
}

export interface EnqueueQueueJobRequest {
  gcodeFileId: string;
  priority?: number;
  assignedPrinterId?: string;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
}

export interface UpdateQueueJobRequest {
  priority?: number;
  assignedPrinterId?: string;
  status?: string;
  failureReason?: string;
}

export interface BulkCancelQueueJobsRequest {
  jobIds: string[];
}

export interface QueueBulkOperationResultDto {
  totalRequested: number;
  successfulCount: number;
  failedCount: number;
  failures: QueueOperationFailureDto[];
  completedAtUtc: string;
}

export interface QueueOperationFailureDto {
  itemId: string;
  errorMessage: string;
  errorCode?: string;
}

// Background Service Status Types
export interface BackgroundServiceStatus {
  serviceId: string;
  displayName: string;
  description?: string;
  category?: string;
  icon?: string;
  isRunning: boolean;
  isEnabled: boolean;
  lastRunTime?: string;
  nextRunTime?: string;
  lastError?: string;
  lastErrorTime?: string;
  successfulRuns: number;
  failedRuns: number;
  intervalSeconds?: number;
}

export interface CategorySummary {
  total: number;
  running: number;
  withErrors: number;
}

export interface BackgroundServicesSummary {
  totalServices: number;
  runningServices: number;
  enabledServices: number;
  disabledServices: number;
  servicesWithErrors: number;
  byCategory: Record<string, CategorySummary>;
}

// Camera Types - for standalone webcam management

export enum CameraSource {
  Standalone = 'Standalone',
  Moonraker = 'Moonraker',
  PrusaLink = 'PrusaLink',
  OctoPrint = 'OctoPrint',
  SDCP = 'SDCP',
  FlashForge = 'FlashForge',
}

export enum CameraType {
  General = 'General',
  Bed = 'Bed',
  Nozzle = 'Nozzle',
  Wide = 'Wide',
  Timelapse = 'Timelapse',
}

export enum CameraHealthStatus {
  Unknown = 'Unknown',
  Healthy = 'Healthy',
  Degraded = 'Degraded',
  Unhealthy = 'Unhealthy',
}

export interface CameraDto {
  id: string;
  name: string;
  description?: string;
  streamUrl?: string;
  snapshotUrl?: string;
  isEnabled: boolean;
  sortOrder: number;
  location?: string;
  createdAt: string;
  updatedAt?: string;
  isStandalone: boolean;
  printerId?: string;
  source: CameraSource;
  cameraType: CameraType;
  healthStatus: CameraHealthStatus;
  lastHealthCheck?: string;
}

export interface CreateCameraDto {
  name: string;
  description?: string;
  streamUrl?: string;
  snapshotUrl?: string;
  isEnabled?: boolean;
  sortOrder?: number;
  location?: string;
  printerId?: string;
  source?: CameraSource;
  cameraType?: CameraType;
}

export interface UpdateCameraDto {
  name?: string;
  description?: string;
  streamUrl?: string;
  snapshotUrl?: string;
  isEnabled?: boolean;
  sortOrder?: number;
  location?: string;
  printerId?: string | null;
  source?: CameraSource;
  cameraType?: CameraType;
}

export interface ToggleCameraDto {
  isEnabled: boolean;
}

// Combined camera view - shows both standalone and printer-attached cameras
export interface DisplayCameraDto {
  id: string;
  name: string;
  description?: string;
  streamUrl?: string;
  snapshotUrl?: string;
  isEnabled: boolean;
  sortOrder: number;
  location?: string;
  isStandalone: boolean;
  printerId?: string;
  printerName?: string;
  printerState?: string;
  isPrinterOnline?: boolean;
  source: CameraSource;
  cameraType: CameraType;
  healthStatus: CameraHealthStatus;
  lastHealthCheck?: string;
  healthMessage?: string;
}

// ============== Print Project Types ==============

/**
 * Status of a print project
 */
export enum PrintProjectStatus {
  Open = 'Open',
  InProgress = 'InProgress',
  Completed = 'Completed',
  Cancelled = 'Cancelled',
  OnHold = 'OnHold'
}

/**
 * Color requirement for a file in a project
 */
export enum PrintColorRequirement {
  Base = 'Base',
  Accent = 'Accent',
  Custom = 'Custom'
}

/**
 * Status of a file within a project
 */
export enum PrintProjectFileStatus {
  Pending = 'Pending',
  Printing = 'Printing',
  Completed = 'Completed',
  Skipped = 'Skipped'
}

/**
 * Summary DTO for displaying projects in lists
 */
export interface PrintProjectListDto {
  id: string;
  name: string;
  description?: string;
  status: PrintProjectStatus;
  priority: number;
  dueDate?: string;
  totalFiles: number;
  completedFiles: number;
  totalPrints: number;
  completedPrints: number;
  estimatedTotalCost?: number | null;
  completedCost?: number | null;
  createdAt: string;
  completedAt?: string;
  progressPercent: number;
}

/**
 * Detailed DTO for single project view with all files
 */
export interface PrintProjectDetailDto {
  id: string;
  name: string;
  description?: string;
  status: PrintProjectStatus;
  priority: number;
  dueDate?: string;
  notes?: string;
  createdAt: string;
  updatedAt: string;
  completedAt?: string;
  files: PrintProjectFileDto[];
  totalPrints: number;
  completedPrints: number;
  progressPercent: number;
  estimatedTotalCost?: number | null;
  completedCost?: number | null;
}

/**
 * DTO for a file within a project
 */
export interface PrintProjectFileDto {
  id: string;
  gcodeFileId: string;
  fileName: string;
  thumbnailUrl?: string;
  spoolmanFilamentId?: number | null;
  materialRequirement?: string;
  printCount: number;
  printedCount: number;
  status: PrintProjectFileStatus;
  sortOrder: number;
  notes?: string;
  lastPrintedAt?: string;
  lastPrintJobId?: string;
  isComplete: boolean;
  remainingPrints: number;
  // Gcode metadata for time/material estimation
  estimatedPrintTimeMinutes?: number | null;
  estimatedFilamentLengthMm?: number | null;
  estimatedFilamentWeightG?: number | null;
  requiredMaterial?: string | null;
  requiredNozzleDiameter?: number | null;
  extractedPrinterModelName?: string | null;
  remainingPrintTimeMinutes?: number | null;
  estimatedCostPerCopy?: number | null;
  estimatedFileCost?: number | null;
}

/**
 * Request to create a new print project
 */
export interface CreatePrintProjectRequest {
  name: string;
  description?: string;
  priority?: number;
  dueDate?: string;
  notes?: string;
  files?: AddFileToProjectRequest[];
}

/**
 * Request to update an existing print project
 */
export interface UpdatePrintProjectRequest {
  name?: string;
  description?: string;
  status?: PrintProjectStatus;
  priority?: number;
  dueDate?: string;
  notes?: string;
}

/**
 * Request to add a file to a project
 */
export interface AddFileToProjectRequest {
  gcodeFileId: string;
  spoolmanFilamentId?: number | null;
  materialRequirement?: string;
  printCount?: number;
  notes?: string;
}

/**
 * Request to update a file within a project
 */
export interface UpdateProjectFileRequest {
  spoolmanFilamentId?: number | null;
  materialRequirement?: string;
  printCount?: number;
  printedCount?: number;
  status?: PrintProjectFileStatus;
  sortOrder?: number;
  notes?: string;
}

/**
 * Progress summary for a project
 */
export interface PrintProjectProgressDto {
  projectId: string;
  projectName: string;
  status: PrintProjectStatus;
  totalFiles: number;
  completedFiles: number;
  totalPrints: number;
  completedPrints: number;
  progressPercent: number;
  fileProgress: FileProgressDto[];
}

/**
 * Progress summary for a single file within a project
 */
export interface FileProgressDto {
  fileId: string;
  fileName: string;
  status: PrintProjectFileStatus;
  printCount: number;
  printedCount: number;
  isComplete: boolean;
}

/**
 * Request to queue all pending files from a project to the job queue
 */
export interface QueueProjectRequest {
  assignedPrinterId?: string | null;
  groupByMaterial?: boolean;
  groupByColor?: boolean;
  priority?: number;
}

/**
 * Result of queueing a project's files
 */
export interface QueueProjectResultDto {
  projectId: string;
  projectName: string;
  totalJobsQueued: number;
  totalPrintsQueued: number;
  estimatedTotalTimeMinutes?: number | null;
  queuedFiles: QueuedProjectFileDto[];
}

/**
 * A single file that was queued from a project
 */
export interface QueuedProjectFileDto {
  projectFileId: string;
  printJobId: string;
  fileName: string;
  materialType?: string | null;
  colorHex?: string | null;
  printCount: number;
  estimatedPrintTimeMinutes?: number | null;
  queueOrder: number;
}

// Print Project Templates
export interface PrintProjectTemplateListDto {
  id: string;
  name: string;
  description: string | null;
  category: string | null;
  fileCount: number;
  totalPrintCount: number;
  isSystemTemplate: boolean;
  sortOrder: number;
}

export interface PrintProjectTemplateDetailDto {
  id: string;
  name: string;
  description: string | null;
  category: string | null;
  defaultPriority: number;
  defaultNotes: string | null;
  isSystemTemplate: boolean;
  sortOrder: number;
  files: PrintProjectTemplateFileDto[];
  createdAt: string;
  updatedAt: string;
}

export interface PrintProjectTemplateFileDto {
  id: string;
  name: string;
  fileNamePattern: string | null;
  colorRequirement: PrintColorRequirement;
  materialRequirement: string | null;
  printCount: number;
  sortOrder: number;
  notes: string | null;
}

export interface CreatePrintProjectTemplateRequest {
  name: string;
  description?: string;
  category?: string;
  defaultPriority?: number;
  defaultNotes?: string;
  files?: CreateTemplateFileRequest[];
}

export interface CreateTemplateFileRequest {
  name: string;
  fileNamePattern?: string;
  colorRequirement?: PrintColorRequirement;
  materialRequirement?: string;
  printCount?: number;
  notes?: string;
}

/**
 * Spoolman spool (matches backend SpoolmanSpoolDto serialized with camelCase)
 */
export interface SpoolmanSpool {
  id: number;
  name: string;
  material: string;
  remainingWeightG?: number | null;
  colorHex?: string | null;
  inUse: boolean;
  filamentName?: string | null;
  vendor?: string | null;
  registeredAt?: string | null;
  firstUsedAt?: string | null;
  lastUsedAt?: string | null;
  initialWeightG?: number | null;
  usedWeightG?: number | null;
  spoolWeightG?: number | null;
  remainingLengthMm?: number | null;
  usedLengthMm?: number | null;
  location?: string | null;
  lotNumber?: string | null;
  archived?: boolean | null;
  usedPercent?: number | null;
  remainingPercent?: number | null;
  price?: number | null;
  comment?: string | null;
}

/**
 * Spoolman filament type/product definition (matches backend SpoolmanFilamentDto).
 * Represents the filament product class (e.g., "PolyTerra PLA Charcoal Black"),
 * not a physical spool instance.
 */
export interface SpoolmanFilament {
  id: number;
  name?: string | null;
  material?: string | null;
  colorHex?: string | null;
  vendor?: string | null;
  density?: number | null;
  diameter?: number | null;
  weight?: number | null;
  spoolWeight?: number | null;
  price?: number | null;
  settingsExtruderTemp?: number | null;
  settingsBedTemp?: number | null;
  articleNumber?: string | null;
  comment?: string | null;
  multiColorHexes?: string | null;
  externalId?: string | null;
}

/**
 * Spoolman vendor record.
 */
export interface SpoolmanVendor {
  id: number;
  name: string;
  externalId?: string | null;
}

/**
 * Spoolman material definition (e.g. PLA, PETG, ASA).
 */
export interface SpoolmanMaterial {
  id: number;
  name: string;
  density?: number | null;
  colorHex?: string | null;
}

/**
 * Request to bulk-update a set of filaments in Spoolman.
 * Only non-null/undefined fields are applied to each filament.
 */
export interface SpoolmanBulkUpdateFilamentsRequest {
  filamentIds: number[];
  vendorId?: number | null;
  material?: string | null;
  price?: number | null;
  settingsExtruderTemp?: number | null;
  settingsBedTemp?: number | null;
  comment?: string | null;
}

/**
 * Result of a bulk filament update.
 */
export interface SpoolmanBulkUpdateResult {
  updatedCount: number;
  errorCount: number;
  errors: string[];
}

/**
 * Request to bulk-delete filaments from Spoolman.
 */
export interface SpoolmanBulkDeleteRequest {
  filamentIds: number[];
}

/**
 * Request to update (PATCH) a single filament in Spoolman.
 * Only non-null/undefined fields are applied.
 */
export interface SpoolmanUpdateFilamentRequest {
  name?: string | null;
  vendorId?: number | null;
  material?: string | null;
  density?: number | null;
  diameter?: number | null;
  weight?: number | null;
  spoolWeight?: number | null;
  settingsExtruderTemp?: number | null;
  settingsBedTemp?: number | null;
  colorHex?: string | null;
  externalId?: string | null;
  comment?: string | null;
  price?: number | null;
  articleNumber?: string | null;
  multiColorHexes?: string | null;
}

// ============ Spool CRUD Types ============

/**
 * Request to create or update (PATCH) a single spool in Spoolman.
 * Only non-null/undefined fields are applied.
 */
export interface SpoolmanUpdateSpoolRequest {
  filamentId?: number | null;
  remainingWeight?: number | null;
  initialWeight?: number | null;
  spoolWeight?: number | null;
  location?: string | null;
  lotNumber?: string | null;
  price?: number | null;
  comment?: string | null;
  archived?: boolean | null;
}

/** Alias for clarity: same shape as update but filamentId is required for create. */
export type SpoolmanCreateSpoolRequest = SpoolmanUpdateSpoolRequest;

/**
 * Request to bulk-update multiple spools in Spoolman.
 */
export interface SpoolmanBulkUpdateSpoolsRequest {
  spoolIds: number[];
  location?: string | null;
  lotNumber?: string | null;
  price?: number | null;
  comment?: string | null;
  archived?: boolean | null;
}

// ============ Connection Diagnostics Types ============

export type PrinterConnectionState = 'Connected' | 'Reconnecting' | 'Offline' | 'Degraded';

export interface ConnectionStateTransition {
  timestampUtc: string;
  fromState: PrinterConnectionState;
  toState: PrinterConnectionState;
  reason: string | null;
}

export interface PrinterConnectionHealthDto {
  printerId: string;
  printerName: string;
  backend: string;
  connectionState: PrinterConnectionState;
  lastConnectedUtc: string | null;
  lastDisconnectedUtc: string | null;
  reconnectAttempts: number;
  totalReconnects: number;
  consecutiveFailures: number;
  uptimePercent: number;
  connectionMode: string | null;
  recentTransitions: ConnectionStateTransition[];
}

export interface ConnectionDiagnosticsResponse {
  printers: PrinterConnectionHealthDto[];
  totalPrinters: number;
  connectedCount: number;
  reconnectingCount: number;
  offlineCount: number;
  degradedCount: number;
  timestampUtc: string;
}

// ============================================================================
// NFC Devices
// ============================================================================

export interface NfcDeviceDto {
  id: string;
  name: string;
  ipAddress?: string;
  printerId?: string;
  printerName?: string;
  firmwareVersion?: string;
  wifiRssi?: number;
  nfcReaderOk: boolean;
  freeHeap?: number;
  isOnline: boolean;
  lastHeartbeat?: string;
  lastScanAt?: string;
  lastScannedSpoolId?: number;
  createdAt: string;
  updatedAt?: string;
}

export interface CreateNfcDeviceDto {
  name: string;
  ipAddress?: string;
  printerId?: string;
  firmwareVersion?: string;
}

export interface UpdateNfcDeviceDto {
  name?: string;
  printerId?: string;
}

export interface NfcScanHistoryDto {
  id: string;
  nfcDeviceId: string;
  deviceName?: string;
  spoolId?: number;
  tagFormat: string;
  materialType?: string;
  brandName?: string;
  action?: string;
  scannedAt: string;
}

// Monitoring
export interface MonitoringServiceStatus {
  available: boolean;
  url?: string;
  error?: string;
}

export interface MonitoringStatusDto {
  grafana: MonitoringServiceStatus;
  jaeger: MonitoringServiceStatus;
  prometheus: MonitoringServiceStatus;
}

export interface FailureDetectionPrinterStatusDto {
  printerId: string;
  printerName: string;
  state: string;
  reason: string;
  isPrinting: boolean;
  jobName?: string;
  fileName?: string;
  detectionSource: string;
  detectionTarget?: string;
  snapshotUrl?: string;
  lastAnalyzedAt?: string;
  lastOutcome: string;
  lastConfidence?: number;
  lastAutoPaused?: boolean;
  lastFailureDetectedAt?: string;
}

export interface FailureDetectionMonitorStatusDto {
  monitoringEnabled: boolean;
  confidenceThreshold: number;
  scanIntervalSeconds: number;
  autoPauseOnFailure: boolean;
  configuredPrinterCount: number;
  activelyMonitoredPrinterCount: number;
  lastAnalyzedPrinterCount: number;
  lastFailureCount: number;
  lastScanStartedAt?: string;
  lastScanCompletedAt?: string;
  lastError?: string;
  printers: FailureDetectionPrinterStatusDto[];
}

export interface MonitoringMetricsSummaryDto {
  requestsPerSecond: number;
  apiCallsLast24h: number;
  topEndpointName: string;
  topEndpointRequestsPerSecond: number;
  errorRatePercent: number;
  clientErrorRatePercent: number;
  p95LatencyMs: number;
  p99LatencyMs: number;
  memoryUsageMb: number;
  activePrinters: number;
  printerSuccessRatePercent: number;
  fileOperationsLast24h: number;
  averageFileSizeMbLast24h: number;
  databaseOperationsLast24h: number;
  slicerJobsLast24h: number;
  slicerSuccessRatePercent: number;
  failureDetectionConfiguredPrinters: number;
  timestamp: string;
}

// ── Webhooks ──────────────────────────────────────────────────

export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  hasSecret: boolean;
  eventTypes: string;
  isActive: boolean;
  consecutiveFailures: number;
  maxConsecutiveFailures: number;
  createdAt: string;
  lastDeliveryAt?: string;
  lastSuccessAt?: string;
}

export interface CreateWebhookDto {
  name: string;
  url: string;
  secret?: string;
  eventTypes?: string;
  isActive?: boolean;
  maxConsecutiveFailures?: number;
}

export interface UpdateWebhookDto {
  name?: string;
  url?: string;
  secret?: string;
  eventTypes?: string;
  isActive?: boolean;
  maxConsecutiveFailures?: number;
}

export interface WebhookDelivery {
  id: string;
  eventType: string;
  statusCode?: number;
  success: boolean;
  errorMessage?: string;
  attempt: number;
  durationMs?: number;
  createdAt: string;
}

// ── Auto-Dispatch ──────────────────────────────────────────────

export type AutoDispatchState = 'None' | 'PendingReady' | 'Ready';

export interface AutoDispatchStatus {
  printerId: string;
  enabled: boolean;
  state: AutoDispatchState;
  queueDepth: number;
  printerName?: string;
  isReady?: boolean;
  currentJobName?: string;
  lastActivity?: string;
  bedPreConfirmed?: boolean;
  readyGateChecks?: ReadyGateCheck[];
  attentionMessage?: string;
  attentionReason?: string;
  operatorAction?: string;
}

export interface AutoDispatchNextJob {
  id: string;
  name: string;
  estimatedFilamentUsageG?: number;
  requiredMaterialType?: string;
  estimatedPrintTime?: string;
}

export interface FilamentCheckResult {
  sufficient: boolean;
  remainingWeightG?: number;
  requiredWeightG?: number;
  loadedMaterial?: string;
  requiredMaterial?: string;
  materialMismatch: boolean;
  message?: string;
}

export interface AutoDispatchReadyResult {
  status: AutoDispatchStatus;
  nextJob?: AutoDispatchNextJob;
  filamentCheck?: FilamentCheckResult;
}

// ── Printer Groups ──────────────────────────────────────────────

/**
 * Basic printer group DTO with printer count
 */
export interface PrinterGroup {
  id: string;
  name: string;
  description?: string;
  createdDate: string;
  updatedDate: string;
  printerCount: number;
}

/**
 * Detailed printer group DTO with assigned printers
 */
export interface PrinterGroupDetail {
  id: string;
  name: string;
  description?: string;
  createdDate: string;
  updatedDate: string;
  printers: PrinterGroupPrinter[];
}

/**
 * Printer DTO within a printer group
 */
export interface PrinterGroupPrinter {
  id: string;
  name: string;
  backend: PrinterBackend;
  isAvailable: boolean;
  inMaintenance: boolean;
}

/**
 * Request DTO for creating a printer group
 */
export interface CreatePrinterGroupRequest {
  name: string;
  description?: string;
}

/**
 * Request DTO for updating a printer group
 */
export interface UpdatePrinterGroupRequest {
  name: string;
  description?: string;
}

/**
 * System platform capabilities — reports which features are available
 * on the current hardware (e.g. ARM/Raspberry Pi may disable slicing).
 */
export interface SystemCapabilities {
  architecture: string;
  slicingEnabled: boolean;
  modelFilesEnabled: boolean;
  thumbnailGenerationEnabled: boolean;
  gcodeUploadEnabled: boolean;
  platformNote?: string;
}

// ============ Dispatch History Types ============

export interface DispatchHistoryDto {
  id: string;
  printJobId: string;
  jobName?: string;
  printerId: string;
  printerName?: string;
  action: string; // "Suggested" | "Dispatched" | "Rejected" | "Failed"
  score?: number;
  reason?: string;
  createdAtUtc: string;
}

export interface DispatchHistoryPageDto {
  items: DispatchHistoryDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// ============ Notification Types ============

export enum NotificationType {
  JobStarted = 'JobStarted',
  JobCompleted = 'JobCompleted',
  JobFailed = 'JobFailed',
  JobPaused = 'JobPaused',
  JobResumed = 'JobResumed',
  QueueAlert = 'QueueAlert',
  SystemAlert = 'SystemAlert'
}

export enum NotificationFrequency {
  RealTime = 'RealTime',
  Hourly = 'Hourly',
  Daily = 'Daily',
  Weekly = 'Weekly',
  Never = 'Never'
}

export interface NotificationDto {
  id: string;
  userId: string;
  jobId?: string;
  type: NotificationType;
  subject: string;
  body: string;
  isRead: boolean;
  createdAt: string;
  readAt?: string;
  expiresAt?: string;
}

export interface NotificationPreferencesDto {
  userId: string;
  enableEmailNotifications: boolean;
  enablePushNotifications: boolean;
  enableInAppNotifications: boolean;
  notifyOnCompletion: boolean;
  notifyOnFailure: boolean;
  notifyOnStart: boolean;
  notifyOnPause: boolean;
  frequency: NotificationFrequency;
  retentionDays: number;
}

export interface UpdateNotificationPreferencesRequest {
  enableEmailNotifications: boolean;
  enablePushNotifications: boolean;
  enableInAppNotifications: boolean;
  notifyOnCompletion: boolean;
  notifyOnFailure: boolean;
  notifyOnStart: boolean;
  notifyOnPause: boolean;
  frequency: NotificationFrequency;
  retentionDays?: number;
}

export interface UnreadCountResponse {
  unreadCount: number;
}

// ============== Job Scheduling ==============

export type RecurrenceType = 'once' | 'daily' | 'weekly' | 'monthly';
export type ScheduleStatus = 'active' | 'paused' | 'completed' | 'cancelled';

export interface ScheduledJob {
  id: string;
  jobId: string;
  jobName: string;
  printerName: string;
  printerId: string;
  scheduledTime: string;
  recurrence?: RecurrenceType;
  recurrenceInterval?: number;
  status: ScheduleStatus;
  nextExecution?: string;
  lastExecution?: string;
  createdAt: string;
}

export interface JobExecution {
  id: string;
  scheduledJobId: string;
  startedAt: string;
  completedAt?: string;
  status: string;
  errorMessage?: string;
}

export interface ScheduleJobRequest {
  scheduledTime: string;
  timezone: string;
  recurrenceType: RecurrenceType;
  recurrenceInterval?: number;
}

export interface MarkMultipleAsReadRequest {
  notificationIds: string[];
}

// ============ Cost Tracking Types ============

/** Settings that control how print job costs are calculated. */
export interface CostTrackingSettings {
  enableAutomaticCostCalculation: boolean;
  electricityRatePerKwh: number;
  averagePrinterWattage: number;
  defaultMachineHourlyRate: number;
  laborMarkupPercent: number;
  profitMarginTargetPercent: number;
}

export interface CostSummary {
  totalCostUsd: number;
  averageCostPerJobUsd: number;
  jobsWithCostData: number;
  totalMaterialCostUsd: number;
  totalEnergyCostUsd: number;
  totalMachineTimeCostUsd: number;
  totalLaborCostUsd: number;
  mostExpensiveMaterial?: string;
  mostExpensiveMaterialCost: number;
}

export interface CostByPrinter {
  printerId: string;
  printerName: string;
  totalCostUsd: number;
  averageCostPerJobUsd: number;
  jobCount: number;
  materialCostUsd: number;
  energyCostUsd: number;
  machineTimeCostUsd: number;
  laborCostUsd: number;
}

export interface CostByMaterial {
  materialType: string;
  totalCostUsd: number;
  averageCostPerJobUsd: number;
  jobCount: number;
  totalFilamentUsageGrams: number;
}

export interface CostByJob {
  jobId: string;
  jobName: string;
  printerName?: string;
  filamentName?: string;
  materialType?: string;
  filamentUsedGrams?: number;
  totalCostUsd: number;
  materialCostUsd: number;
  energyCostUsd: number;
  machineTimeCostUsd: number;
  laborCostUsd: number;
  printTimeSeconds?: number;
  completedAt?: string;
}

export interface CostOverTime {
  date: string;
  totalCostUsd: number;
  materialCostUsd: number;
  energyCostUsd: number;
  machineTimeCostUsd: number;
  laborCostUsd: number;
  jobCount: number;
}

// ============ Auto-Dispatch Dashboard Types ============

export interface ReadyGateCheck {
  name: string;
  passed: boolean;
  message: string;
  checkedAt: string;
}

export interface AutoDispatchDetailedStatus {
  printerId: string;
  printerName: string;
  enabled: boolean;
  isReady: boolean;
  currentJobName?: string;
  queueDepth: number;
  readyGateChecks: ReadyGateCheck[];
  lastActivity?: string;
  /** Auto-dispatch workflow state: "None", "PendingReady", or "Ready" */
  state: string;
  bedPreConfirmed?: boolean;
  attentionMessage?: string;
  attentionReason?: string;
  operatorAction?: string;
}

export interface AutoDispatchGlobalStatus {
  globalEnabled: boolean;
  printers: AutoDispatchDetailedStatus[];
}

// ============== Obico ML Server Management ==============

/**
 * Obico ML server configuration for failure detection
 */
export interface ObicoServer {
  id: string;
  name: string;
  url: string;
  isEnabled: boolean;
  hasApiKey: boolean;
  maxConcurrentAnalyses: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * Request to create a new Obico server
 */
export interface CreateObicoServerRequest {
  name: string;
  url: string;
  apiKey?: string;
  isEnabled?: boolean;
  maxConcurrentAnalyses?: number;
}

/**
 * Request to update an existing Obico server
 */
export interface UpdateObicoServerRequest {
  name?: string;
  url?: string;
  apiKey?: string | null;
  isEnabled?: boolean;
  maxConcurrentAnalyses?: number;
}

// ============ Obico ML Server Types ============

/**
 * Obico ML API server for AI-powered print failure detection.
 */
export interface ObicoServerDto {
  id: string;
  name: string;
  url: string;
  isEnabled: boolean;
  hasApiKey: boolean;
  maxConcurrentAnalyses: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * DTO for creating a new Obico ML server.
 */
export interface CreateObicoServerDto {
  name: string;
  url: string;
  apiKey?: string;
  isEnabled?: boolean;
  maxConcurrentAnalyses?: number;
}

/**
 * DTO for updating an existing Obico ML server.
 */
export interface UpdateObicoServerDto {
  name?: string;
  url?: string;
  apiKey?: string | null;
  isEnabled?: boolean;
  maxConcurrentAnalyses?: number;
}

/**
 * Health check result for an Obico server
 */
export interface ObicoServerHealthResponse {
  healthy: boolean;
  latencyMs: number;
  errorMessage?: string;
}

/**
 * SignalR event payload for print failure detection.
 * Broadcast when Obico ML detects a potential print failure.
 */
export interface FailureDetectionEvent {
  /** Persisted incident identifier when available */
  id?: string;
  /** Printer ID where failure was detected */
  printerId: string;
  /** Printer name for display */
  printerName: string;
  /** Job ID if available */
  jobId?: string;
  /** Active job display name when available */
  jobName?: string;
  /** Active print file name when available */
  fileName?: string;
  /** Confidence score from 0.0 to 1.0 */
  confidence: number;
  /** Detection timestamp (ISO 8601) */
  detectedAt: string;
  /** Snapshot URL used for the detection */
  snapshotUrl?: string;
  /** Whether the print was automatically paused */
  autoPaused: boolean;
}

export interface TimezoneInfo {
  id: string;
  displayName: string;
  offset: string;
}

// ── Slicer Profile DTOs ──────────────────────────────────────────────────

export interface MachineProfileDto {
  name: string;
  manufacturer: string;
  description?: string;
  printer_model?: string;
  printerVariant?: string;
  instantiation: boolean;
  inherits?: string;
  nozzleDiameter?: number;
  nozzleType?: string;
  buildVolumeX?: number;
  buildVolumeY?: number;
  buildVolumeZ?: number;
  printableArea?: string;
  maxPrintSpeed?: number;
  motionType?: string;
  gcodeDialect?: string;
  hasHeatedBed?: boolean;
  hasHeatedChamber?: boolean;
  maxBedTemperature?: number;
  maxHotendTemperature?: number;
  extruderCount: number;
  supportMultiMaterial?: boolean;
  retractionLength?: number;
  retractionSpeed?: number;
  retractionLiftZ?: number;
  detractionSpeed?: number;
  bedType?: string;
  bedShape?: string;
  startGcode?: string;
  endGcode?: string;
  maxAccelerationX?: number;
  maxAccelerationY?: number;
  maxFeedrateX?: number;
  maxFeedrateY?: number;
  settings: Record<string, unknown>;
}

export interface FilamentProfileDto {
  name: string;
  material: string;
  manufacturer?: string;
  description?: string;
  color?: string;
  compatible_printers: string[];
  instantiation: boolean;
  inherits?: string;
  nozzleTemperature: number;
  bedTemperature: number;
  firstLayerNozzleTemperature?: number;
  firstLayerBedTemperature?: number;
  chamberTemperature?: number;
  maxVolumetricSpeed?: number;
  flowRatio?: number;
  printSpeed: number;
  enablePressureAdvance?: boolean;
  pressureAdvance?: number;
  retractionLength?: number;
  retractionSpeed?: number;
  detractionSpeed?: number;
  enableFanCooling?: boolean;
  minFanSpeed?: number;
  maxFanSpeed?: number;
  bridgeFanSpeed?: number;
  density?: number;
  cost?: number;
  startGcode?: string;
  endGcode?: string;
  settings: Record<string, unknown>;
}

export interface ProcessProfileDto {
  name: string;
  quality: string;
  description?: string;
  compatible_printers: string[];
  instantiation: boolean;
  inherits?: string;
  layerHeight: number;
  firstLayerHeight: number;
  topLayers: number;
  bottomLayers: number;
  wallCount: number;
  infillPercentage: number;
  infillPattern?: string;
  printSpeed: number;
  firstLayerPrintSpeed: number;
  outerWallSpeed?: number;
  innerWallSpeed?: number;
  infillSpeed?: number;
  topSurfaceSpeed?: number;
  travelSpeed?: number;
  bedAdhesion?: string;
  supports: boolean;
  supportType?: string;
  supportDensity?: number;
  supportAngle?: number;
  seamPosition?: string;
  enableIroning?: boolean;
  nozzleTemp?: number;
  bedTemp?: number;
  firstLayerNozzleTemp?: number;
  firstLayerBedTemp?: number;
  retractionLength?: number;
  retractionSpeed?: number;
  lineWidthDefault?: number;
  lineWidthOuterWall?: number;
  lineWidthInnerWall?: number;
  defaultAcceleration?: number;
  outerWallAcceleration?: number;
  settings: Record<string, unknown>;
}

// ── Profile Schema Types (schema-driven settings editor) ─────────────────

export interface ProfileFieldMetadata {
  key: string;
  label: string;
  fieldType: 'number' | 'integer' | 'boolean' | 'string' | 'enum';
  category: string;
  description?: string;
  defaultValue?: unknown;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  options?: EnumOption[];
  isAdvanced: boolean;
}

export interface EnumOption {
  value: string;
  label: string;
}

export interface ProfileTypeSchema {
  profileType: string;
  categories: string[];
  fields: ProfileFieldMetadata[];
}

export interface ProfileSchemasResponse {
  process: ProfileTypeSchema;
  machine: ProfileTypeSchema;
  filament: ProfileTypeSchema;
}

// ── Print Quotas & User Balances ─────────────────────────────────────────

export type QuotaType = 'Cost' | 'Count' | 'Weight';
export type QuotaPeriodType = 'Daily' | 'Weekly' | 'Monthly' | 'Semester' | 'Manual';
export type BalanceTransactionType = 'Credit' | 'Debit' | 'Refund' | 'JobCharge';

export interface QuotaDto {
  id: string;
  userId: string | null;
  groupName: string | null;
  quotaType: QuotaType;
  limitAmount: number;
  usedAmount: number;
  periodType: QuotaPeriodType;
  resetAt: string | null;
  isActive: boolean;
  notes: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateQuotaRequest {
  userId?: string;
  groupName?: string;
  quotaType: QuotaType;
  limitAmount: number;
  periodType: QuotaPeriodType;
  isActive?: boolean;
  notes?: string;
}

export interface UpdateQuotaRequest {
  limitAmount?: number;
  periodType?: QuotaPeriodType;
  isActive?: boolean;
  notes?: string;
}

export interface CheckQuotaRequest {
  userId: string;
  estimatedCost?: number;
  jobCount?: number;
  estimatedWeightGrams?: number;
}

export interface QuotaCheckResult {
  allowed: boolean;
  deniedReason: string | null;
  quotaId: string | null;
}

export interface UserBalanceDto {
  id: string;
  userId: string;
  balanceAmount: number;
  currency: string;
  lastUpdated: string;
}

export interface BalanceTransactionDto {
  id: string;
  transactionType: BalanceTransactionType;
  amount: number;
  printJobId: string | null;
  description: string | null;
  performedBy: string | null;
  createdAt: string;
}

export interface BalanceAdjustRequest {
  amount: number;
  description?: string;
}
