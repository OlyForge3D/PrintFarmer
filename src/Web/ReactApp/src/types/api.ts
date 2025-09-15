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

export enum PrinterBackend {
  Moonraker = 0,
  PrusaLink = 1,
  SDCP = 2
}

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
}

export interface ManufacturerDto {
  id: string;
  name: string;
}

export interface PrinterModelDto {
  id: string;
  name: string;
  manufacturerId: string;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackend;
  supportedFilamentTypes?: string[];
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
  modelMaxX?: number;
  modelMaxY?: number;
  modelMaxZ?: number;
  dateAcquired?: Date;
  backend: PrinterBackend;
  apiKey?: string;
  originalServerUrl?: string;
  ipAddress?: string;
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
  status: string;            // Healthy | Degraded | Unhealthy | etc.
  duration?: string;         // e.g. 00:00:00.0423123
  description?: string;      // optional description
  data?: Record<string, unknown>; // additional payload from health check
}

export interface DetailedHealthStatus {
  kind: 'detailed';
  status: string;                 // Overall status
  totalChecksDuration: string;    // Overall duration
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

export interface UpdateModelRequest {
  name: string;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackend;
  supportedFilamentTypeIds?: string[];
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
  Starting = 0,
  Running = 1,
  Completed = 2,
  Failed = 3,
  Cancelled = 4
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
  filesProcessed: number;
  filesAdded: number;
  filesSkipped: number;
  filesErrored: number;
  duplicatesSkipped: number;
  totalSizeBytes: number;
  startedAt: Date;
  completedAt?: Date;
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