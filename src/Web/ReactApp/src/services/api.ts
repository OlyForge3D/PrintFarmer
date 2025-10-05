  // Get hash for a G-code file (returns string)
import {
  ApiError,
  PrintJobStatusDto,
  AuthenticationResult,
  CommandResult,
  CreateFilamentTypeRequest,
  CreatePrinterDto,
  DiscoveredPrinterDto,
  FilamentPresets,
  FilamentTypeDto,
  GcodeFile,
  GcodeHarvestOperation,
  GetGcodeFilesResponse,
  HealthStatus,
  HistoryJob,
  HistoryListResponse,
  HistoryTotals,
  JobQueuePrintJob,
  LoginRequest,
  ManufacturerDto,
  MoveRequest,
  MultiUploadResponse,
  Printer,
  PrinterCameraUrls,
  PrinterCapabilitiesDto,
  PrinterDetails,
  PrinterFast,
  PrinterModelDto,
  RegisterRequest,
  ResolveHostnameRequest,
  ResolveHostnameResponse,
  SpoolmanDiscoveryResult,
  SpoolmanFilamentImportResult,
  TempTargets,
  UpdateFilamentTypeRequest,
  UpdateModelRequest,
  UpdatePrinterDto,
  UserDto,
  DiscoveredGcodeFileDto,
  GcodeHarvestResultDto
} from '@/types/api';
import type { AxiosError, AxiosInstance, AxiosRequestConfig } from 'axios';
import axios from 'axios';

export class ApiClient {
  // Utility to generate a correlation ID (UUID v4)
  private static generateCorrelationId(): string {
    // Use crypto API if available, fallback to random
    if (typeof crypto !== 'undefined' && crypto.randomUUID) {
      return crypto.randomUUID();
    }
    // Fallback: simple random string
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
      const r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
      return v.toString(16);
    });
  }
  // ============ Generic Settings API methods ============
  /**
   * Get settings for any settings class by class name
   */
  async getSettings<T = Record<string, unknown>>(className: string): Promise<T> {
    const res = await this.client.get(`/settings/${className}`);
    return res.data;
  }

  /**
   * Save settings for any settings class by class name
   */
  async saveSettings<T = Record<string, unknown>>(className: string, settings: T): Promise<void> {
    await this.client.post(`/settings/${className}`, settings);
  }

  /**
   * Get all settings metadata for dynamic UI generation
   */
  async getSettingsMetadata(): Promise<Array<Record<string, unknown>>> {
    const res = await this.client.get('/settings/metadata');
    return res.data;
  }

  /**
   * Get all unified settings
   */
  async getAllSettings(): Promise<Record<string, unknown>> {
    const res = await this.client.get('/settings');
    return res.data;
  }

  /**
   * Save all unified settings
   */
  async saveAllSettings(settings: Record<string, unknown>): Promise<void> {
    await this.client.post('/settings', settings);
  }


  private client: AxiosInstance;

  constructor() {
    // Use environment variable for API base URL, fallback to relative path for monolithic deployment
    // If a full origin is provided (e.g., http://localhost:5245), ensure it includes the '/api' prefix.
    const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
    const apiBaseUrl = (() => {
      if (!rawBase || rawBase.trim() === '') return '/api';
      const trimmed = rawBase.replace(/\/$/, ''); // drop trailing slash
      // If it already ends with '/api' or contains '/api/' path segment, keep as-is
      if (/\/(api)(\/|$)/.test(trimmed)) return trimmed;
      return `${trimmed}/api`;
    })();

    this.client = axios.create({
      baseURL: apiBaseUrl,
      timeout: 30000,
      headers: {
        'Content-Type': 'application/json',
      },
    });

    // Request interceptor for authentication and correlationId
    this.client.interceptors.request.use((config) => {
      const token = localStorage.getItem('auth-token');
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      // Add correlationId header to every request
      config.headers['X-Correlation-Id'] = ApiClient.generateCorrelationId();
      return config;
    });

    // Response interceptor for error handling
    this.client.interceptors.response.use(
      (response) => response,
      (error: AxiosError) => {
        const apiError: ApiError = {
          message: error.message,
          statusCode: error.response?.status || 500,
          details: error.response?.data as string || undefined,
        };
        return Promise.reject(apiError);
      }
    );
  }

  // ===== Harvest/discovered file API methods =====
  // Get discovered G-code files for a harvest operation
  async getDiscoveredGcodeFiles(harvestOperationId: string): Promise<DiscoveredGcodeFileDto[]> {
  const resp = await this.client.get<DiscoveredGcodeFileDto[]>(`/gcode-harvest/operations/${harvestOperationId}/files`);
  return resp.data;
  }

  // Import selected discovered G-code files
  async importSelectedGcodeFiles(dto: { harvestOperationId: string; fileIds: string[] }): Promise<GcodeHarvestResultDto> {
    const resp = await this.client.post<GcodeHarvestResultDto>(`/harvest/import-selected`, dto);
    return resp.data;
  }

  // Skip a discovered G-code file
  async skipDiscoveredGcodeFile(fileId: string): Promise<DiscoveredGcodeFileDto> {
    const resp = await this.client.post<DiscoveredGcodeFileDto>(`/harvest/discovered-files/${fileId}/skip`, {});
    return resp.data;
  }

  // Retry a discovered G-code file
  async retryDiscoveredGcodeFile(fileId: string): Promise<DiscoveredGcodeFileDto> {
    const resp = await this.client.post<DiscoveredGcodeFileDto>(`/harvest/discovered-files/${fileId}/retry`, {});
    return resp.data;
  }

  // Get hash for a G-code file (returns string)
  async getGcodeFileHash(path: string, algorithm: 'sha256' | 'sha1' = 'sha256'): Promise<string> {
    const resp = await this.client.get<{ hash: string }>(`/gcode-files/hash`, { params: { path, algorithm } });
    return resp.data.hash;
  }

  // ============ Printer API methods ============

  async getPrinters(): Promise<Printer[]> {
    // Use fast summary endpoint for lighter list retrieval
    const response = await this.client.get<PrinterFast[]>('/printers/fast');
    // Cast to Printer[] for compatibility; fast objects are subset of Printer
    return response.data as unknown as Printer[];
  }

  async getPrintersFast(): Promise<PrinterFast[]> {
    const response = await this.client.get<PrinterFast[]>('/printers/fast');
    return response.data;
  }

  async getPrinterCameraUrls(): Promise<PrinterCameraUrls[]> {
    const response = await this.client.get<PrinterCameraUrls[]>('/printers/camera-urls');
    return response.data;
  }

  async getPrinter(id: string): Promise<Printer> {
    const response = await this.client.get<Printer>(`/printers/${id}`);
    return response.data;
  }

  async getPrinterDetails(id: string): Promise<PrinterDetails> {
    const response = await this.client.get<PrinterDetails>(`/printers/${id}/details`);
    return response.data;
  }

  async createPrinter(printer: CreatePrinterDto): Promise<Printer> {
    const response = await this.client.post<Printer>('/printers', printer);
    return response.data;
  }

  async updatePrinter(id: string, printer: UpdatePrinterDto): Promise<Printer> {
    const response = await this.client.put<Printer>(`/printers/${id}`, printer);
    return response.data;
  }

  async deletePrinter(id: string): Promise<void> {
    await this.client.delete(`/printers/${id}`);
  }

  async discoverPrinters(): Promise<DiscoveredPrinterDto[]> {
    const response = await this.client.get<DiscoveredPrinterDto[]>('/printers/discover');
    return response.data;
  }

  async startDiscoveryStream(): Promise<{ sessionId: string; message: string }> {
    const response = await this.client.post<{ sessionId: string; message: string }>('/printers/discover/stream');
    return response.data;
  }

  // ============ Printer Control API methods ============

  async setTemperatures(printerId: string, targets: TempTargets): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/temps`, targets);
    return response.data;
  }

  async movePrinter(printerId: string, move: MoveRequest): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/move`, move);
    return response.data;
  }

  async movePrinterTo(printerId: string, position: MoveRequest): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/moveto`, position);
    return response.data;
  }

  async homePrinter(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/home`);
    return response.data;
  }

  async homeXY(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/homexy`);
    return response.data;
  }

  async homeZ(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/homez`);
    return response.data;
  }

  async pausePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/pause`);
    return response.data;
  }

  async resumePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/resume`);
    return response.data;
  }

  async emergencyStop(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/emergency-stop`);
    return response.data;
  }

  async firmwareRestart(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/firmware-restart`);
    return response.data;
  }

  // ============ Printer History API methods ============

  async getPrinterHistory(
    printerId: string, 
    options?: {
      limit?: number;
      start?: number;
      since?: Date;
      before?: Date;
      order?: string;
    }
  ): Promise<HistoryListResponse> {
    const params: Record<string, string | number> = {};
    if (options?.limit) params.limit = options.limit;
    if (options?.start) params.start = options.start;
    if (options?.since) params.since = options.since.toISOString();
    if (options?.before) params.before = options.before.toISOString();
    if (options?.order) params.order = options.order;

    const response = await this.client.get<HistoryListResponse>(`/printers/${printerId}/history`, { params });
    return response.data;
  }

  async getPrinterHistoryJob(printerId: string, jobId: string): Promise<HistoryJob> {
    const response = await this.client.get<HistoryJob>(`/printers/${printerId}/history/${jobId}`);
    return response.data;
  }

  async getPrinterHistoryTotals(printerId: string): Promise<HistoryTotals> {
    const response = await this.client.get<HistoryTotals>(`/printers/${printerId}/history/totals`);
    return response.data;
  }

  // ============ Catalog API methods ============

  async getManufacturers(): Promise<ManufacturerDto[]> {
    const response = await this.client.get<ManufacturerDto[]>('/catalog/manufacturers');
    return response.data;
  }

  async createManufacturer(name: string): Promise<ManufacturerDto> {
    const response = await this.client.post<ManufacturerDto>('/catalog/manufacturers', { name });
    return response.data;
  }

  async updateManufacturer(id: string, name: string): Promise<ManufacturerDto> {
    const response = await this.client.put<ManufacturerDto>(`/catalog/manufacturers/${id}`, { name });
    return response.data;
  }

  async deleteManufacturer(id: string): Promise<void> {
    await this.client.delete(`/catalog/manufacturers/${id}`);
  }

  async getModels(manufacturerId?: string): Promise<PrinterModelDto[]> {
    const params = manufacturerId ? { manufacturerId } : {};
    const response = await this.client.get<PrinterModelDto[]>('/catalog/printer-models', { params });
    return response.data;
  }

  async createModel(model: Omit<PrinterModelDto, 'id'>): Promise<PrinterModelDto> {
    const response = await this.client.post<PrinterModelDto>('/catalog/printer-models', model);
    return response.data;
  }

  async updateModel(id: string, request: UpdateModelRequest): Promise<PrinterModelDto> {
    const response = await this.client.put<PrinterModelDto>(`/catalog/printer-models/${id}`, request);
    return response.data;
  }

  // Legacy method for simple name updates
  async updateModelName(id: string, name: string): Promise<PrinterModelDto> {
    return this.updateModel(id, { name });
  }

  // Delete a model by id
  async deleteModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/printer-models/${id}`);
  }

  // Get default capabilities for a printer model
  async getModelDefaultCapabilities(modelId: string): Promise<PrinterCapabilitiesDto | null> {
    try {
      const response = await this.client.get<PrinterCapabilitiesDto>(`/printers/model/${modelId}/default-capabilities`);
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 204) {
        return null; // No default capabilities available
      }
      throw error;
    }
  }

  // ============ File type API methods ============



  // ============ Filament Type API methods ============

  async getFilamentTypes(): Promise<FilamentTypeDto[]> {
    const response = await this.client.get<FilamentTypeDto[]>('/filamenttype');
    return response.data;
  }

  async createFilamentType(filamentType: CreateFilamentTypeRequest): Promise<FilamentTypeDto> {
    const response = await this.client.post<FilamentTypeDto>('/filamenttype', filamentType);
    return response.data;
  }

  async updateFilamentType(id: string, filamentType: UpdateFilamentTypeRequest): Promise<void> {
    await this.client.put(`/filamenttype/${id}`, filamentType);
  }

  async deleteFilamentType(id: string): Promise<void> {
    await this.client.delete(`/filamenttype/${id}`);
  }

  async getFilamentPresets(): Promise<FilamentPresets> {
    const response = await this.client.get<{ presets: FilamentPresets }>('/filamenttype/presets');
    return response.data.presets;
  }

  async saveFilamentPresets(presets: FilamentPresets): Promise<void> {
    await this.client.post('/filamenttype/presets', { presets });
  }

  async importFilamentTypesFromSpoolman(): Promise<SpoolmanFilamentImportResult> {
    const response = await this.client.post<SpoolmanFilamentImportResult>('/filamenttype/import-from-spoolman');
    return response.data;
  }

  async scanNetworkForSpoolman(): Promise<SpoolmanDiscoveryResult[]> {
    const response = await this.client.post<SpoolmanDiscoveryResult[]>('/spoolman/scan-network');
    return response.data;
  }

  // ============ Network utilities ============

  async resolveHostname(request: ResolveHostnameRequest): Promise<ResolveHostnameResponse> {
    const response = await this.client.post<ResolveHostnameResponse>('/resolve-hostname', request);
    return response.data;
  }

  // ============ G-code library methods ============

  async getGcodeFiles(page = 1, pageSize = 50): Promise<GcodeFile[]> {
    const response = await this.client.get<GcodeFile[]>('/gcode-files', {
      params: { page, pageSize }
    });
    return response.data;
  }

  async getGcodeFile(id: string): Promise<GcodeFile> {
    const response = await this.client.get<GcodeFile>(`/gcode-files/${id}`);
    return response.data;
  }

  async uploadGcodeFile(file: File, description?: string, tags?: string[]): Promise<GcodeFile> {
    const formData = new FormData();
    formData.append('file', file);
    if (description) formData.append('description', description);
    if (tags) formData.append('tags', JSON.stringify(tags));

    const response = await this.client.post<GcodeFile>('/gcode-files/upload', formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
    return response.data;
  }

  async deleteGcodeFile(id: string): Promise<void> {
    await this.client.delete(`/gcode-files/${id}`);
  }

  // ============ G-code harvest operations ============

  async startHarvestOperation(printerId: string, opts?: { includeSubdirectories?: boolean; maxFileSizeBytes?: number; modifiedAfter?: Date | string; fileExtensions?: string[]; minFileSizeBytes?: number; duplicateHandling?: string }): Promise<{ operationId: string }> {
    const payload = {
      printerId,
      includeSubdirectories: opts?.includeSubdirectories ?? true,
      maxFileSizeBytes: opts?.maxFileSizeBytes ?? 100 * 1024 * 1024,
      modifiedAfter: opts?.modifiedAfter ? (typeof opts.modifiedAfter === 'string' ? opts.modifiedAfter : opts.modifiedAfter.toISOString()) : undefined,
      fileExtensions: opts?.fileExtensions,
      minFileSizeBytes: opts?.minFileSizeBytes,
      duplicateHandling: opts?.duplicateHandling
    };
    const response = await this.client.post('/gcode-harvest/start', payload);
    return response.data as { operationId: string };
  }

  async startBulkHarvest(printerIds: string[], options: { includeSubfolders?: boolean; maxFileAge?: number; fileTypes?: string[]; minFileSize?: number; duplicateHandling?: string } = {}): Promise<{ operationIds: string[] }> {
    const modifiedAfter = options.maxFileAge ? new Date(Date.now() - options.maxFileAge) : undefined;
    const results = await Promise.all(printerIds.map(pid => this.startHarvestOperation(pid, {
      includeSubdirectories: options?.includeSubfolders ?? true,
      modifiedAfter,
      fileExtensions: options.fileTypes,
      minFileSizeBytes: options.minFileSize,
      duplicateHandling: options.duplicateHandling,
    }).catch(err => {
      console.error('Failed to start harvest for printer', pid, err);
      return null;
    })));
    return { operationIds: results.filter(r => r !== null).map(r => (r as { operationId: string }).operationId) };
  }

  async getHarvestOperations(printerId?: string, status?: string, limit?: number, offset?: number): Promise<GcodeHarvestOperation[]> {
    const params: Record<string, string | number> = {};
    if (printerId) params.printerId = printerId;
    if (status) params.status = status;
    if (limit) params.limit = limit;
    if (offset) params.offset = offset;
    
    const response = await this.client.get<GcodeHarvestOperation[]>('/gcode-harvest/operations', { params });
    return response.data;
  }

  async getHarvestOperation(id: string): Promise<GcodeHarvestOperation> {
    const response = await this.client.get<GcodeHarvestOperation>(`/gcode-harvest/operations/${id}`);
    return response.data;
  }


  async cancelHarvestOperation(operationId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(`/gcode-harvest/operations/${operationId}/cancel`);
    return response.data;
  }

  /**
   * Skip a file in a harvest operation (mark as skipped and emit update)
   * @param operationId The harvest operation ID
   * @param fileId The file ID to skip
   * @returns Promise<boolean> indicating success
   */
  async skipHarvestFile(operationId: string, fileId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(`/gcode-harvest/operations/${operationId}/files/${fileId}/skip`);
    return response.data === true;
  }

  /**
   * Retry a file in a harvest operation (reset error and reprocess)
   * @param operationId The harvest operation ID
   * @param fileId The file ID to retry
   * @returns Promise<boolean> indicating success
   */
  async retryHarvestFile(operationId: string, fileId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(`/gcode-harvest/operations/${operationId}/files/${fileId}/retry`);
    return response.data === true;
  }

  async getGcodeFilesWithFilter(request: Record<string, unknown>): Promise<GetGcodeFilesResponse> {
    const response = await this.client.get<GetGcodeFilesResponse>('/gcode-files', { params: request });
    return response.data;
  }

  async deleteGcodeFiles(filePaths: string[]): Promise<void> {
    await this.client.delete('/gcode-files', { data: { filePaths } });
  }

  async downloadGcodeFile(filePath: string): Promise<void> {
    const response = await this.client.get(`/gcode-files/download`, {
      params: { path: filePath },
      responseType: 'blob'
    });

    // Create a download link
    const url = window.URL.createObjectURL(new Blob([response.data]));
    const link = document.createElement('a');
    link.href = url;
    link.setAttribute('download', filePath.split('/').pop() || 'file.gcode');
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.URL.revokeObjectURL(url);
  }

  async uploadGcodeLibraryFile(file: File, virtualPath = '/'): Promise<void> {
    const form = new FormData();
    form.append('file', file);
    await this.client.post(`/gcode-files/upload`, form, {
      params: { path: virtualPath },
      headers: { 'Content-Type': 'multipart/form-data' }
    });
  }

  async uploadMultipleGcodeLibraryFiles(files: File[], virtualPath = '/'): Promise<MultiUploadResponse> {
    const form = new FormData();
    files.forEach(f => form.append('files', f));
    const resp = await this.client.post<MultiUploadResponse>(`/gcode-files/upload-multiple`, form, {
      params: { path: virtualPath },
      headers: { 'Content-Type': 'multipart/form-data' }
    });
    return resp.data as MultiUploadResponse;
  }

  async getGcodeUploadSettings(): Promise<import('@/types/api').GcodeUploadSettings> {
    const resp = await this.client.get('/gcode-files/settings');
    return resp.data as import('@/types/api').GcodeUploadSettings;
  }

  async updateGcodeUploadSettings(allowedExtensions: string[]): Promise<void> {
    await this.client.put('/gcode-files/settings', { allowedExtensions });
  }

  async moveGcodePath(sourcePath: string, destinationPath: string, overwrite = false): Promise<{ path: string; isDirectory: boolean; }> {
    const resp = await this.client.post('/gcode-files/move', { sourcePath, destinationPath, overwrite });
    return resp.data as { path: string; isDirectory: boolean; };
  }


  // ============ Job Queue methods ============

  async getJobQueue(printerId?: string): Promise<JobQueuePrintJob[]> {
    const params = printerId ? { printerId } : {};
    const response = await this.client.get<JobQueuePrintJob[]>('/job-queue', { params });
    return response.data;
  }

  async queuePrintJob(printerId: string, gcodeFileId: string, priority = 0): Promise<JobQueuePrintJob> {
    const response = await this.client.post<JobQueuePrintJob>('/job-queue', {
      printerId,
      gcodeFileId,
      priority
    });
    return response.data;
  }

  async cancelJob(jobId: string): Promise<void> {
    await this.client.patch(`/job-queue/${jobId}/cancel`);
  }

  async deleteJob(jobId: string): Promise<void> {
    await this.client.delete(`/job-queue/${jobId}`);
  }

  // ============ Printer file operations ============

  async uploadGcodeToPrinter(printerId: string, file: File): Promise<boolean> {
    const formData = new FormData();
    formData.append('file', file);

    const response = await this.client.post<{ success: boolean }>(`/printers/${printerId}/upload-gcode`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
    return response.data.success;
  }

  async startPrintFromFile(printerId: string, fileName: string): Promise<boolean> {
    const response = await this.client.post<{ success: boolean }>(`/printers/${printerId}/start-print`, {
      fileName
    });
    return response.data.success;
  }

  // ============ Health checks ============

  async getHealthStatus(): Promise<HealthStatus> {
    const response = await this.client.get<HealthStatus>('/health');
    return response.data as HealthStatus;
  }

  async getBasicHealth(): Promise<{ status: string }> {
    const response = await this.client.get<{ status: string }>('/healthz');
    return response.data;
  }

  // ============ Authentication API methods ============

  async login(credentials: LoginRequest): Promise<AuthenticationResult> {
    const response = await this.client.post<AuthenticationResult>('/auth/login', credentials);
    return response.data;
  }

  async register(userData: RegisterRequest): Promise<AuthenticationResult> {
    const response = await this.client.post<AuthenticationResult>('/auth/register', userData);
    return response.data;
  }

  async getCurrentUser(): Promise<UserDto> {
    const response = await this.client.get<UserDto>('/auth/me');
    return response.data;
  }

  async logout(): Promise<void> {
    await this.client.post('/auth/logout');
  }

  // ============ Generic request method ============

  async request<T>(config: AxiosRequestConfig): Promise<T> {
    const response = await this.client.request<T>(config);
    return response.data;
  }
  // Get print job status for Moonraker printers
  async getPrintJobStatus(printerId: string): Promise<PrintJobStatusDto | null> {
    try {
      const response = await this.client.get<PrintJobStatusDto>(`/printers/${printerId}/printjob`);
      return response.data;
    } catch {
      return null;
    }
  }
}

// Export singleton instance
export const apiClient = new ApiClient();