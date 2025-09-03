// Mirror existing shared models from Farm.Web.Shared

export interface Printer {
  id: string;
  name: string;
  serverUrl: string;
  notes?: string;
  isOnline: boolean;
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

export interface ModelDto {
  id: string;
  name: string;
  manufacturerId: string;
  maxX?: number;
  maxY?: number;
  maxZ?: number;
  defaultBackend?: PrinterBackend;
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

// Filament temperature presets
export interface FilamentPresets {
  abs: TempTargets;
  asa: TempTargets;
  pla: TempTargets;
  pc: TempTargets;
  pctg: TempTargets;
  petg: TempTargets;
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
  Running = 0,
  Completed = 1,
  Failed = 2,
  Cancelled = 3
}

export interface GcodeHarvestOperation {
  id: string;
  printerId: string;
  status: GcodeHarvestStatus;
  filesFound: number;
  filesProcessed: number;
  startedAt: Date;
  completedAt?: Date;
  error?: string;
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