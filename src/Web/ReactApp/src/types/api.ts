// Mirror existing shared models from Farm.Web.Shared

export interface Printer {
  id: string;
  name: string;
  serverUrl: string;
  notes?: string;
  isOnline: boolean;
  isReachable: boolean;
  state?: string;
  manufacturerName?: string;
  modelName?: string;
  progress?: number;
  jobName?: string;
  thumbnailUrl?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  x?: number;
  y?: number;
  z?: number;
  hotendTemp?: number;
  bedTemp?: number;
  hotendTarget?: number;
  bedTarget?: number;
  backend: PrinterBackend;
  apiKey?: string;
  originalServerUrl?: string;
  ipAddress?: string;
  spoolInfo?: PrinterSpoolInfo;
}

export interface PrinterCameraUrls {
  id: string;
  name: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
}

export interface PrinterFast {
  id: string;
  name: string;
  serverUrl: string;
  notes?: string;
  isOnline: boolean;
  state?: string;
  manufacturerName?: string;
  modelName?: string;
  backend: PrinterBackend;
  apiKey?: string;
  originalServerUrl?: string;
  ipAddress?: string;
}

export enum PrinterBackend {
  Moonraker = 0,
  PrusaLink = 1,
  SDCP = 2
}

export enum MotionType {
  Cartesian = 0,
  CoreXY = 1,
  Delta = 2,
  Unknown = 99
}

// String enum types for API responses (enums are serialized as strings)
export type PrinterBackendString = 'Moonraker' | 'PrusaLink' | 'SDCP';
export type MotionTypeString = 'Cartesian' | 'CoreXY' | 'Delta' | 'Unknown';

export interface PrinterSpoolInfo {
  id?: number;
  filament?: FilamentInfo;
  used_length?: number;
  location?: string;
  lot_nr?: string;
  first_used?: string;
  last_used?: string;
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

// Basic printer info without live status
export interface PrinterBasic {
  id: string;
  name: string;
  serverUrl: string;
  notes?: string;
  manufacturerName?: string;
  modelName?: string;
  backend: PrinterBackend;
  apiKey?: string;
  originalServerUrl?: string;
  ipAddress?: string;
}

// Live status info
export interface PrinterStatus {
  id: string;
  isOnline: boolean;
  state?: string;
  progress?: number;
  jobName?: string;
  thumbnailUrl?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  x?: number;
  y?: number;
  z?: number;
  hotendTemp?: number;
  bedTemp?: number;
  hotendTarget?: number;
  bedTarget?: number;
  spoolInfo?: PrinterSpoolInfo;
}

// Real-time update payload for SignalR
export interface PrinterStatusUpdate {
  id: string;
  isOnline: boolean;
  state?: string;
  progress?: number;
  jobName?: string;
  thumbnailUrl?: string;
  cameraStreamUrl?: string;
  cameraSnapshotUrl?: string;
  x?: number;
  y?: number;
  z?: number;
  hotendTemp?: number;
  bedTemp?: number;
  hotendTarget?: number;
  bedTarget?: number;
  homedAxes?: string;
  spoolInfo?: PrinterSpoolInfo;
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
}

export interface UpdatePrinterDto {
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
  // Printer capabilities
  nozzleDiameter?: number;
  supportedMaterials?: string[];
  maxBuildVolumeX?: number;
  maxBuildVolumeY?: number;
  maxBuildVolumeZ?: number;
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  multiMaterial?: boolean;
  numberOfExtruders?: number;
  minHotendTemp?: number;
  maxHotendTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  supportsAutoLeveling?: boolean;
  maxPrintSpeed?: number;
}

export interface ManufacturerDto {
  id: string;
  name: string;
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
  
  // Capability properties
  defaultNozzleDiameter?: number;
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  multiMaterial?: boolean;
  numberOfExtruders?: number;
  supportsAutoLeveling?: boolean;
  minHotendTemp?: number;
  maxHotendTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  maxPrintSpeed?: number;
}

// Printer capabilities interface
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
  numberOfExtruders: number;
  minHotendTemp?: number;
  maxHotendTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  currentMaterial?: string;
  currentSpoolId?: number;
  isAvailable: boolean;
  supportsAutoLeveling: boolean;
  maxPrintSpeed?: number;
  lastUpdated: Date;
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
  originalServerUrl?: string;
  ipAddress?: string;
  capabilities?: PrinterCapabilitiesDto;
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
  kind: 'basic';
  status: string; // "ok"
}

// Detailed health (/health, /api/health) produced by ASP.NET Core health checks writer
// Property names are camelCased by System.Text.Json (see Program.HealthJsonOptions)
export interface DetailedHealthStatusEntry {
  status: string;
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
  kind: 'detailed';
  status: string;                 // Overall status
  totalChecksDuration: string;    // Overall duration
  startup?: StartupStatus;        // Startup initialization status
  results: Record<string, DetailedHealthStatusEntry>;
}

// Union used by hooks/components; runtime narrowing via 'kind'
export type HealthStatus = BasicHealthStatus | DetailedHealthStatus;

// Runtime type guard helpers
export function isDetailedHealthStatus(h: HealthStatus | undefined | null): h is DetailedHealthStatus {
  if (!h || h.kind !== 'detailed') return false;
  const candidate: unknown = (h as unknown as { results?: unknown }).results;
  return typeof candidate === 'object' && candidate !== null;
}

export function isBasicHealthStatus(h: HealthStatus | undefined | null): h is BasicHealthStatus {
  return !!h && h.kind === 'basic';
}

export interface FilamentTypeDto {
  id: string;
  name: string;
  defaultTemperatures: TempTargets;
}

export interface CreateFilamentTypeRequest {
  name: string;
  defaultTemperatures: TempTargets;
}

export interface UpdateFilamentTypeRequest {
  name: string;
  defaultTemperatures: TempTargets;
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

export interface UpdateModelRequest {
  name: string;
  motionType?: MotionTypeString;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackendString;
  supportedFilamentTypeIds?: string[];
  
  // Capability properties
  defaultNozzleDiameter?: number;
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  multiMaterial?: boolean;
  numberOfExtruders?: number;
  supportsAutoLeveling?: boolean;
  minHotendTemp?: number;
  maxHotendTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  maxPrintSpeed?: number;
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
  
  // Capability properties
  defaultNozzleDiameter?: number;
  hasHeatedBed?: boolean;
  hasEnclosure?: boolean;
  multiMaterial?: boolean;
  numberOfExtruders?: number;
  supportsAutoLeveling?: boolean;
  minHotendTemp?: number;
  maxHotendTemp?: number;
  minBedTemp?: number;
  maxBedTemp?: number;
  maxPrintSpeed?: number;
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
  Harvest = 1
}

export interface GcodeFile {
  id: string;
  originalFileName: string;
  displayName: string;
  fileSizeBytes: number;
  uploadedAt: Date;
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
  Running = 'Running',
  Completed = 'Completed',
  Failed = 'Failed',
  Cancelled = 'Cancelled'
}

export interface HarvestOptions {
  includeSubfolders: boolean;
  fileTypes: string[];
  minFileSize: number;
  maxFileAge?: number;
  duplicateHandling: 'skip' | 'overwrite' | 'rename';
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
  options?: HarvestOptions;
  filesPaths?: string[];
}

export interface HarvestProgress {
  operationId: string;
  filesProcessed: number;
  filesFound: number;
  currentFile?: string;
  phase: 'discovering' | 'processing' | 'completing';
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
  phase?: 'discovering' | 'processing' | 'completing';
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

export interface GcodeFile {
  id: string;
  path: string;
  name: string;
  size: number;
  modifiedAt: Date;
  isDirectory: boolean;
  harvestOperationId?: string;
}

export interface GetGcodeFilesRequest {
  path?: string;
  harvestId?: string;
  printerId?: string;
  sortBy?: 'name' | 'size' | 'date';
  sortOrder?: 'asc' | 'desc';
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
  Cancelled = 4
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
  createdAt: Date;
  updatedAt: Date;
}

// API response types
export interface ApiResponse<T> {
  data: T;
  success: boolean;
  message?: string;
}

export interface PagedResponse<T> {
  data: T[];
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
  username: string;
  password: string;
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

export interface DiscoveredPrinterDto {
  ipAddress: string;
  port: number;
  serverUrl: string;
  backend: PrinterBackend;
  name: string;
  manufacturer?: string;
  model?: string;
  firmware?: string;
  version?: string;
}

// Discovery streaming types
export enum DiscoveryStatus {
  Starting = 'Starting',
  Scanning = 'Scanning',
  Completed = 'Completed',
  Cancelled = 'Cancelled',
  Error = 'Error'
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
}

// Failure detail for an individual file during multi-upload.
export interface MultiUploadFailure {
  fileName: string;
  error: string;
}

// Response for multi-file upload endpoint.
export interface MultiUploadResponse {
  created: GcodeFile[];
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